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
    sealed class EncodedFrame
    {
      public uint sequence;
      public double presentationTime;
      public bool isKeyframe;
      public byte[] data;
    }

    sealed class DecodedFrame
    {
      public uint sequence;
      public double presentationTime;
      public Vector3 rootPosition;
      public float[][] vertices;
      public GpuVertexPipeline.Frame gpuFrame;
    }

    readonly Dictionary<string, Texture2D> m_TextureDictionary = new Dictionary<string, Texture2D>();
    readonly Dictionary<string, Material> m_MaterialDictionary = new Dictionary<string, Material>();
    readonly Dictionary<string, Mesh> m_MeshDictionary = new Dictionary<string, Mesh>();
    readonly List<Mesh> m_MeshList = new List<Mesh>();
    readonly HashSet<int> m_ReceivedChunks = new HashSet<int>();
    readonly SortedDictionary<uint, EncodedFrame> m_EncodedFrames = new SortedDictionary<uint, EncodedFrame>();
    readonly List<DecodedFrame> m_DecodedFrames = new List<DecodedFrame>();

    float[][] m_VertexLayout;
    Vector3[][] m_InterpolatedVertices;
    GameObject m_RootGameObject;
    VertexContainer m_VertexContainer;
    GpuVertexPipeline m_GpuPipeline;
    readonly Queue<GpuVertexPipeline.Frame> m_PendingGpuFrames = new Queue<GpuVertexPipeline.Frame>();
    bool m_RecalculateNormals = true;
    public ReceiverDecodeBackend DecodeBackend { get; set; } = ReceiverDecodeBackend.Auto;
    public ReceiverNormalMode NormalMode { get; set; } = ReceiverNormalMode.Auto;
    public bool IsGpuResident { get { return m_GpuPipeline != null; } }
    public int EncodedFrameCount { get { return m_EncodedFrames.Count; } }
    public bool CanAcceptChunk { get { return m_EncodedFrames.Count < Math.Max(16, m_CombinedFrames * 2); } }
    public int PendingGpuFrameCount { get { return m_PendingGpuFrames.Count; } }
    public long GpuPoolBytes { get { return m_GpuPipeline == null ? 0 : m_GpuPipeline.PoolBytes; } }

    int m_ContainerSize = 4;
    int m_PackageSize = 128;
    float m_FrameInterval = 0.1f;
    int m_CombinedFrames = 100;
    int m_PrebufferFrames = 2;
    int m_MaxDecodedFrames = 8;

    bool m_DecodeInFlight;
    bool m_NeedsKeyframe = true;
    bool m_HasDecodeCursor;
    uint m_NextDecodeSequence;
    float m_LastInterpolation = -1.0f;
    uint m_LastPresentedSequence = uint.MaxValue;
    StreamingPlaybackState m_PlaybackState = StreamingPlaybackState.WaitingForKeyframe;

    public bool IsPlayable { get { return m_PlaybackState == StreamingPlaybackState.Playing; } }
    public StreamingPlaybackState PlaybackState { get { return m_PlaybackState; } }
    public int BufferedFrameCount { get { return m_DecodedFrames.Count; } }
    public double PresentedTime { get; private set; } = double.NaN;
    public double BufferedUntil { get { return m_DecodedFrames.Count == 0 ? double.NaN : m_DecodedFrames[m_DecodedFrames.Count - 1].presentationTime; } }

    public bool CanPlayAt(double time, double leadSeconds)
    {
      return m_DecodedFrames.Count >= m_PrebufferFrames &&
        m_DecodedFrames[0].presentationTime <= time + 0.001 &&
        BufferedUntil >= time + leadSeconds;
    }
    public bool IsUsingComputeShader
    {
      get { return IsGpuResident || (m_VertexContainer != null && m_VertexContainer.IsUsingComputeShader); }
    }

    public bool TryGetPlaybackStartTime(out double presentationTime)
    {
      if (m_DecodedFrames.Count > 0)
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
      set { m_CombinedFrames = Mathf.Max(1, value); }
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

    public void AddMesh(string name, Mesh mesh)
    {
      if (!m_MeshDictionary.ContainsKey(name))
      {
        m_MeshDictionary.Add(name, mesh);
        m_MeshList.Add(mesh);
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
      m_DecodedFrames.Clear();
      m_NeedsKeyframe = true;
      m_HasDecodeCursor = false;
      m_LastPresentedSequence = uint.MaxValue;
      m_ContainerSize = containerSize;
      m_PackageSize = packageSize;
      m_RecalculateNormals = NormalMode == ReceiverNormalMode.Recalculate ||
        (NormalMode == ReceiverNormalMode.Auto && UsesNormals());
      // The CPU decoder always remains a true CPU fallback, never a readback path.
      m_VertexContainer = new VertexContainer(packageSize, containerSize, false);
      if (enableComputeShader && DecodeBackend != ReceiverDecodeBackend.CPU)
      {
        try
        {
          m_GpuPipeline = new GpuVertexPipeline(m_MeshList, packageSize, containerSize,
            m_MaxDecodedFrames + 8, m_RecalculateNormals);
          m_MaxDecodedFrames = Math.Min(m_MaxDecodedFrames, Math.Max(3, m_GpuPipeline.Capacity - 2));
          Debug.Log("StreamingMesh GPU resident receiver: " + m_GpuPipeline.Capacity +
            " slots, " + m_GpuPipeline.PoolBytes + " pool bytes, normals=" + m_RecalculateNormals);
        }
        catch (Exception exception)
        {
          Debug.LogWarning("StreamingMesh GPU initialization unavailable; using CPU: " + exception.Message);
        }
      }
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
      m_DecodedFrames.RemoveAt(index);
    }

    void CollectGpuFrames()
    {
      while (m_PendingGpuFrames.Count > 0 && m_PendingGpuFrames.Peek().IsReady)
      {
        var frame = m_PendingGpuFrames.Dequeue();
        if (m_DecodedFrames.Count >= m_MaxDecodedFrames && m_DecodedFrames.Count > 2)
          RemoveDecodedFrame(1);
        m_DecodedFrames.Add(new DecodedFrame {sequence=frame.Sequence, presentationTime=frame.Time,
          rootPosition=frame.RootPosition, gpuFrame=frame});
      }
    }

    void FallBackFromGpu(Exception exception)
    {
      Debug.LogWarning("StreamingMesh GPU pipeline failed; waiting for a CPU keyframe: " + exception.Message);
      m_GpuPipeline?.Dispose();
      m_GpuPipeline = null;
      m_PendingGpuFrames.Clear();
      m_DecodedFrames.Clear();
      m_NeedsKeyframe = true;
      m_LastPresentedSequence = uint.MaxValue;
      m_VertexContainer.Dispose();
      m_VertexContainer = new VertexContainer(m_PackageSize, m_ContainerSize, false);
    }

    public void CreateVertexBuffer()
    {
      if (m_MeshList.Count == 0)
        return;

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

    public void AddVertexData(string name, byte[] data, long ticks)
    {
      if (!TryBeginChunk(name, data, out int index)) return;
      try { CommitChunk(ParseChunk(index, data, ticks, m_CombinedFrames, m_FrameInterval)); }
      catch (Exception exception) { RejectChunk(index, exception); }
    }

    // Called from the Unity thread: only byte parsing runs in the worker. Await
    // returns to Unity's synchronization context before touching playback state.
    public async Task<bool> AddVertexDataAsync(string name, byte[] data, long ticks)
    {
      if (m_PlaybackState == StreamingPlaybackState.Disposed || data == null || data.Length == 0) return false;
      if (!int.TryParse(name, out int index)) return false;
      if (!m_ReceivedChunks.Add(index)) return true;
      int combined = m_CombinedFrames;
      float interval = m_FrameInterval;
      try
      {
#if UNITY_WEBGL && !UNITY_EDITOR
        var frames = ParseChunk(index, data, ticks, combined, interval);
#else
        var frames = await Task.Run(() => ParseChunk(index, data, ticks, combined, interval));
#endif
        if (m_PlaybackState == StreamingPlaybackState.Disposed) return false;
        CommitChunk(frames);
        return true;
      }
      catch (Exception exception) { RejectChunk(index, exception); return false; }
    }

    bool TryBeginChunk(string name, byte[] data, out int index)
    {
      index = -1;
      return m_PlaybackState != StreamingPlaybackState.Disposed && data != null && data.Length > 0 &&
        int.TryParse(name, out index) && m_ReceivedChunks.Add(index);
    }

    void RejectChunk(int index, Exception exception)
    {
      m_ReceivedChunks.Remove(index);
      if (m_PlaybackState != StreamingPlaybackState.Disposed)
        Debug.LogWarning("StreamingMesh rejected stream chunk " + index + ": " + exception.Message);
    }

    void CommitChunk(List<EncodedFrame> frames)
    {
      // Nothing is committed until the entire chunk passes validation.
      foreach (var frame in frames)
        if ((!m_HasDecodeCursor || frame.sequence >= m_NextDecodeSequence) && !m_EncodedFrames.ContainsKey(frame.sequence))
          m_EncodedFrames.Add(frame.sequence, frame);
    }

    static List<EncodedFrame> ParseChunk(int chunkIndex, byte[] data, long ticks, int combined, float interval)
    {
      byte[] raw = StreamingMesh.Lib.ExternalTools.Decompress(data);
      if (raw.Length < sizeof(int)) throw new InvalidOperationException("Missing frame-count header.");
      int frameCount = BitConverter.ToInt32(raw, 0);
      int sizeTableBytes = checked((frameCount + 1) * sizeof(int));
      if (frameCount < 0 || sizeTableBytes > raw.Length) throw new InvalidOperationException("Invalid frame size table.");
      var frames = new List<EncodedFrame>(frameCount);
      int dataOffset = sizeTableBytes;
      double start = ticks / (double)TimeSpan.TicksPerSecond;
      for (int i = 0; i < frameCount; i++)
      {
        int size = BitConverter.ToInt32(raw, (i + 1) * sizeof(int));
        if (size < 21 || size > raw.Length - dataOffset) throw new InvalidOperationException("Invalid frame size.");
        byte[] frame = new byte[size];
        Buffer.BlockCopy(raw, dataOffset, frame, 0, size);
        dataOffset += size;
        if (frame[8] >= 2 && size < 29) throw new InvalidOperationException("Missing PTS header.");
        frames.Add(new EncodedFrame {
          sequence = frame[8] >= 1 ? BitConverter.ToUInt32(frame, 1) : (uint)(chunkIndex * combined + i),
          presentationTime = frame[8] >= 2 ? BitConverter.ToInt64(frame, 21) / (double)TimeSpan.TicksPerSecond : start + i * interval,
          isKeyframe = frame[0] == 0x0F, data = frame
        });
      }
      if (dataOffset != raw.Length) throw new InvalidOperationException("Unexpected trailing chunk data.");
      return frames;
    }

    public void UpdateWithTime(double updateTime)
    {
      if (m_PlaybackState == StreamingPlaybackState.Disposed)
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
        if (!PumpDecoder(updateTime) || m_DecodeInFlight ||
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
      if (m_DecodeInFlight || m_VertexContainer == null || m_VertexLayout == null)
        return false;
      double queuedUntil = BufferedUntil;
      foreach (var pending in m_PendingGpuFrames) queuedUntil = pending.Time;
      if (m_DecodedFrames.Count + m_PendingGpuFrames.Count >= m_PrebufferFrames &&
          queuedUntil >= playbackTime + Math.Max(DecodeAheadSeconds, m_FrameInterval * 2)) return false;
      EncodedFrame frame;
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
          if (!m_GpuPipeline.TrySubmit(frame.data, frame.sequence, frame.presentationTime, out gpuFrame, out error))
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
      m_DecodeInFlight = true;
      m_VertexContainer.DecodeAsync(frame.data, m_VertexLayout, result =>
      {
        m_DecodeInFlight = false;
        AdvanceDecodeCursor(frame.sequence, result.succeeded);

        if (!result.succeeded)
        {
          Debug.LogWarning("StreamingMesh frame " + frame.sequence + " was rejected: " + result.error);
          m_NeedsKeyframe = true;
          m_HasDecodeCursor = true;
          m_NextDecodeSequence = frame.sequence + 1;
          return;
        }

        m_DecodedFrames.Add(new DecodedFrame
        {
          sequence = frame.sequence,
          presentationTime = frame.presentationTime,
          rootPosition = result.rootPosition,
          vertices = result.vertices
        });
        m_DecodedFrames.Sort((left, right) => left.sequence.CompareTo(right.sequence));
        m_NeedsKeyframe = false;
        m_HasDecodeCursor = true;
        m_NextDecodeSequence = frame.sequence + 1;
      });
      return true;
    }

    void AdvanceDecodeCursor(uint sequence, bool succeeded)
    {
      var obsolete = new List<uint>();
      foreach (var pair in m_EncodedFrames)
      {
        if (pair.Key > sequence) break;
        obsolete.Add(pair.Key);
      }
      foreach (uint key in obsolete) m_EncodedFrames.Remove(key);
      m_NeedsKeyframe = !succeeded;
      m_HasDecodeCursor = true;
      m_NextDecodeSequence = sequence + 1;
    }

    bool TrySelectDecodeFrame(out EncodedFrame selected)
    {
      selected = null;
      if (m_NeedsKeyframe || !m_HasDecodeCursor)
      {
        foreach (KeyValuePair<uint, EncodedFrame> pair in m_EncodedFrames)
        {
          if (pair.Value.isKeyframe && (!m_HasDecodeCursor || pair.Key >= m_NextDecodeSequence))
          {
            selected = pair.Value;
            return true;
          }
        }
        return false;
      }

      if (m_EncodedFrames.TryGetValue(m_NextDecodeSequence, out selected))
        return true;

      foreach (KeyValuePair<uint, EncodedFrame> pair in m_EncodedFrames)
      {
        if (pair.Key > m_NextDecodeSequence && pair.Value.isKeyframe)
        {
          m_NeedsKeyframe = true;
          selected = pair.Value;
          return true;
        }
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
        if (m_RecalculateNormals) m_MeshList[meshIndex].RecalculateNormals();
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
      m_EncodedFrames.Clear();
      m_DecodedFrames.Clear();
      foreach (var mesh in m_MeshList) UnityEngine.Object.Destroy(mesh);
      foreach (var material in m_MaterialDictionary.Values) UnityEngine.Object.Destroy(material);
      foreach (var texture in m_TextureDictionary.Values) UnityEngine.Object.Destroy(texture);
      m_MeshList.Clear();
      m_MeshDictionary.Clear();
      m_MaterialDictionary.Clear();
      m_TextureDictionary.Clear();
      m_VertexLayout = null;
      m_InterpolatedVertices = null;
    }
  }
}
