using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using StreamingMesh.Core.Serialization;

namespace StreamingMesh
{
  [ExecuteInEditMode]
  [RequireComponent(typeof(STMHttpSerializer)), RequireComponent(typeof(STMAudioRecorder))]
  public sealed class STMHttpSender : MonoBehaviour
  {
    [StructLayout(LayoutKind.Sequential)]
    struct TiledVertex
    {
      public uint tileID;
      public uint polyIndex;
      public uint x;
      public uint y;
      public uint z;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FragmentVertex
    {
      public uint x;
      public uint y;
      public uint z;
    }

    sealed class EncoderBuffers : IDisposable
    {
      public readonly Mesh bakedMesh = new Mesh();
      public readonly List<Vector3> vertices = new List<Vector3>();
      public ComputeBuffer source;
      public ComputeBuffer previous;
      public ComputeBuffer tiled;
      public ComputeBuffer delta;
      public TiledVertex[] tiledReadback;
      public FragmentVertex[] deltaReadback;
      public int vertexCount;
      public int inFlightReadbacks;

      public bool EnsureCapacity(int count)
      {
        if (count == vertexCount && source != null)
          return false;

        ReleaseBuffers();
        vertexCount = count;
        if (count <= 0)
          return true;

        source = new ComputeBuffer(count, sizeof(float) * 3);
        previous = new ComputeBuffer(count, sizeof(float) * 3);
        tiled = new ComputeBuffer(count, sizeof(uint) * 5);
        delta = new ComputeBuffer(count, sizeof(uint) * 3);
        tiledReadback = new TiledVertex[count];
        deltaReadback = new FragmentVertex[count];
        return true;
      }

      void ReleaseBuffers()
      {
        Release(ref source);
        Release(ref previous);
        Release(ref tiled);
        Release(ref delta);
        tiledReadback = null;
        deltaReadback = null;
      }

      static void Release(ref ComputeBuffer buffer)
      {
        if (buffer == null)
          return;
        buffer.Release();
        buffer = null;
      }

      public void Dispose()
      {
        ReleaseBuffers();
        if (Application.isPlaying)
          UnityEngine.Object.Destroy(bakedMesh);
        else
          UnityEngine.Object.DestroyImmediate(bakedMesh);
      }
    }

    sealed class PendingEncodeFrame
    {
      public readonly uint sequence;
      public readonly bool isKeyframe;
      public readonly long ptsTicks;
      public readonly Vector3 rootPosition;
      public readonly TiledVertex[][] tiledVertices;
      public readonly FragmentVertex[][] deltaVertices;
      public int pendingReadbacks;
      public bool schedulingComplete;
      public bool hasError;

      public PendingEncodeFrame(
        uint frameSequence,
        bool keyframe,
        long presentationTicks,
        Vector3 position,
        int meshCount)
      {
        sequence = frameSequence;
        isKeyframe = keyframe;
        ptsTicks = presentationTicks;
        rootPosition = position;
        tiledVertices = keyframe ? new TiledVertex[meshCount][] : null;
        deltaVertices = keyframe ? null : new FragmentVertex[meshCount][];
      }

      public bool IsComplete { get { return schedulingComplete && pendingReadbacks == 0; } }
    }

    const int ThreadGroupSize = 128;
    const int MaxMeshCount = 256;
    const int MaxVertexCount = 65536;

    [Header("Source")]
    public GameObject targetGameObject;

    [Header("Stream encoding")]
    [Min(1)] public int containerSize = 4;
    [Range(2, 254)] public int packageSize = 128;
    [Tooltip("Mesh capture rate in frames per second.")]
    [Min(0.1f)] public float frameRate = 10.0f;
    // Kept only to migrate scenes created before frame rate was exposed as FPS.
    [SerializeField, HideInInspector] float frameInterval = 0.1f;
    [SerializeField, HideInInspector] bool frameRateMigrated;
    [Min(0)] public int subframesPerKeyframe = 4;
    [Min(1)] public int combinedFrames = 100;
    [Tooltip("Maximum encoded frames waiting for GPU readback. Capture pauses when this limit is reached.")]
    [Range(2, 32)] public int maxPendingReadbacks = 8;

