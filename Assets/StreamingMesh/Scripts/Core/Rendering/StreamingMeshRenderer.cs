using System;
using System.Collections.Generic;
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
    public bool IsUsingComputeShader
    {
      get { return m_VertexContainer != null && m_VertexContainer.IsUsingComputeShader; }
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
      set { m_FrameInterval = Mathf.Max(0.001f, value); }
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

    public void CreateVertexContainer(int packageSize, int containerSize)
    {
      if (m_VertexContainer != null)
        m_VertexContainer.Dispose();
      m_VertexContainer = new VertexContainer(packageSize, containerSize);
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
      if (data == null || data.Length == 0)
      {
        Debug.LogError("StreamingMesh received an empty stream chunk.");
        return;
      }

      int chunkIndex;
      if (!int.TryParse(name, out chunkIndex) || !m_ReceivedChunks.Add(chunkIndex))
        return;

      try
      {
        byte[] raw = StreamingMesh.Lib.ExternalTools.Decompress(data);
        if (raw.Length < sizeof(int))
          throw new InvalidOperationException("Stream chunk is shorter than its frame-count header.");

        int frameCount = BitConverter.ToInt32(raw, 0);
        int sizeTableBytes = checked((frameCount + 1) * sizeof(int));
        if (frameCount < 0 || sizeTableBytes > raw.Length)
          throw new InvalidOperationException("Stream chunk has an invalid size table.");

        int dataOffset = sizeTableBytes;
        double chunkStartTime = ticks / (double)TimeSpan.TicksPerSecond;
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
          int frameSize = BitConverter.ToInt32(raw, (frameIndex + 1) * sizeof(int));
          if (frameSize < 21 || frameSize > raw.Length - dataOffset)
            throw new InvalidOperationException("Stream chunk contains an invalid frame size.");

          byte[] frameData = new byte[frameSize];
          Buffer.BlockCopy(raw, dataOffset, frameData, 0, frameSize);
          dataOffset += frameSize;

          bool hasSequenceHeader = frameData[8] >= 1;
          bool hasPresentationTimestamp = frameData[8] >= 2;
          if (hasPresentationTimestamp && frameSize < 29)
            throw new InvalidOperationException("Timestamped stream frame is shorter than its header.");
          uint sequence = hasSequenceHeader
            ? BitConverter.ToUInt32(frameData, 1)
            : (uint)(chunkIndex * m_CombinedFrames + frameIndex);
          if (!m_EncodedFrames.ContainsKey(sequence))
          {
            m_EncodedFrames.Add(sequence, new EncodedFrame
            {
              sequence = sequence,
              presentationTime = hasPresentationTimestamp
                ? BitConverter.ToInt64(frameData, 21) / (double)TimeSpan.TicksPerSecond
                : chunkStartTime + frameIndex * m_FrameInterval,
              isKeyframe = frameData[0] == 0x0F,
              data = frameData
            });
          }
        }
      }
      catch (Exception exception)
      {
        m_ReceivedChunks.Remove(chunkIndex);
        Debug.LogError("StreamingMesh failed to parse stream chunk " + name + ": " + exception.Message);
      }
    }

    public void UpdateWithTime(double updateTime)
    {
      if (m_PlaybackState == StreamingPlaybackState.Disposed)
        return;

      PumpDecoder();
      if (m_DecodedFrames.Count == 0)
      {
        m_PlaybackState = m_NeedsKeyframe
          ? StreamingPlaybackState.WaitingForKeyframe
          : StreamingPlaybackState.Buffering;
        return;
      }

      while (m_DecodedFrames.Count > 2 && m_DecodedFrames[1].presentationTime <= updateTime)
        m_DecodedFrames.RemoveAt(0);

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

    void PumpDecoder()
    {
      if (m_DecodeInFlight || m_VertexContainer == null || m_VertexLayout == null)
        return;
      if (m_DecodedFrames.Count >= m_MaxDecodedFrames)
        return;

      EncodedFrame frame;
      if (!TrySelectDecodeFrame(out frame))
        return;

      m_DecodeInFlight = true;
      m_VertexContainer.DecodeAsync(frame.data, m_VertexLayout, result =>
      {
        m_DecodeInFlight = false;
        m_EncodedFrames.Remove(frame.sequence);

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
    }

    bool TrySelectDecodeFrame(out EncodedFrame selected)
    {
      selected = null;
      if (m_NeedsKeyframe || !m_HasDecodeCursor)
      {
        foreach (KeyValuePair<uint, EncodedFrame> pair in m_EncodedFrames)
        {
          if (pair.Value.isKeyframe)
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
      if (m_LastPresentedSequence == frame.sequence && Mathf.Approximately(m_LastInterpolation, 0.0f))
        return;

      ApplyVertices(frame.vertices, frame.vertices, 0.0f);
      if (m_RootGameObject != null)
        m_RootGameObject.transform.localPosition = frame.rootPosition;
      m_LastPresentedSequence = frame.sequence;
      m_LastInterpolation = 0.0f;
    }

    void PresentInterpolated(DecodedFrame previous, DecodedFrame next, float interpolation)
    {
      if (m_LastPresentedSequence == previous.sequence && Mathf.Approximately(m_LastInterpolation, interpolation))
        return;

      ApplyVertices(previous.vertices, next.vertices, interpolation);
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
        m_MeshList[meshIndex].RecalculateNormals();
      }
    }

    public void Dispose()
    {
      m_PlaybackState = StreamingPlaybackState.Disposed;
      if (m_VertexContainer != null)
      {
        m_VertexContainer.Dispose();
        m_VertexContainer = null;
      }
      m_EncodedFrames.Clear();
      m_DecodedFrames.Clear();
    }
  }
}
