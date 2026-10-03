using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace StreamingMesh.Core.Rendering
{
  public enum StreamingPlaybackState
  {
    WaitingForKeyframe,
    Buffering,
    Playing,
    Holding,
    Disposed
  }

  public sealed class StreamingMeshRenderer : IDisposable
  {
    public const double ResumeBufferSeconds = 0.25;
    public const double DecodeAheadSeconds = 0.5;
    sealed class DecodedFrame
    {
      public uint sequence;
      public double presentationTime;
      public Vector3 rootPosition;
      public float[][] vertices;
      public GpuVertexPipeline.Frame gpuFrame;
      public bool held;
    }

    readonly Dictionary<string, Texture2D> m_TextureDictionary = new Dictionary<string, Texture2D>();
    readonly Dictionary<string, Material> m_MaterialDictionary = new Dictionary<string, Material>();
    readonly Dictionary<string, Mesh> m_MeshDictionary = new Dictionary<string, Mesh>();
    readonly List<Mesh> m_MeshList = new List<Mesh>();
    readonly List<List<string>> m_MeshMaterialNames = new List<List<string>>();
    readonly HashSet<int> m_ReceivedChunks = new HashSet<int>();
    FrameRing<EncodedFrameSlice> m_EncodedFrames;
    EncodedChunkPool m_ChunkPool;
    readonly object m_ImportGate = new object();
    FrameRing<DecodedFrame> m_DecodedFrames;
    DecodedFrame[] m_DecodedPool;
    int[] m_RecentChunks;
    int m_RecentChunkCursor;

    float[][] m_VertexLayout;
    Vector3[][] m_InterpolatedVertices;
    GameObject m_RootGameObject;
    VertexContainer m_VertexContainer;
    GpuVertexPipeline m_GpuPipeline;
    readonly Queue<GpuVertexPipeline.Frame> m_PendingGpuFrames = new Queue<GpuVertexPipeline.Frame>(64);
    bool[] m_RecalculateNormals, m_RecalculateTangents;
    public ReceiverDecodeBackend DecodeBackend { get; set; } = ReceiverDecodeBackend.Auto;
    public ReceiverNormalMode NormalMode { get; set; } = ReceiverNormalMode.Auto;
    public ReceiverTangentMode TangentMode { get; set; } = ReceiverTangentMode.Auto;
    /// <summary>Stream material names explicitly declared to require tangents in Auto mode.
    /// Configure before CreateVertexContainer; reconnect to change the output layout.</summary>
    public HashSet<string> TangentMaterialNames { get; } = new HashSet<string>(StringComparer.Ordinal);
    public bool IsGpuResident { get { return m_GpuPipeline != null; } }
    public int EncodedFrameCount { get { return m_EncodedFrames == null ? 0 : m_EncodedFrames.Count; } }
    public bool CanAcceptChunk { get { return EncodedFrameCount < Math.Max(16, m_CombinedFrames * 2) && (m_ChunkPool == null || m_ChunkPool.HasFreeChunk); } }
    public int PendingGpuFrameCount { get { return m_PendingGpuFrames.Count; } }
    public long GpuPoolBytes { get { return m_GpuPipeline == null ? 0 : m_GpuPipeline.PoolBytes; } }
    public long GpuModelBytes { get { return m_GpuPipeline == null ? 0 : m_GpuPipeline.ModelBytes; } }
    public long GpuTangentBytes { get { return m_GpuPipeline == null ? 0 : m_GpuPipeline.TangentBytes; } }

    public long EncodedPoolBytes => m_ChunkPool == null ? 0 : m_ChunkPool.AllocatedBytes;

    int m_ContainerSize = 4;
    int m_PackageSize = 128;
    float m_FrameInterval = 0.1f;
    int m_CombinedFrames = 100;
    int m_PrebufferFrames = 2;
    int m_MaxDecodedFrames = 8;

    bool m_NeedsKeyframe = true;
    bool m_HasDecodeCursor;
    uint m_NextDecodeSequence;
    float m_LastInterpolation = -1.0f;
    uint m_LastPresentedSequence = uint.MaxValue;
    StreamingPlaybackState m_PlaybackState = StreamingPlaybackState.WaitingForKeyframe;

    public bool IsPlayable { get { return m_PlaybackState == StreamingPlaybackState.Playing; } }
    public StreamingPlaybackState PlaybackState { get { return m_PlaybackState; } }
    public int BufferedFrameCount { get { return m_DecodedFrames == null ? 0 : m_DecodedFrames.Count; } }
    public double PresentedTime { get; private set; } = double.NaN;
    public double BufferedUntil { get { return BufferedFrameCount == 0 ? double.NaN : m_DecodedFrames[m_DecodedFrames.Count - 1].presentationTime; } }

    public bool CanPlayAt(double time, double leadSeconds)
    {
      return BufferedFrameCount >= m_PrebufferFrames &&
        m_DecodedFrames[0].presentationTime <= time + 0.001 &&
        BufferedUntil >= time + leadSeconds;
    }
    public bool IsUsingComputeShader
    {
      get { return IsGpuResident || (m_VertexContainer != null && m_VertexContainer.IsUsingComputeShader); }
    }

    public bool TryGetPlaybackStartTime(out double presentationTime)
    {
      if (BufferedFrameCount > 0)
      {
        presentationTime = m_DecodedFrames[0].presentationTime;
        return true;
      }
      presentationTime = 0.0;
      return false;
    }

    public int ContainerSize
    {
      get { return m_ContainerSize; }
      set { m_ContainerSize = value; }
    }

    public int PackageSize
    {
      get { return m_PackageSize; }
      set { m_PackageSize = value; }
    }

    public float FrameInterval
    {
      get { return m_FrameInterval; }
      set
      {
        m_FrameInterval = Mathf.Max(0.001f, value);
        // Keep enough slots for the resume margin even for 30/60 fps streams.
        m_MaxDecodedFrames = Math.Max(8, (int)Math.Ceiling(DecodeAheadSeconds / m_FrameInterval) + 10);
      }
    }

    public int CombinedFrames
    {
      get { return m_CombinedFrames; }
      set { if (value > 4096) throw new ArgumentOutOfRangeException(nameof(value), "Receiver chunks support at most 4096 frames."); m_CombinedFrames = Mathf.Max(1, value); }
    }

    public int PrebufferFrames
    {
      get { return m_PrebufferFrames; }
      set { m_PrebufferFrames = Mathf.Max(2, value); }
    }

    public void AddTexture(string name, Texture2D texture)
    {
      if (!m_TextureDictionary.ContainsKey(name))
        m_TextureDictionary.Add(name, texture);
    }

    public void AddMaterial(string name, Material material)
    {
      if (!m_MaterialDictionary.ContainsKey(name))
        m_MaterialDictionary.Add(name, material);
    }

    public void AddMesh(string name, Mesh mesh, IList<string> materialNames = null)
    {
      if (!m_MeshDictionary.ContainsKey(name))
      {
        m_MeshDictionary.Add(name, mesh);
        m_MeshList.Add(mesh);
        m_MeshMaterialNames.Add(materialNames == null ? new List<string>() : new List<string>(materialNames));
      }
    }

    public Dictionary<string, Texture2D> TextureDictionary { get { return m_TextureDictionary; } }
    public Dictionary<string, Material> MaterialDictionary { get { return m_MaterialDictionary; } }

    public GameObject RootGameObject
    {
      set { m_RootGameObject = value; }
    }

    public void CreateVertexContainer(int packageSize, int containerSize, bool enableComputeShader = true)
    {
      m_VertexContainer?.Dispose();
      m_GpuPipeline?.Dispose();
      m_GpuPipeline = null;
      m_PendingGpuFrames.Clear();
      ClearDecodedFrames();
      m_NeedsKeyframe = true;
      m_HasDecodeCursor = false;
      m_LastPresentedSequence = uint.MaxValue;
      m_ContainerSize = containerSize;
      m_PackageSize = packageSize;
      ConfigureVertexRequirements();
      // The CPU decoder always remains a true CPU fallback, never a readback path.
      m_VertexContainer = new VertexContainer(packageSize, containerSize, false);
      if (enableComputeShader && DecodeBackend != ReceiverDecodeBackend.CPU)
      {
        try
        {
          m_GpuPipeline = new GpuVertexPipeline(m_MeshList, packageSize, containerSize,
            m_MaxDecodedFrames + 8, m_RecalculateNormals, m_RecalculateTangents);
          m_MaxDecodedFrames = Math.Min(m_MaxDecodedFrames, Math.Max(3, m_GpuPipeline.Capacity - 2));
          Debug.Log("StreamingMesh GPU resident receiver: " + m_GpuPipeline.Capacity +
            " slots, " + m_GpuPipeline.PoolBytes + " pool bytes, " + m_GpuPipeline.ModelBytes +
            " model bytes, " + m_GpuPipeline.TangentBytes + " tangent bytes");
        }
        catch (Exception exception)
        {
          Debug.LogWarning("StreamingMesh GPU initialization unavailable; using CPU: " + exception.Message);
        }
      }
    }

    void ConfigureVertexRequirements()
    {
      m_RecalculateNormals = new bool[m_MeshList.Count];
      m_RecalculateTangents = new bool[m_MeshList.Count];
      bool defaultNormals = NormalMode == ReceiverNormalMode.Recalculate ||
        (NormalMode == ReceiverNormalMode.Auto && UsesNormals());
      bool forcedNormals = false;
      for (int i=0; i<m_MeshList.Count; i++)
      {
        bool requested = TangentMode == ReceiverTangentMode.Recalculate;
        if (TangentMode == ReceiverTangentMode.Auto)
          foreach (string material in m_MeshMaterialNames[i])
            if (material != null && TangentMaterialNames.Contains(material.TrimEnd('\0')))
              { requested = true; break; }
        bool tangents = requested && GpuVertexPipeline.CanRecalculateTangents(m_MeshList[i]);
        m_RecalculateTangents[i] = tangents;
        m_RecalculateNormals[i] = defaultNormals || tangents;
        forcedNormals |= tangents && NormalMode == ReceiverNormalMode.None;
        if (requested && !tangents && m_MeshList[i].vertexCount > 0)
          Debug.LogWarning("StreamingMesh tangent reconstruction skipped for '" + m_MeshList[i].name +
            "': UV0 (at least two components) and triangles are required.");
      }
      if (forcedNormals)
        Debug.LogWarning("StreamingMesh tangent reconstruction enables normals on its target meshes despite Normal Mode=None.");
    }

    bool UsesNormals()
    {
      if (m_MaterialDictionary.Count == 0) return true;
      foreach (var material in m_MaterialDictionary.Values)
      {
        string shader = material.shader.name;
        if (shader != "StreamingMesh/Standard" && shader != "Universal Render Pipeline/Unlit" &&
            shader != "Unlit/Texture" && shader != "Unlit/Color") return true;
      }
      return false;
    }

    void RemoveDecodedFrame(int index)
    {
      m_GpuPipeline?.Release(m_DecodedFrames[index].gpuFrame);
      var frame = m_DecodedFrames.RemoveAt(index);
      frame.held = false; frame.gpuFrame = null;
    }

    void CollectGpuFrames()
    {
      while (m_PendingGpuFrames.Count > 0 && m_PendingGpuFrames.Peek().IsReady)
      {
        var frame = m_PendingGpuFrames.Dequeue();
        if (m_DecodedFrames.Count >= m_MaxDecodedFrames && m_DecodedFrames.Count > 2)
          RemoveDecodedFrame(1);
        var decoded = RentDecodedFrame(false);
        decoded.sequence=frame.Sequence; decoded.presentationTime=frame.Time;
        decoded.rootPosition=frame.RootPosition; decoded.gpuFrame=frame;
        m_DecodedFrames.Add(decoded);
      }
    }

    void FallBackFromGpu(Exception exception)
    {
      Debug.LogWarning("StreamingMesh GPU pipeline failed; waiting for a CPU keyframe: " + exception.Message);
      m_GpuPipeline?.Dispose();
      m_GpuPipeline = null;
      m_PendingGpuFrames.Clear();
      ClearDecodedFrames();
      m_NeedsKeyframe = true;
      m_LastPresentedSequence = uint.MaxValue;
      m_VertexContainer.Dispose();
      m_VertexContainer = new VertexContainer(m_PackageSize, m_ContainerSize, false);
    }

    public void CreateVertexBuffer()
    {
      if (m_MeshList.Count == 0)
        return;

      ClearDecodedFrames(); m_DecodedPool = null;
      EnsureDecodedStorage();
      m_VertexLayout = new float[m_MeshList.Count][];
      m_InterpolatedVertices = new Vector3[m_MeshList.Count][];
      for (int meshIndex = 0; meshIndex < m_MeshList.Count; meshIndex++)
      {
        Vector3[] vertices = m_MeshList[meshIndex].vertices;
        m_VertexLayout[meshIndex] = new float[vertices.Length * 3];
        m_InterpolatedVertices[meshIndex] = new Vector3[vertices.Length];
        for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
          int offset = vertexIndex * 3;
          m_VertexLayout[meshIndex][offset] = vertices[vertexIndex].x;
          m_VertexLayout[meshIndex][offset + 1] = vertices[vertexIndex].y;
          m_VertexLayout[meshIndex][offset + 2] = vertices[vertexIndex].z;
        }
      }
    }

    void EnsureEncodedStorage()
    {
      if (m_EncodedFrames != null) return;
      int threshold = Math.Max(16, m_CombinedFrames * 2);
      int slots = (threshold + m_CombinedFrames - 1) / m_CombinedFrames + 2;
      int vertices = 0;
      foreach (var mesh in m_MeshList) vertices = checked(vertices + mesh.vertexCount);
      int maximumFrame = vertices == 0 ? 16 * 1024 * 1024 : checked(29 + vertices * 11);
      m_EncodedFrames = new FrameRing<EncodedFrameSlice>(threshold + m_CombinedFrames);
      m_ChunkPool = new EncodedChunkPool(slots, m_CombinedFrames, maximumFrame);
      m_RecentChunks = new int[slots * 2];
      for (int i = 0; i < m_RecentChunks.Length; i++) m_RecentChunks[i] = -1;
    }

    void EnsureDecodedStorage()
    {
      if (m_DecodedPool != null && m_DecodedPool.Length >= m_MaxDecodedFrames + 1) return;
      ClearDecodedFrames();
      m_DecodedFrames = new FrameRing<DecodedFrame>(m_MaxDecodedFrames + 1);
      m_DecodedPool = new DecodedFrame[m_MaxDecodedFrames + 1];
      for (int i = 0; i < m_DecodedPool.Length; i++) m_DecodedPool[i] = new DecodedFrame();
    }

    DecodedFrame RentDecodedFrame(bool cpu)
    {
      EnsureDecodedStorage();
      foreach (var frame in m_DecodedPool)
      {
        if (frame.held) continue;
        if (cpu && frame.vertices == null)
        {
          frame.vertices = new float[m_VertexLayout.Length][];
          for (int i = 0; i < frame.vertices.Length; i++) frame.vertices[i] = new float[m_VertexLayout[i].Length];
        }
        frame.held = true;
        return frame;
      }
      throw new InvalidOperationException("Receiver decoded frame pool is full.");
    }

    void ClearDecodedFrames()
    {
      if (m_DecodedFrames != null) while (m_DecodedFrames.Count > 0) RemoveDecodedFrame(0);
    }

    public void AddVertexData(string name, byte[] data, long ticks)
    {
      if (!TryBeginChunk(name, data, out int index)) return;
      EncodedChunkPool.Chunk chunk = null;
      try
      {
        lock (m_ImportGate) chunk = m_ChunkPool.Parse(index, data, ticks, m_CombinedFrames, m_FrameInterval);
        CommitChunk(chunk); chunk = null;
      }
      catch (Exception exception) { RejectChunk(index, exception); }
      finally { if (chunk != null) m_ChunkPool.Return(chunk); }
    }

    // Only parsing owns the worker lease. Commit/decoder state remains on Unity's
    // synchronization context; disposing a Receiver cannot recycle an active lease.
    public async Task<bool> AddVertexDataAsync(string name, byte[] data, long ticks)
    {
      if (m_PlaybackState == StreamingPlaybackState.Disposed || data == null || data.Length == 0 ||
          !int.TryParse(name, out int index) || index < 0) return false;
      EnsureEncodedStorage();
      if (m_ReceivedChunks.Contains(index)) return true;
      if (!CanAcceptChunk) return false;
      m_ReceivedChunks.Add(index);
      var pool = m_ChunkPool;
      int combined = m_CombinedFrames; float interval = m_FrameInterval;
      EncodedChunkPool.Chunk chunk = null;
      try
      {
#if UNITY_WEBGL && !UNITY_EDITOR
        lock (m_ImportGate) chunk = pool.Parse(index, data, ticks, combined, interval);
#else
        chunk = await Task.Run(() => { lock (m_ImportGate) return pool.Parse(index, data, ticks, combined, interval); });
#endif
        if (m_PlaybackState == StreamingPlaybackState.Disposed) return false;
        CommitChunk(chunk); chunk = null;
        return true;
      }
      catch (Exception exception) { RejectChunk(index, exception); return false; }
      finally { if (chunk != null) pool.Return(chunk); }
    }

    bool TryBeginChunk(string name, byte[] data, out int index)
    {
      index = -1;
      if (m_PlaybackState == StreamingPlaybackState.Disposed || data == null || data.Length == 0 ||
          !int.TryParse(name, out index) || index < 0) return false;
      EnsureEncodedStorage();
      return CanAcceptChunk && m_ReceivedChunks.Add(index);
    }

    void RejectChunk(int index, Exception exception)
    {
      m_ReceivedChunks.Remove(index);
      if (m_PlaybackState != StreamingPlaybackState.Disposed)
        Debug.LogWarning("StreamingMesh rejected stream chunk " + index + ": " + exception.Message);
    }

    int FindEncoded(uint sequence)
    {
      int lo = 0, hi = m_EncodedFrames.Count;
      while (lo < hi)
      {
        int mid = lo + (hi - lo) / 2;
        if (m_EncodedFrames[mid].sequence < sequence) lo = mid + 1; else hi = mid;
      }
      return lo;
    }

    void CommitChunk(EncodedChunkPool.Chunk chunk)
    {
      // Admission reserves room for the entire batch. No partial import on a
      // malformed chunk or concurrent imports that consume the remaining room.
      if (chunk.count > m_EncodedFrames.Capacity - m_EncodedFrames.Count)
        throw new InvalidOperationException("Receiver frame ring is full.");
      for (int i = 0; i < chunk.count; i++)
      {
        var frame = chunk.frames[i];
        if (m_HasDecodeCursor && frame.sequence < m_NextDecodeSequence) continue;
        int at = FindEncoded(frame.sequence);
        if (at < m_EncodedFrames.Count && m_EncodedFrames[at].sequence == frame.sequence) continue;
        m_EncodedFrames.Insert(at, frame); chunk.retained++;
      }
      // Duplicate bookkeeping is bounded too; old deltas are filtered by cursor.
      int index = chunk.index;
      int previous = m_RecentChunks[m_RecentChunkCursor];
      if (previous >= 0) m_ReceivedChunks.Remove(previous);
      m_RecentChunks[m_RecentChunkCursor] = index;
      m_RecentChunkCursor = (m_RecentChunkCursor + 1) % m_RecentChunks.Length;
      if (chunk.retained == 0) m_ChunkPool.Return(chunk);
    }

    void ReleaseEncodedFrame()
    {
      var frame = m_EncodedFrames.RemoveAt(0);
      if (--frame.chunk.retained == 0) m_ChunkPool.Return(frame.chunk);
    }

    public void UpdateWithTime(double updateTime)
    {
      if (m_PlaybackState == StreamingPlaybackState.Disposed || m_DecodedFrames == null)
        return;

      try { CollectGpuFrames(); }
      catch (Exception exception) { FallBackFromGpu(exception); }

      // Release consumed poses before refill so the GPU can submit a batch in
      // this Update instead of waiting an extra render frame for free slots.
      while (m_DecodedFrames.Count > 2 && m_DecodedFrames[1].presentationTime <= updateTime)
        RemoveDecodedFrame(0);

      // Synchronous decoders can refill more than one frame per rendered frame.
      // Bound the work so catching up does not monopolize the Unity main thread.
      double decodeDeadline = Time.realtimeSinceStartupAsDouble + 0.004;
      for (int i = 0; i < 8; i++)
      {
        if (!PumpDecoder(updateTime) ||
            Time.realtimeSinceStartupAsDouble >= decodeDeadline)
          break;
      }
      if (m_DecodedFrames.Count == 0)
      {
        m_PlaybackState = m_NeedsKeyframe
          ? StreamingPlaybackState.WaitingForKeyframe
          : StreamingPlaybackState.Buffering;
        return;
      }

      while (m_DecodedFrames.Count > 2 && m_DecodedFrames[1].presentationTime <= updateTime)
        RemoveDecodedFrame(0);

      if (m_DecodedFrames.Count < m_PrebufferFrames && updateTime >= m_DecodedFrames[0].presentationTime)
      {
        PresentFrame(m_DecodedFrames[0]);
        m_PlaybackState = StreamingPlaybackState.Buffering;
        return;
      }

      DecodedFrame previous = m_DecodedFrames[0];
      if (m_DecodedFrames.Count == 1 || updateTime >= m_DecodedFrames[m_DecodedFrames.Count - 1].presentationTime)
      {
        PresentFrame(m_DecodedFrames[m_DecodedFrames.Count - 1]);
        m_PlaybackState = StreamingPlaybackState.Holding;
        return;
      }

      DecodedFrame next = m_DecodedFrames[1];
      double duration = Math.Max(0.000001, next.presentationTime - previous.presentationTime);
      float interpolation = Mathf.Clamp01((float)((updateTime - previous.presentationTime) / duration));
      PresentInterpolated(previous, next, interpolation);
      m_PlaybackState = StreamingPlaybackState.Playing;
    }

    bool PumpDecoder(double playbackTime)
    {
      if (m_VertexContainer == null || m_VertexLayout == null)
        return false;
      double queuedUntil = BufferedUntil;
      foreach (var pending in m_PendingGpuFrames) queuedUntil = pending.Time;
      if (m_DecodedFrames.Count + m_PendingGpuFrames.Count >= m_PrebufferFrames &&
          queuedUntil >= playbackTime + Math.Max(DecodeAheadSeconds, m_FrameInterval * 2)) return false;
      EncodedFrameSlice frame;
      if (!TrySelectDecodeFrame(out frame))
        return false;
      if (m_DecodedFrames.Count >= m_MaxDecodedFrames)
      {
        if (BufferedUntil >= playbackTime + Math.Max(DecodeAheadSeconds, m_FrameInterval * 2))
          return false;
        // Capture PTS can be closer together than the advertised frame interval.
        // Keep the interpolation anchor and newest poses, while allowing the
        // decoder to reach the required time horizon with bounded memory.
        // Encoded delta frames are still decoded in order; no dependency is skipped.
        if (m_DecodedFrames.Count > 2)
          RemoveDecodedFrame(1);
      }
      if (m_GpuPipeline != null)
      {
        // Bound decode-ahead by both presentation horizon and available immutable slots.
        if (m_PendingGpuFrames.Count > 0 && m_DecodedFrames.Count + m_PendingGpuFrames.Count >= m_MaxDecodedFrames)
          return false;
        try
        {
          GpuVertexPipeline.Frame gpuFrame;
          string error;
          if (!m_GpuPipeline.TrySubmit(frame.Data, frame.sequence, frame.presentationTime, out gpuFrame, out error))
          {
            if (error == null) return false;
            Debug.LogWarning("StreamingMesh frame " + frame.sequence + " was rejected: " + error);
            AdvanceDecodeCursor(frame.sequence, false);
            return true;
          }
          m_PendingGpuFrames.Enqueue(gpuFrame);
          AdvanceDecodeCursor(frame.sequence, true);
          return true;
        }
        catch (Exception exception)
        {
          FallBackFromGpu(exception);
          return true;
        }
      }
      var decoded = RentDecodedFrame(true);
      bool succeeded = m_VertexContainer.DecodeInto(frame.Data, decoded.vertices, out var root, out var errorMessage);
      AdvanceDecodeCursor(frame.sequence, succeeded);
      if (!succeeded)
      {
        decoded.held = false;
        Debug.LogWarning("StreamingMesh frame " + frame.sequence + " was rejected: " + errorMessage);
        return true;
      }
      decoded.sequence = frame.sequence; decoded.presentationTime = frame.presentationTime;
      decoded.rootPosition = root; decoded.gpuFrame = null;
      m_DecodedFrames.Add(decoded);
      return true;
    }

    void AdvanceDecodeCursor(uint sequence, bool succeeded)
    {
      while (m_EncodedFrames.Count > 0 && m_EncodedFrames[0].sequence <= sequence) ReleaseEncodedFrame();
      m_NeedsKeyframe = !succeeded;
      m_HasDecodeCursor = true;
      m_NextDecodeSequence = sequence + 1;
    }

    bool TrySelectDecodeFrame(out EncodedFrameSlice selected)
    {
      selected = default(EncodedFrameSlice);
      if (m_EncodedFrames == null) return false;
      int at = m_HasDecodeCursor ? FindEncoded(m_NextDecodeSequence) : 0;
      if (!m_NeedsKeyframe && m_HasDecodeCursor && at < m_EncodedFrames.Count &&
          m_EncodedFrames[at].sequence == m_NextDecodeSequence)
      {
        selected = m_EncodedFrames[at]; return true;
      }
      for (int i = at; i < m_EncodedFrames.Count; i++)
        if (m_EncodedFrames[i].isKeyframe)
        {
          selected = m_EncodedFrames[i]; m_NeedsKeyframe = true; return true;
        }
      return false;
    }

    void PresentFrame(DecodedFrame frame)
    {
      PresentedTime = frame.presentationTime;
      if (m_LastPresentedSequence == frame.sequence && Mathf.Approximately(m_LastInterpolation, 0.0f))
        return;

      if (!ApplyFrameVertices(frame, frame, 0.0f)) return;
      if (m_RootGameObject != null)
        m_RootGameObject.transform.localPosition = frame.rootPosition;
      m_LastPresentedSequence = frame.sequence;
      m_LastInterpolation = 0.0f;
    }

    void PresentInterpolated(DecodedFrame previous, DecodedFrame next, float interpolation)
    {
      PresentedTime = previous.presentationTime + (next.presentationTime - previous.presentationTime) * interpolation;
      if (m_LastPresentedSequence == previous.sequence && Mathf.Approximately(m_LastInterpolation, interpolation))
        return;

      if (!ApplyFrameVertices(previous, next, interpolation)) return;
      if (m_RootGameObject != null)
      {
        m_RootGameObject.transform.localPosition = Vector3.LerpUnclamped(
          previous.rootPosition,
          next.rootPosition,
          interpolation);
      }
      m_LastPresentedSequence = previous.sequence;
      m_LastInterpolation = interpolation;
    }

    bool ApplyFrameVertices(DecodedFrame previous, DecodedFrame next, float interpolation)
    {
      if (m_GpuPipeline != null)
      {
        try { m_GpuPipeline.Present(previous.gpuFrame, next.gpuFrame, interpolation); }
        catch (Exception exception) { FallBackFromGpu(exception); return false; }
      }
      else ApplyVertices(previous.vertices, next.vertices, interpolation);
      return true;
    }

    void ApplyVertices(float[][] previous, float[][] next, float interpolation)
    {
      for (int meshIndex = 0; meshIndex < m_MeshList.Count; meshIndex++)
      {
        Vector3[] output = m_InterpolatedVertices[meshIndex];
        float[] oldVertices = previous[meshIndex];
        float[] newVertices = next[meshIndex];
        for (int vertexIndex = 0; vertexIndex < output.Length; vertexIndex++)
        {
          int offset = vertexIndex * 3;
          output[vertexIndex].x = Mathf.LerpUnclamped(oldVertices[offset], newVertices[offset], interpolation);
          output[vertexIndex].y = Mathf.LerpUnclamped(oldVertices[offset + 1], newVertices[offset + 1], interpolation);
          output[vertexIndex].z = Mathf.LerpUnclamped(oldVertices[offset + 2], newVertices[offset + 2], interpolation);
        }
        m_MeshList[meshIndex].vertices = output;
        if (m_RecalculateNormals == null || m_RecalculateNormals[meshIndex])
          m_MeshList[meshIndex].RecalculateNormals();
        if (m_RecalculateTangents != null && m_RecalculateTangents[meshIndex])
          m_MeshList[meshIndex].RecalculateTangents();
      }
    }

    public void Dispose()
    {
      m_PlaybackState = StreamingPlaybackState.Disposed;
      m_GpuPipeline?.Dispose();
      m_GpuPipeline = null;
      m_PendingGpuFrames.Clear();
      if (m_VertexContainer != null)
      {
        m_VertexContainer.Dispose();
        m_VertexContainer = null;
      }
      if (m_EncodedFrames != null) while (m_EncodedFrames.Count > 0) ReleaseEncodedFrame();
      m_ChunkPool?.Dispose();
      ClearDecodedFrames();
      foreach (var mesh in m_MeshList) UnityEngine.Object.Destroy(mesh);
      foreach (var material in m_MaterialDictionary.Values) UnityEngine.Object.Destroy(material);
      foreach (var texture in m_TextureDictionary.Values) UnityEngine.Object.Destroy(texture);
      m_MeshList.Clear();
      m_MeshMaterialNames.Clear();
      m_MeshDictionary.Clear();
      m_MaterialDictionary.Clear();
      m_TextureDictionary.Clear();
      m_DecodedPool = null; m_ReceivedChunks.Clear();
      m_VertexLayout = null;
      m_InterpolatedVertices = null;
      m_RecalculateNormals = null;
      m_RecalculateTangents = null;
    }
  }
}