    [Header("Compute shaders")]
    [SerializeField] ComputeShader tilingShader;
    [SerializeField] ComputeShader diffShader;

    STMHttpSerializer serializer;
    STMAudioRecorder audioRecorder;
    readonly List<SkinnedMeshRenderer> renderers = new List<SkinnedMeshRenderer>();
    EncoderBuffers[] encoderBuffers;
    Matrix4x4[] oldMatrices;
    readonly List<int> linedIndices = new List<int>();

    readonly List<int> byteSizes = new List<int>();
    readonly List<byte> combinedBinary = new List<byte>();
    readonly Dictionary<uint, PendingEncodeFrame> pendingFrames = new Dictionary<uint, PendingEncodeFrame>();

    int tilingKernel = -1;
    int diffKernel = -1;
    bool computeReady;
    bool startRecord;
    bool captureScheduled;
    float currentTime;
    int frameCount;
    uint timeStamp;
    long temporaryStartTicks;
    long lastCommittedTicks;
    uint nextCommitSequence;
    uint combinedFirstSequence;
    uint combinedLastSequence;
    long combinedChunkIndex;
    double recordStartRealtime;
    int captureGeneration;
    bool readbackFailureLogged;

    public bool IsStartRecord { get { return startRecord; } }
    public float FrameInterval { get { return 1.0f / Mathf.Max(0.1f, frameRate); } }

    void Awake()
    {
      MigrateFrameRate();
      LoadComputeShaders();
    }

    void Start()
    {
      audioRecorder = GetComponent<STMAudioRecorder>();
#if UNITY_EDITOR
      if (audioRecorder != null)
      {
        audioRecorder.OnFmp4InitData = OnGetFmp4InitData;
        audioRecorder.OnFmp4FragmentData = OnGetFmp4FragmentData;
      }
#endif
      InitializeSender();
    }

    void OnDestroy()
    {
      ReleaseEncoderBuffers();
    }

    void OnValidate()
    {
      packageSize = Mathf.Clamp(packageSize, 2, 254);
      containerSize = Mathf.Max(1, containerSize);
      MigrateFrameRate();
      combinedFrames = Mathf.Max(1, combinedFrames);
      subframesPerKeyframe = Mathf.Max(0, subframesPerKeyframe);
      maxPendingReadbacks = Mathf.Clamp(maxPendingReadbacks, 2, 32);
    }

    void MigrateFrameRate()
    {
      if (!frameRateMigrated)
      {
        frameRate = 1.0f / Mathf.Max(0.001f, frameInterval);
        frameRateMigrated = true;
      }

      frameRate = Mathf.Max(0.1f, frameRate);
      frameInterval = FrameInterval;
    }

    public void Record()
    {
      if (!computeReady)
        LoadComputeShaders();
      if (!computeReady)
      {
        Debug.LogError("StreamingMesh Sender requires compute shader support.");
        return;
      }
      if (!SystemInfo.supportsAsyncGPUReadback)
      {
        Debug.LogError("StreamingMesh Sender requires asynchronous GPU readback support.");
        return;
      }

      if (renderers.Count == 0)
        InitializeSender();
      if (renderers.Count == 0)
        return;

      if (!startRecord)
      {
        startRecord = true;
        captureGeneration++;
        recordStartRealtime = Time.realtimeSinceStartupAsDouble;
        currentTime = 0.0f;
        frameCount = 0;
        timeStamp = 0;
        nextCommitSequence = 0;
        combinedChunkIndex = 0;
        temporaryStartTicks = 0;
        lastCommittedTicks = 0;
        combinedFirstSequence = 0;
        combinedLastSequence = 0;
        pendingFrames.Clear();
        byteSizes.Clear();
        combinedBinary.Clear();
        linedIndices.Clear();
        readbackFailureLogged = false;

#if UNITY_EDITOR
        // Establish the mesh clock first, then start the persistent audio encoder
        // in the same main-thread turn. This keeps their startup offset bounded to
        // the next audio DSP buffer instead of allowing audio to lead the mesh.
        if (audioRecorder != null)
          audioRecorder.Record();
#endif
      }
    }

