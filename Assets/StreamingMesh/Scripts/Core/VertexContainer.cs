using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace StreamingMesh.Core
{
  public sealed class VertexDecodeResult
  {
    public bool succeeded;
    public bool isKeyframe;
    public uint sequence;
    public Vector3 rootPosition;
    public float[][] vertices;
    public string error;
  }

  /// <summary>
  /// Decodes the StreamingMesh wire format. Quantized keyframe and delta math is
  /// dispatched to a compute shader when the platform supports it; parsing and
  /// validation stay on the CPU because the wire format is variable length.
  /// </summary>
  public sealed class VertexContainer : IDisposable
  {
    [StructLayout(LayoutKind.Sequential)]
    struct KeyframeCommand
    {
      public uint vertexIndex;
      public Vector3 position;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DeltaCommand
    {
      public uint vertexIndex;
      public int x;
      public int y;
      public int z;
    }

    const int LegacyHeaderSize = 21;
    const int TimestampedHeaderSize = 29;
    const int ThreadGroupSize = 128;

    readonly int m_ContainerSize;
    readonly int m_PackageSize;
    readonly List<int> m_PackedIndices = new List<int>();

    ComputeShader m_DecodeShader;
    ComputeBuffer m_VertexBuffer;
    ComputeBuffer m_KeyframeCommandBuffer;
    ComputeBuffer m_DeltaCommandBuffer;
    int m_KeyframeKernel = -1;
    int m_DeltaKernel = -1;
    int m_KeyframeCapacity;
    int m_DeltaCapacity;
    bool m_UseCompute;
    bool m_HasKeyframe;
    bool m_ComputeFailureLogged;
    bool m_DecodeInFlight;
    bool m_DisposeRequested;

    int[] m_MeshOffsets;
    int[] m_MeshVertexCounts;
    Vector4[] m_FlatVertices;

    public bool IsUsingComputeShader { get { return m_UseCompute; } }
    public bool IsDecodeInFlight { get { return m_DecodeInFlight; } }

    public VertexContainer(int packageSize, int containerSize, bool enableComputeShader = true)
    {
      m_PackageSize = packageSize;
      m_ContainerSize = containerSize;

      if (enableComputeShader && SystemInfo.supportsComputeShaders)
      {
        m_DecodeShader = Resources.Load<ComputeShader>("VertexDecodeShader");
        if (m_DecodeShader != null)
        {
          try
          {
            m_KeyframeKernel = m_DecodeShader.FindKernel("DecodeKeyframe");
            m_DeltaKernel = m_DecodeShader.FindKernel("DecodeDelta");
            m_UseCompute = true;
          }
          catch (Exception exception)
          {
            Debug.LogWarning("StreamingMesh compute decoder is unavailable: " + exception.Message);
          }
        }
      }
    }

    public int Decode(
      byte[] source, ref float[][] dest,
      out float destX, out float destY, out float destZ,
      out int keyFrame)
    {
      destX = 0.0f;
      destY = 0.0f;
      destZ = 0.0f;
      keyFrame = 0;

      if (source == null || source.Length < LegacyHeaderSize || dest == null)
      {
        Debug.LogError("StreamingMesh frame is missing or shorter than its header.");
        return -1;
      }

      destX = BitConverter.ToSingle(source, 9);
      destY = BitConverter.ToSingle(source, 13);
      destZ = BitConverter.ToSingle(source, 17);

      if (!EnsureLayout(dest))
        return -1;

      if (source[0] == 0x0F)
      {
        List<KeyframeCommand> commands;
        List<int> packedIndices;
        if (!TryParseKeyframe(source, dest, out commands, out packedIndices))
          return -1;

#if UNITY_WEBGL
        ApplyKeyframeOnCpu(commands);
#else
        if (!ExecuteKeyframe(commands))
          ApplyKeyframeOnCpu(commands);
#endif

        m_PackedIndices.Clear();
        m_PackedIndices.AddRange(packedIndices);
        m_HasKeyframe = true;
        keyFrame = 1;
      }
      else if (source[0] == 0x0E)
      {
        if (!m_HasKeyframe)
          return -1;

        List<DeltaCommand> commands;
        if (!TryParseDelta(source, out commands))
          return -1;

#if UNITY_WEBGL
        ApplyDeltaOnCpu(commands);
#else
        if (!ExecuteDelta(commands))
          ApplyDeltaOnCpu(commands);
#endif
      }
      else
      {
        Debug.LogError("StreamingMesh frame has an unknown type: " + source[0]);
        return -1;
      }

      CopyFlatVerticesToDestination(dest);
      return 0;
    }

    public void DecodeAsync(
      byte[] source,
      float[][] destinationLayout,
      Action<VertexDecodeResult> completed)
    {
      if (completed == null)
        throw new ArgumentNullException(nameof(completed));

      if (m_DisposeRequested)
      {
        completed(CreateErrorResult(source, "StreamingMesh decoder is disposed."));
        return;
      }
      if (m_DecodeInFlight)
      {
        completed(CreateErrorResult(source, "A StreamingMesh GPU decode is already in flight."));
        return;
      }
      if (source == null || source.Length < LegacyHeaderSize || destinationLayout == null)
      {
        completed(CreateErrorResult(source, "StreamingMesh frame is missing or shorter than its header."));
        return;
      }
      if (!EnsureLayout(destinationLayout))
      {
        completed(CreateErrorResult(source, "StreamingMesh destination layout is invalid."));
        return;
      }

      bool isKeyframe = source[0] == 0x0F;
      uint sequence = BitConverter.ToUInt32(source, 1);
      Vector3 rootPosition = new Vector3(
        BitConverter.ToSingle(source, 9),
        BitConverter.ToSingle(source, 13),
        BitConverter.ToSingle(source, 17));

      List<KeyframeCommand> keyframeCommands = null;
      List<DeltaCommand> deltaCommands = null;
      if (isKeyframe)
      {
        List<int> packedIndices;
        if (!TryParseKeyframe(source, destinationLayout, out keyframeCommands, out packedIndices))
        {
          completed(CreateErrorResult(source, "StreamingMesh keyframe payload is invalid."));
          return;
        }

        m_PackedIndices.Clear();
        m_PackedIndices.AddRange(packedIndices);
        m_HasKeyframe = true;
      }
      else if (source[0] == 0x0E)
      {
        if (!m_HasKeyframe || !TryParseDelta(source, out deltaCommands))
        {
          completed(CreateErrorResult(source, "StreamingMesh delta frame has no valid base keyframe."));
          return;
        }
      }
      else
      {
        completed(CreateErrorResult(source, "StreamingMesh frame has an unknown type."));
        return;
      }

      if (!m_UseCompute || m_VertexBuffer == null || !SystemInfo.supportsAsyncGPUReadback)
      {
        if (isKeyframe)
          ApplyKeyframeOnCpu(keyframeCommands);
        else
          ApplyDeltaOnCpu(deltaCommands);
        completed(CreateSuccessResult(sequence, isKeyframe, rootPosition));
        return;
      }

      try
      {
        int commandCount;
        if (isKeyframe)
        {
          commandCount = keyframeCommands.Count;
          if (commandCount > 0)
          {
            EnsureCommandBuffer(ref m_KeyframeCommandBuffer, ref m_KeyframeCapacity, commandCount, 16);
            m_KeyframeCommandBuffer.SetData(keyframeCommands);
            m_DecodeShader.SetInt("commandCount", commandCount);
            m_DecodeShader.SetBuffer(m_KeyframeKernel, "keyframeCommands", m_KeyframeCommandBuffer);
            m_DecodeShader.SetBuffer(m_KeyframeKernel, "vertices", m_VertexBuffer);
            m_DecodeShader.Dispatch(m_KeyframeKernel, DivideRoundUp(commandCount, ThreadGroupSize), 1, 1);
          }
        }
        else
        {
          commandCount = deltaCommands.Count;
          if (commandCount > 0)
          {
            EnsureCommandBuffer(ref m_DeltaCommandBuffer, ref m_DeltaCapacity, commandCount, 16);
            m_DeltaCommandBuffer.SetData(deltaCommands);
            m_DecodeShader.SetInt("commandCount", commandCount);
            m_DecodeShader.SetBuffer(m_DeltaKernel, "deltaCommands", m_DeltaCommandBuffer);
            m_DecodeShader.SetBuffer(m_DeltaKernel, "vertices", m_VertexBuffer);
            m_DecodeShader.Dispatch(m_DeltaKernel, DivideRoundUp(commandCount, ThreadGroupSize), 1, 1);
          }
        }

        if (commandCount == 0)
        {
          completed(CreateSuccessResult(sequence, isKeyframe, rootPosition));
          return;
        }

        m_DecodeInFlight = true;
        AsyncGPUReadback.Request(m_VertexBuffer, request =>
        {
          VertexDecodeResult result;
          try
          {
            if (request.hasError)
            {
              m_HasKeyframe = false;
              result = CreateErrorResult(source, "StreamingMesh GPU readback failed.");
            }
            else
            {
              request.GetData<Vector4>().CopyTo(m_FlatVertices);
              result = CreateSuccessResult(sequence, isKeyframe, rootPosition);
            }
          }
          catch (Exception exception)
          {
            m_HasKeyframe = false;
            result = CreateErrorResult(source, exception.Message);
          }
          finally
          {
            m_DecodeInFlight = false;
            if (m_DisposeRequested)
              ReleaseComputeBuffers();
          }

          try
          {
            completed(result);
          }
          catch (Exception exception)
          {
            Debug.LogException(exception);
          }
        });
      }
      catch (Exception exception)
      {
        DisableCompute(exception);
        if (isKeyframe)
          ApplyKeyframeOnCpu(keyframeCommands);
        else
          ApplyDeltaOnCpu(deltaCommands);
        completed(CreateSuccessResult(sequence, isKeyframe, rootPosition));
      }
    }

    VertexDecodeResult CreateSuccessResult(uint sequence, bool isKeyframe, Vector3 rootPosition)
    {
      float[][] vertices = new float[m_MeshVertexCounts.Length][];
      for (int meshIndex = 0; meshIndex < vertices.Length; meshIndex++)
        vertices[meshIndex] = new float[m_MeshVertexCounts[meshIndex] * 3];
      CopyFlatVerticesToDestination(vertices);

      return new VertexDecodeResult
      {
        succeeded = true,
        sequence = sequence,
        isKeyframe = isKeyframe,
        rootPosition = rootPosition,
        vertices = vertices
      };
    }

    static VertexDecodeResult CreateErrorResult(byte[] source, string error)
    {
      return new VertexDecodeResult
      {
        succeeded = false,
        sequence = source != null && source.Length >= 5 ? BitConverter.ToUInt32(source, 1) : 0,
        isKeyframe = source != null && source.Length > 0 && source[0] == 0x0F,
        error = error
      };
    }

    bool EnsureLayout(float[][] destination)
    {
      int totalVertexCount = 0;
      bool layoutChanged = m_MeshVertexCounts == null || m_MeshVertexCounts.Length != destination.Length;
      int[] vertexCounts = new int[destination.Length];

      for (int i = 0; i < destination.Length; i++)
      {
        if (destination[i] == null || destination[i].Length % 3 != 0)
        {
          Debug.LogError("StreamingMesh destination vertex buffer has an invalid layout.");
          return false;
        }

        vertexCounts[i] = destination[i].Length / 3;
        if (!layoutChanged && vertexCounts[i] != m_MeshVertexCounts[i])
          layoutChanged = true;
        totalVertexCount += vertexCounts[i];
      }

      if (!layoutChanged)
        return true;

      m_MeshOffsets = new int[destination.Length];
      m_MeshVertexCounts = vertexCounts;
      if (m_DecodeInFlight)
      {
        Debug.LogError("StreamingMesh mesh layout changed while a GPU decode was in flight.");
        return false;
      }

      m_FlatVertices = new Vector4[totalVertexCount];

      int offset = 0;
      for (int meshIndex = 0; meshIndex < destination.Length; meshIndex++)
      {
        m_MeshOffsets[meshIndex] = offset;
        float[] meshVertices = destination[meshIndex];
        for (int vertexIndex = 0; vertexIndex < vertexCounts[meshIndex]; vertexIndex++)
        {
          int sourceIndex = vertexIndex * 3;
          m_FlatVertices[offset + vertexIndex] = new Vector4(
            meshVertices[sourceIndex], meshVertices[sourceIndex + 1], meshVertices[sourceIndex + 2], 1.0f);
        }
        offset += vertexCounts[meshIndex];
      }

      ReleaseBuffer(ref m_VertexBuffer);
      if (m_UseCompute && totalVertexCount > 0)
      {
        m_VertexBuffer = new ComputeBuffer(totalVertexCount, sizeof(float) * 4);
        m_VertexBuffer.SetData(m_FlatVertices);
      }

      m_PackedIndices.Clear();
      m_HasKeyframe = false;
      return true;
    }

    bool TryParseKeyframe(
      byte[] source,
      float[][] destination,
      out List<KeyframeCommand> commands,
      out List<int> packedIndices)
    {
      commands = new List<KeyframeCommand>();
      packedIndices = new List<int>();

      int headerSize = GetHeaderSize(source);
      if (source.Length < headerSize)
        return ReportBrokenFrame();

      int packageCount = source[5] | (source[6] << 8) | (source[7] << 16);
      int offset = headerSize;
      int halfPackage = m_PackageSize / 2;
      if (halfPackage <= 0 || m_ContainerSize <= 0)
      {
        Debug.LogError("StreamingMesh channel has invalid quantization settings.");
        return false;
      }

      float tileScale = (float)m_ContainerSize / halfPackage;
      float subTileScale = tileScale / 32.0f;

      for (int packageIndex = 0; packageIndex < packageCount; packageIndex++)
      {
        if (offset > source.Length - 6)
          return ReportBrokenFrame();

        float tileX = (source[offset] - halfPackage) * tileScale;
        float tileY = (source[offset + 1] - halfPackage) * tileScale;
        float tileZ = (source[offset + 2] - halfPackage) * tileScale;
        int vertexCount = source[offset + 3] | (source[offset + 4] << 8) | (source[offset + 5] << 16);
        offset += 6;

        long packageBytes = (long)vertexCount * 5L;
        if (packageBytes > source.Length - offset)
          return ReportBrokenFrame();

        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
          int vertexOffset = offset + vertex * 5;
          int vertexIndex = source[vertexOffset] | (source[vertexOffset + 1] << 8);
          int meshIndex = source[vertexOffset + 2];
          if (meshIndex >= destination.Length || vertexIndex >= m_MeshVertexCounts[meshIndex])
            return ReportBrokenFrame();

          int compressed = source[vertexOffset + 3] | (source[vertexOffset + 4] << 8);
          int globalVertexIndex = m_MeshOffsets[meshIndex] + vertexIndex;
          commands.Add(new KeyframeCommand
          {
            vertexIndex = (uint)globalVertexIndex,
            position = new Vector3(
              tileX + (compressed & 0x1F) * subTileScale,
              tileY + ((compressed >> 5) & 0x1F) * subTileScale,
              tileZ + ((compressed >> 10) & 0x1F) * subTileScale)
          });
          packedIndices.Add(globalVertexIndex);
        }

        offset += vertexCount * 5;
      }

      return true;
    }

    bool TryParseDelta(byte[] source, out List<DeltaCommand> commands)
    {
      commands = new List<DeltaCommand>(m_PackedIndices.Count);
      int headerSize = GetHeaderSize(source);
      long requiredSize = headerSize + (long)m_PackedIndices.Count * 3L;
      if (requiredSize > source.Length)
        return ReportBrokenFrame();

      for (int i = 0; i < m_PackedIndices.Count; i++)
      {
        int offset = headerSize + i * 3;
        commands.Add(new DeltaCommand
        {
          vertexIndex = (uint)m_PackedIndices[i],
          x = source[offset] - 128,
          y = source[offset + 1] - 128,
          z = source[offset + 2] - 128
        });
      }
      return true;
    }

    bool ExecuteKeyframe(List<KeyframeCommand> commands)
    {
      if (!m_UseCompute || m_VertexBuffer == null)
        return false;
      if (commands.Count == 0)
        return true;

      try
      {
        EnsureCommandBuffer(ref m_KeyframeCommandBuffer, ref m_KeyframeCapacity, commands.Count, 16);
        m_KeyframeCommandBuffer.SetData(commands);
        m_DecodeShader.SetInt("commandCount", commands.Count);
        m_DecodeShader.SetBuffer(m_KeyframeKernel, "keyframeCommands", m_KeyframeCommandBuffer);
        m_DecodeShader.SetBuffer(m_KeyframeKernel, "vertices", m_VertexBuffer);
        m_DecodeShader.Dispatch(m_KeyframeKernel, DivideRoundUp(commands.Count, ThreadGroupSize), 1, 1);
        m_VertexBuffer.GetData(m_FlatVertices);
        return true;
      }
      catch (Exception exception)
      {
        DisableCompute(exception);
        return false;
      }
    }

    bool ExecuteDelta(List<DeltaCommand> commands)
    {
      if (!m_UseCompute || m_VertexBuffer == null)
        return false;
      if (commands.Count == 0)
        return true;

      try
      {
        EnsureCommandBuffer(ref m_DeltaCommandBuffer, ref m_DeltaCapacity, commands.Count, 16);
        m_DeltaCommandBuffer.SetData(commands);
        m_DecodeShader.SetInt("commandCount", commands.Count);
        m_DecodeShader.SetBuffer(m_DeltaKernel, "deltaCommands", m_DeltaCommandBuffer);
        m_DecodeShader.SetBuffer(m_DeltaKernel, "vertices", m_VertexBuffer);
        m_DecodeShader.Dispatch(m_DeltaKernel, DivideRoundUp(commands.Count, ThreadGroupSize), 1, 1);
        m_VertexBuffer.GetData(m_FlatVertices);
        return true;
      }
      catch (Exception exception)
      {
        DisableCompute(exception);
        return false;
      }
    }

    void ApplyKeyframeOnCpu(List<KeyframeCommand> commands)
    {
      for (int i = 0; i < commands.Count; i++)
      {
        Vector3 position = commands[i].position;
        m_FlatVertices[commands[i].vertexIndex] = new Vector4(position.x, position.y, position.z, 1.0f);
      }
    }

    void ApplyDeltaOnCpu(List<DeltaCommand> commands)
    {
      const float deltaScale = 1.0f / 16384.0f;
      for (int i = 0; i < commands.Count; i++)
      {
        DeltaCommand command = commands[i];
        Vector4 position = m_FlatVertices[command.vertexIndex];
        position.x += DecodeDeltaComponent(command.x, deltaScale);
        position.y += DecodeDeltaComponent(command.y, deltaScale);
        position.z += DecodeDeltaComponent(command.z, deltaScale);
        m_FlatVertices[command.vertexIndex] = position;
      }
    }

    static float DecodeDeltaComponent(int value, float scale)
    {
      return value < 0 ? -(value * value) * scale : (value * value) * scale;
    }

    static int GetHeaderSize(byte[] source)
    {
      return source != null && source.Length > 8 && source[8] >= 2
        ? TimestampedHeaderSize
        : LegacyHeaderSize;
    }

    void CopyFlatVerticesToDestination(float[][] destination)
    {
      for (int meshIndex = 0; meshIndex < destination.Length; meshIndex++)
      {
        float[] meshVertices = destination[meshIndex];
        int offset = m_MeshOffsets[meshIndex];
        for (int vertexIndex = 0; vertexIndex < m_MeshVertexCounts[meshIndex]; vertexIndex++)
        {
          Vector4 vertex = m_FlatVertices[offset + vertexIndex];
          int destinationIndex = vertexIndex * 3;
          meshVertices[destinationIndex] = vertex.x;
          meshVertices[destinationIndex + 1] = vertex.y;
          meshVertices[destinationIndex + 2] = vertex.z;
        }
      }
    }

    static void EnsureCommandBuffer(ref ComputeBuffer buffer, ref int capacity, int requiredCount, int stride)
    {
      if (buffer != null && capacity >= requiredCount)
        return;

      ReleaseBuffer(ref buffer);
      capacity = Mathf.NextPowerOfTwo(requiredCount);
      buffer = new ComputeBuffer(capacity, stride);
    }

    void DisableCompute(Exception exception)
    {
      if (!m_ComputeFailureLogged)
      {
        Debug.LogWarning("StreamingMesh compute decode failed; continuing on CPU. " + exception.Message);
        m_ComputeFailureLogged = true;
      }

      m_UseCompute = false;
      ReleaseComputeBuffers();
    }

    static bool ReportBrokenFrame()
    {
      Debug.LogError("StreamingMesh frame data is invalid or does not match the channel mesh layout.");
      return false;
    }

    static int DivideRoundUp(int value, int divisor)
    {
      return (value + divisor - 1) / divisor;
    }

    static void ReleaseBuffer(ref ComputeBuffer buffer)
    {
      if (buffer == null)
        return;
      buffer.Release();
      buffer = null;
    }

    void ReleaseComputeBuffers()
    {
      ReleaseBuffer(ref m_VertexBuffer);
      ReleaseBuffer(ref m_KeyframeCommandBuffer);
      ReleaseBuffer(ref m_DeltaCommandBuffer);
      m_KeyframeCapacity = 0;
      m_DeltaCapacity = 0;
    }

    public void Dispose()
    {
      m_DisposeRequested = true;
      if (!m_DecodeInFlight)
        ReleaseComputeBuffers();
      m_UseCompute = false;
    }
  }
}