    public void Stop()
    {
      startRecord = false;
      TryCommitPendingFrames();
      if (pendingFrames.Count == 0 && byteSizes.Count > 0)
        FlushCombinedFrames();
#if UNITY_EDITOR
      if (audioRecorder != null)
        audioRecorder.Stop();
#endif
    }

    public void CreateChannel()
    {
      if (serializer == null || renderers.Count == 0)
        InitializeSender();
      CreateInfos();
    }

    void LoadComputeShaders()
    {
      if (!SystemInfo.supportsComputeShaders)
      {
        computeReady = false;
        return;
      }

      if (tilingShader == null)
        tilingShader = Resources.Load<ComputeShader>("TilingShader");
      if (diffShader == null)
        diffShader = Resources.Load<ComputeShader>("DiffShader");

      try
      {
        if (tilingShader != null && diffShader != null)
        {
          tilingKernel = tilingShader.FindKernel("EncodeKeyframe");
          diffKernel = diffShader.FindKernel("EncodeDelta");
          computeReady = true;
        }
      }
      catch (Exception exception)
      {
        computeReady = false;
        Debug.LogError("StreamingMesh compute shaders could not be initialized: " + exception.Message);
      }
    }

    void InitializeSender()
    {
      serializer = GetComponent<STMHttpSerializer>();
      renderers.Clear();

      if (targetGameObject == null)
      {
        Debug.LogWarning("StreamingMesh Sender target GameObject is not assigned.");
        return;
      }

      renderers.AddRange(targetGameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true));
      renderers.RemoveAll(renderer => renderer == null || renderer.sharedMesh == null);
      if (renderers.Count > MaxMeshCount)
      {
        Debug.LogError("StreamingMesh format supports at most 256 meshes.");
        renderers.RemoveRange(MaxMeshCount, renderers.Count - MaxMeshCount);
      }

      ReleaseEncoderBuffers();
      encoderBuffers = new EncoderBuffers[renderers.Count];
      oldMatrices = new Matrix4x4[renderers.Count];
      for (int i = 0; i < encoderBuffers.Length; i++)
        encoderBuffers[i] = new EncoderBuffers();

      linedIndices.Clear();
      byteSizes.Clear();
      combinedBinary.Clear();
    }

    void CreateInfos()
    {
#if !UNITY_EDITOR
      Debug.LogError("Creating StreamingMesh material metadata currently requires the Unity Editor.");
      return;
#else
      if (serializer == null || renderers.Count == 0 || targetGameObject == null)
      {
        Debug.LogError("StreamingMesh Sender is not initialized; assign a target before creating a channel.");
        return;
      }

      Dictionary<string, Material> materials = new Dictionary<string, Material>();
      Dictionary<string, Texture> textures = new Dictionary<string, Texture>();
      List<MeshInfo> meshInfos = new List<MeshInfo>();
      List<MaterialInfo> materialInfos = new List<MaterialInfo>();
      List<string> meshNames = new List<string>();
      List<string> materialNames = new List<string>();
      List<string> textureNames = new List<string>();

      for (int i = 0; i < renderers.Count; i++)
      {
        SkinnedMeshRenderer renderer = renderers[i];
        if (renderer == null)
          continue;

        MeshInfo meshInfo = serializer.CreateMeshInfo(renderer);
        if (meshInfo == null)
          continue;
        meshInfos.Add(meshInfo);
        meshNames.Add("mesh" + i);

        Material[] sharedMaterials = renderer.sharedMaterials;
        for (int materialIndex = 0; materialIndex < sharedMaterials.Length; materialIndex++)
        {
          Material material = sharedMaterials[materialIndex];
          if (material == null || materials.ContainsKey(material.name))
            continue;
          materials.Add(material.name, material);

          MaterialInfo materialInfo = serializer.CreateMaterialInfo(material);
          if (materialInfo == null)
            continue;
          materialInfos.Add(materialInfo);
          materialNames.Add("material" + materialNames.Count);

          List<KeyValuePair<string, Texture>> texturePairs = serializer.GetTexturesFromMaterial(material);
          if (texturePairs == null)
            continue;
          foreach (KeyValuePair<string, Texture> texturePair in texturePairs)
          {
            if (texturePair.Value == null || textures.ContainsKey(texturePair.Key))
              continue;
            textures.Add(texturePair.Key, texturePair.Value);
            textureNames.Add(texturePair.Key);
          }
        }
      }

      List<int> meshBufferSizes = new List<int>();
      List<int> materialBufferSizes = new List<int>();
      List<int> textureBufferSizes = new List<int>();
      List<byte> combinedBuffer = new List<byte>();

      foreach (KeyValuePair<string, Texture> texturePair in textures)
      {
        byte[] buffer = STMHttpSerializer.GetTextureToPNGByteArray(texturePair.Value, true);
        if (buffer == null)
          buffer = new byte[0];
        textureBufferSizes.Add(buffer.Length);
        combinedBuffer.AddRange(buffer);
      }

      for (int i = 0; i < materialInfos.Count; i++)
      {
        byte[] buffer = Encoding.UTF8.GetBytes(JsonUtility.ToJson(materialInfos[i]));
        materialBufferSizes.Add(buffer.Length);
        combinedBuffer.AddRange(buffer);
      }

      for (int i = 0; i < meshInfos.Count; i++)
      {
        byte[] buffer = Encoding.UTF8.GetBytes(JsonUtility.ToJson(meshInfos[i]));
        meshBufferSizes.Add(buffer.Length);
        combinedBuffer.AddRange(buffer);
      }

      ChannelInfo channelInfo = serializer.CreateChannelInfo(
        containerSize, packageSize, FrameInterval, combinedFrames,
        meshNames, materialNames, textureNames,
        meshBufferSizes, materialBufferSizes, textureBufferSizes);

      serializer.Send(channelInfo);
      serializer.Send(Lib.ExternalTools.Compress(combinedBuffer.ToArray()));
#endif
    }

#if UNITY_EDITOR
    void OnGetFmp4InitData(string fileName, byte[] data)
    {
      serializer.SendAudioInit(data, fileName);
    }

    void OnGetFmp4FragmentData(
      uint sequence,
      long startSample,
      int sampleCount,
      string fileName,
      byte[] data)
    {
      AudioInfo audioInfo = serializer.CreateAudioInfo(sequence, fileName, startSample, sampleCount);
      serializer.Send(data, "audio", fileName);
      serializer.Send(audioInfo, sequence);
    }
#endif

    void LateUpdate()
    {
      TryCommitPendingFrames();

      if (!Application.isPlaying || !startRecord || !computeReady)
        return;

      currentTime += Time.deltaTime;
      if (currentTime < FrameInterval)
        return;

      // Holding the capture clock here is intentional. In production mode we
      // prefer latency over silently dropping a keyframe dependency chain.
      if (pendingFrames.Count >= maxPendingReadbacks)
        return;

      currentTime -= FrameInterval;

      if (captureScheduled)
        return;

      captureScheduled = true;
      StartCoroutine(CaptureFrameAtEndOfFrame(frameCount == 0, captureGeneration));
      frameCount = (frameCount + 1) % (subframesPerKeyframe + 1);
    }

    System.Collections.IEnumerator CaptureFrameAtEndOfFrame(bool isKeyframe, int generation)
    {
      yield return new WaitForEndOfFrame();

      captureScheduled = false;
      if (!startRecord || generation != captureGeneration)
        yield break;

      // Capture after all LateUpdate callbacks so spring bones and other
      // post-animation deformation have been applied before BakeMesh().
      EncodeFrame(isKeyframe);
    }

    void EncodeFrame(bool isKeyframe)
    {
      uint sequence = timeStamp++;
      long ptsTicks = (long)((Time.realtimeSinceStartupAsDouble - recordStartRealtime) * TimeSpan.TicksPerSecond);
      PendingEncodeFrame frame = new PendingEncodeFrame(
        sequence,
        isKeyframe,
        ptsTicks,
        targetGameObject.transform.position,
        renderers.Count);
      pendingFrames.Add(sequence, frame);
      int generation = captureGeneration;

      for (int meshIndex = 0; meshIndex < renderers.Count; meshIndex++)
      {
        SkinnedMeshRenderer renderer = renderers[meshIndex];
        if (renderer == null || renderer.sharedMesh == null)
          continue;

        int vertexCount = renderer.sharedMesh.vertexCount;
        if (vertexCount <= 0 || vertexCount > MaxVertexCount)
        {
          Debug.LogError("StreamingMesh mesh '" + renderer.name + "' exceeds the 16-bit vertex index limit.");
          continue;
        }

        EncoderBuffers buffers = encoderBuffers[meshIndex];
        if (buffers.vertexCount != vertexCount && buffers.inFlightReadbacks > 0)
        {
          frame.hasError = true;
          continue;
        }

        bool layoutChanged = buffers.EnsureCapacity(vertexCount);
        if (layoutChanged && !isKeyframe)
        {
          // The previous keyframe no longer describes this topology. It will be
          // picked up safely on the next scheduled keyframe.
          continue;
        }

        renderer.BakeMesh(buffers.bakedMesh);
        buffers.vertices.Clear();
        buffers.bakedMesh.GetVertices(buffers.vertices);
        if (buffers.vertices.Count != vertexCount)
          continue;
        buffers.source.SetData(buffers.vertices);

        Matrix4x4 modelToStream = Matrix4x4.Translate(-targetGameObject.transform.position)
          * renderer.transform.localToWorldMatrix;
        int dispatchCount = DivideRoundUp(vertexCount, ThreadGroupSize);

        if (isKeyframe)
        {
          tilingShader.SetInt("vertexCount", vertexCount);
          tilingShader.SetInt("packSize", packageSize);
          tilingShader.SetInt("modelGroup", meshIndex);
          tilingShader.SetFloat("range", containerSize);
          tilingShader.SetMatrix("worldMatrix", modelToStream);
          tilingShader.SetBuffer(tilingKernel, "srcBuf", buffers.source);
          tilingShader.SetBuffer(tilingKernel, "previousBuf", buffers.previous);
          tilingShader.SetBuffer(tilingKernel, "destBuf", buffers.tiled);
          tilingShader.Dispatch(tilingKernel, dispatchCount, 1, 1);
          frame.pendingReadbacks++;
          buffers.inFlightReadbacks++;
          int requestedMeshIndex = meshIndex;
          int requestedVertexCount = vertexCount;
          AsyncGPUReadback.Request(buffers.tiled, request =>
          {
            buffers.inFlightReadbacks--;
            OnTiledReadback(
              generation,
              frame,
              requestedMeshIndex,
              requestedVertexCount,
              request);
          });
        }
        else
        {
          diffShader.SetInt("vertexCount", vertexCount);
          diffShader.SetMatrix("worldMatrix", modelToStream);
          diffShader.SetMatrix("oldMatrix", oldMatrices[meshIndex]);
          diffShader.SetBuffer(diffKernel, "srcBuf", buffers.source);
          diffShader.SetBuffer(diffKernel, "previousBuf", buffers.previous);
          diffShader.SetBuffer(diffKernel, "destBuf", buffers.delta);
          diffShader.Dispatch(diffKernel, dispatchCount, 1, 1);
          frame.pendingReadbacks++;
          buffers.inFlightReadbacks++;
          int requestedMeshIndex = meshIndex;
          int requestedVertexCount = vertexCount;
          AsyncGPUReadback.Request(buffers.delta, request =>
          {
            buffers.inFlightReadbacks--;
            OnDeltaReadback(
              generation,
              frame,
              requestedMeshIndex,
              requestedVertexCount,
              request);
          });
        }

        oldMatrices[meshIndex] = modelToStream;
      }

      frame.schedulingComplete = true;
      TryCommitPendingFrames();
    }

    void OnTiledReadback(
      int generation,
      PendingEncodeFrame frame,
      int meshIndex,
      int vertexCount,
      AsyncGPUReadbackRequest request)
    {
      if (generation != captureGeneration)
        return;

      try
      {
        if (request.hasError)
        {
          frame.hasError = true;
        }
        else
        {
          TiledVertex[] vertices = new TiledVertex[vertexCount];
          request.GetData<TiledVertex>().CopyTo(vertices);
          frame.tiledVertices[meshIndex] = vertices;
        }
      }
      catch (Exception exception)
      {
        frame.hasError = true;
        ReportReadbackFailure(exception);
      }
      finally
      {
        frame.pendingReadbacks--;
        TryCommitPendingFrames();
      }
    }

    void OnDeltaReadback(
      int generation,
      PendingEncodeFrame frame,
      int meshIndex,
      int vertexCount,
      AsyncGPUReadbackRequest request)
    {
      if (generation != captureGeneration)
        return;

      try
      {
        if (request.hasError)
        {
          frame.hasError = true;
        }
        else
        {
          FragmentVertex[] vertices = new FragmentVertex[vertexCount];
          request.GetData<FragmentVertex>().CopyTo(vertices);
          frame.deltaVertices[meshIndex] = vertices;
        }
      }
      catch (Exception exception)
      {
        frame.hasError = true;
        ReportReadbackFailure(exception);
      }
      finally
      {
        frame.pendingReadbacks--;
        TryCommitPendingFrames();
      }
    }

    void TryCommitPendingFrames()
    {
      while (true)
      {
        PendingEncodeFrame frame;
        if (!pendingFrames.TryGetValue(nextCommitSequence, out frame) || !frame.IsComplete)
          break;

        pendingFrames.Remove(nextCommitSequence);
        nextCommitSequence++;

        if (frame.hasError)
        {
          ReportReadbackFailure(null);
          startRecord = false;
          foreach (PendingEncodeFrame pending in pendingFrames.Values)
            pending.hasError = true;
          continue;
        }

        CommitFrame(frame);
      }

      if (!startRecord && pendingFrames.Count == 0 && byteSizes.Count > 0)
        FlushCombinedFrames();
    }

    void CommitFrame(PendingEncodeFrame frame)
    {
      Dictionary<int, TilePacker> tilePacks = frame.isKeyframe
        ? new Dictionary<int, TilePacker>()
        : null;

      List<byte> payload = new List<byte>();
      int packageCount = 0;
      if (frame.isKeyframe)
      {
        for (int meshIndex = 0; meshIndex < frame.tiledVertices.Length; meshIndex++)
        {
          TiledVertex[] vertices = frame.tiledVertices[meshIndex];
          if (vertices != null)
            AddKeyframeVertices(tilePacks, vertices, vertices.Length);
        }

        linedIndices.Clear();
        List<int> tileIds = new List<int>(tilePacks.Keys);
        tileIds.Sort();
        for (int i = 0; i < tileIds.Count; i++)
        {
          TilePacker pack = tilePacks[tileIds[i]];
          payload.AddRange(pack.PackToByteArray(packageSize));
          linedIndices.AddRange(pack.getIndices());
          packageCount++;
        }
      }
      else
      {
        for (int i = 0; i < linedIndices.Count; i++)
        {
          int meshIndex = (linedIndices[i] >> 16) & 0xFF;
          int vertexIndex = linedIndices[i] & 0xFFFF;
          FragmentVertex[] meshFragments = frame.deltaVertices[meshIndex];
          if (meshFragments == null || vertexIndex >= meshFragments.Length)
          {
            payload.Add(128);
            payload.Add(128);
            payload.Add(128);
            continue;
          }

          FragmentVertex fragment = meshFragments[vertexIndex];
          payload.Add((byte)fragment.x);
          payload.Add((byte)fragment.y);
          payload.Add((byte)fragment.z);
        }
      }

      if (combinedBinary.Count == 0)
      {
        temporaryStartTicks = frame.ptsTicks;
        combinedFirstSequence = frame.sequence;
      }

      byte[] streamData = AddHeader(
        payload.ToArray(), frame.rootPosition, packageCount, frame.isKeyframe, frame.sequence, frame.ptsTicks);
      byteSizes.Add(streamData.Length);
      combinedBinary.AddRange(streamData);
      lastCommittedTicks = frame.ptsTicks;
      combinedLastSequence = frame.sequence;

      if (byteSizes.Count >= combinedFrames)
        FlushCombinedFrames();
    }

    void ReportReadbackFailure(Exception exception)
    {
      if (readbackFailureLogged)
        return;

      string suffix = exception == null ? string.Empty : " " + exception.Message;
      Debug.LogError("StreamingMesh GPU readback failed; recording was paused to preserve the keyframe chain." + suffix);
      readbackFailureLogged = true;
    }

    static void AddKeyframeVertices(
      Dictionary<int, TilePacker> tilePacks,
      TiledVertex[] vertices,
      int vertexCount)
    {
      for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
      {
        TiledVertex vertex = vertices[vertexIndex];
        int tileX = (int)(vertex.tileID & 0xFF);
        int tileY = (int)((vertex.tileID >> 8) & 0xFF);
        int tileZ = (int)((vertex.tileID >> 16) & 0xFF);
        if (tileX == 255 && tileY == 255 && tileZ == 255)
          continue;

        int tileId = tileX | (tileY << 8) | (tileZ << 16);
        TilePacker pack;
        if (!tilePacks.TryGetValue(tileId, out pack))
        {
          pack = new TilePacker(tileX, tileY, tileZ);
          tilePacks.Add(tileId, pack);
        }

        pack.AddVertex(new ByteCoord
        {
          p1 = (byte)vertex.polyIndex,
          p2 = (byte)(vertex.polyIndex >> 8),
          p3 = (byte)(vertex.polyIndex >> 16),
          x = (byte)vertex.x,
          y = (byte)vertex.y,
          z = (byte)vertex.z
        });
      }
    }

    void FlushCombinedFrames()
    {
      if (byteSizes.Count == 0)
        return;

      long tick = combinedChunkIndex++;
      long endTicks = lastCommittedTicks + (long)(FrameInterval * TimeSpan.TicksPerSecond);
      StreamInfo streamInfo = serializer.CreateStreamInfo(
        tick,
        temporaryStartTicks,
        endTicks,
        combinedFirstSequence,
        combinedLastSequence);
      if (streamInfo == null)
      {
        Debug.LogError("StreamingMesh stream metadata could not be created on this platform.");
        byteSizes.Clear();
        combinedBinary.Clear();
        return;
      }

      serializer.Send(streamInfo, tick);
      string fileName = tick.ToString("000000") + ".stmv";
      int headerIntegerCount = byteSizes.Count + 1;
      byte[] buffer = new byte[headerIntegerCount * sizeof(int) + combinedBinary.Count];
      int[] sizes = new int[headerIntegerCount];
      sizes[0] = byteSizes.Count;
      byteSizes.CopyTo(sizes, 1);
      Buffer.BlockCopy(sizes, 0, buffer, 0, sizes.Length * sizeof(int));
      Buffer.BlockCopy(combinedBinary.ToArray(), 0, buffer, sizes.Length * sizeof(int), combinedBinary.Count);
      serializer.Send(Lib.ExternalTools.Compress(buffer), "stream", fileName);

      byteSizes.Clear();
      combinedBinary.Clear();
    }

    static byte[] AddHeader(
      byte[] rawData,
      Vector3 position,
      int packages,
      bool isKeyframe,
      uint stamp,
      long presentationTicks)
    {
      const int headerSize = 29;
      byte[] output = new byte[rawData.Length + headerSize];
      Buffer.BlockCopy(rawData, 0, output, headerSize, rawData.Length);
      output[0] = isKeyframe ? (byte)0x0F : (byte)0x0E;
      output[8] = 2;

      byte[] stampBytes = BitConverter.GetBytes(stamp);
      Buffer.BlockCopy(stampBytes, 0, output, 1, stampBytes.Length);
      if (isKeyframe)
      {
        output[5] = (byte)packages;
        output[6] = (byte)(packages >> 8);
        output[7] = (byte)(packages >> 16);
      }

      Buffer.BlockCopy(BitConverter.GetBytes(position.x), 0, output, 9, sizeof(float));
      Buffer.BlockCopy(BitConverter.GetBytes(position.y), 0, output, 13, sizeof(float));
      Buffer.BlockCopy(BitConverter.GetBytes(position.z), 0, output, 17, sizeof(float));
      Buffer.BlockCopy(BitConverter.GetBytes(presentationTicks), 0, output, 21, sizeof(long));
      return output;
    }

    static int DivideRoundUp(int value, int divisor)
    {
      return (value + divisor - 1) / divisor;
    }

    void ReleaseEncoderBuffers()
    {
      if (encoderBuffers == null)
        return;
      for (int i = 0; i < encoderBuffers.Length; i++)
      {
        if (encoderBuffers[i] != null)
          encoderBuffers[i].Dispose();
      }
      encoderBuffers = null;
    }
  }
}
