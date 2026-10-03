using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace StreamingMesh.Core.Rendering
{
  public enum ReceiverDecodeBackend { Auto, GPU, CPU }
  public enum ReceiverNormalMode { Auto, None, Recalculate }
  public enum ReceiverTangentMode { Auto, None, Recalculate }

  /// <summary>Ordered graphics-queue decode, bounded snapshots and direct Mesh GPU output.</summary>
  public sealed class GpuVertexPipeline : IDisposable
  {
    [StructLayout(LayoutKind.Sequential)]
    struct Tile { public uint start, count, data, origin; }

    public sealed class Frame
    {
      public uint Sequence { get; internal set; }
      public double Time { get; internal set; }
      public Vector3 RootPosition { get; internal set; }
      public Bounds Bounds { get; internal set; }
      internal ComputeBuffer input, tiles, vertices;
      internal uint[] upload;
      internal GraphicsFence fence;
      internal bool submitted, held;
#if UNITY_WEBGL && !UNITY_EDITOR
      internal bool readbackDone, readbackFailed;
      internal readonly Action<AsyncGPUReadbackRequest> readbackCallback;
      public Frame() { readbackCallback = CompleteReadback; }
      void CompleteReadback(AsyncGPUReadbackRequest request)
      {
        if (request.hasError) { readbackFailed = true; return; }
        try
        {
          Vector4 firstVertex = request.GetData<Vector4>()[0];
          if (!Finite(firstVertex.x) || !Finite(firstVertex.y) || !Finite(firstVertex.z))
            throw new InvalidOperationException("WebGPU decoded a non-finite vertex.");
          if (Sequence < 2) Debug.Log("StreamingMesh WebGPU decoded frame " + Sequence + " first vertex=" + firstVertex.ToString("F6"));
          readbackDone = true;
        }
        catch (Exception exception)
        {
          readbackFailed = true;
          Debug.LogWarning("StreamingMesh WebGPU decode probe failed: " + exception.Message);
        }
      }
      public bool IsReady
      {
        get
        {
          if (readbackFailed) throw new InvalidOperationException("WebGPU decode completion readback failed.");
          return submitted && readbackDone;
        }
      }
#else
      public bool IsReady { get { return submitted && fence.passed; } }
#endif
    }

    sealed class Output : IDisposable
    {
      public Mesh mesh;
      public GraphicsBuffer buffer;
      public ComputeBuffer triangles, faces, offsets, adjacency, uvCoefficients, faceTangents, faceBitangents;
      public int count, globalOffset, stride, normalOffset, tangentOffset, faceCount;
      public bool normals, tangents;
      public void Dispose()
      {
        buffer?.Dispose(); triangles?.Dispose(); faces?.Dispose();
        offsets?.Dispose(); adjacency?.Dispose();
        uvCoefficients?.Dispose(); faceTangents?.Dispose(); faceBitangents?.Dispose();
      }
    }

    readonly List<Frame> m_Frames = new List<Frame>();
    readonly List<Output> m_Outputs = new List<Output>();
    readonly List<Tile> m_Tiles = new List<Tile>();
    readonly int[] m_Offsets, m_Counts;
    readonly bool[] m_Seen;
    readonly int m_Count, m_PackageSize, m_ContainerSize, m_MaxInputBytes;
    CommandBuffer m_RestoreCommands, m_PresentCommands;
    ComputeShader m_Shader;
    ComputeBuffer m_State, m_PackedIndices, m_MeshOffsets;
    int m_KeyKernel, m_DeltaKernel, m_CopyKernel, m_PresentKernel, m_FaceKernel, m_NormalKernel;
    int m_FaceTangentKernel, m_VertexTangentKernel;
    bool m_HasKeyframe, m_Disposed;
    readonly bool m_MemoryDiagnostics;
    Bounds m_Bounds;
    public int Capacity { get { return m_Frames.Count; } }
    public long UploadArrayBytes { get; private set; }
    public int MaximumObservedInputBytes { get; private set; }
    public int PeakHeldSlots { get; private set; }
    public long PoolBytes { get; private set; }
    public long ModelBytes { get; private set; }
    public long TangentBytes { get; private set; }

    public GpuVertexPipeline(IList<Mesh> meshes, int packageSize, int containerSize,
      int requestedSlots, bool recalculateNormals, long poolBudgetBytes = 64L * 1024 * 1024)
      : this(meshes, packageSize, containerSize, requestedSlots,
          UniformFlags(meshes.Count, recalculateNormals), new bool[meshes.Count], poolBudgetBytes) { }

    public GpuVertexPipeline(IList<Mesh> meshes, int packageSize, int containerSize,
      int requestedSlots, bool[] recalculateNormals, bool[] recalculateTangents,
      long poolBudgetBytes = 64L * 1024 * 1024, bool memoryDiagnostics = false)
    {
      m_MemoryDiagnostics = memoryDiagnostics;
      if (recalculateNormals == null || recalculateTangents == null ||
          recalculateNormals.Length != meshes.Count || recalculateTangents.Length != meshes.Count)
        throw new ArgumentException("Vertex requirements must match the mesh count.");
      if (!SystemInfo.supportsComputeShaders)
        throw new NotSupportedException("Compute shaders are required.");
#if UNITY_WEBGL && !UNITY_EDITOR
      if (!SystemInfo.supportsAsyncGPUReadback)
        throw new NotSupportedException("WebGPU decode completion requires a small asynchronous readback.");
#else
      if (!SystemInfo.supportsGraphicsFence)
        throw new NotSupportedException("Graphics fences are required.");
#endif
      if (packageSize < 2 || packageSize > 254 || containerSize < 1 || meshes.Count > 256)
        throw new ArgumentException("Invalid channel quantization or mesh count.");
      m_PackageSize = packageSize; m_ContainerSize = containerSize;
      m_Offsets = new int[meshes.Count]; m_Counts = new int[meshes.Count];
      for (int i=0; i<meshes.Count; i++)
      {
        m_Offsets[i] = m_Count;
        m_Counts[i] = meshes[i].vertexCount;
        if (m_Counts[i] > 65536) throw new ArgumentException("Mesh exceeds the wire-format vertex limit.");
        m_Count = checked(m_Count + m_Counts[i]);
      }
      if (m_Count == 0) throw new ArgumentException("No vertices to decode.");
      m_Seen = new bool[m_Count];
      // Worst case: one nonempty tile per vertex. Fixed slot allocation prevents
      // buffer resizing or SetData overwrites while commands are still in flight.
      m_MaxInputBytes = checked(29 + m_Count * 11);
      int words = (m_MaxInputBytes + 3) / 4;
      long bytesPerSlot = (long)words * 4 + (long)m_Count * (16 + 16);
      int slots = (int)Math.Min(Math.Min(64, Math.Max(4, requestedSlots)), poolBudgetBytes / bytesPerSlot);
      if (slots < 4) throw new NotSupportedException("GPU snapshot pool exceeds the configured memory budget.");
      try
      {
        var asset = Resources.Load<ComputeShader>("ReceiverVertexPipeline");
        if (asset == null) throw new InvalidOperationException("ReceiverVertexPipeline compute asset is missing.");
        m_Shader = UnityEngine.Object.Instantiate(asset);
        m_KeyKernel = m_Shader.FindKernel("RestoreKeyframe");
        m_DeltaKernel = m_Shader.FindKernel("RestoreDelta");
        m_CopyKernel = m_Shader.FindKernel("Snapshot");
        m_PresentKernel = m_Shader.FindKernel("Present");
        m_FaceKernel = m_Shader.FindKernel("FaceNormals");
        m_NormalKernel = m_Shader.FindKernel("VertexNormals");
        m_FaceTangentKernel = m_Shader.FindKernel("FaceTangents");
        m_VertexTangentKernel = m_Shader.FindKernel("VertexTangents");
        foreach (int k in new[] {m_KeyKernel,m_DeltaKernel,m_CopyKernel,m_PresentKernel,m_FaceKernel,m_NormalKernel})
          if (!m_Shader.IsSupported(k)) throw new NotSupportedException("Receiver compute kernel is unsupported.");
        if (Array.Exists(recalculateTangents, value => value) &&
            (!m_Shader.IsSupported(m_FaceTangentKernel) || !m_Shader.IsSupported(m_VertexTangentKernel)))
          throw new NotSupportedException("Receiver tangent kernels are unsupported.");
        m_RestoreCommands = new CommandBuffer {name="StreamingMesh GPU restore"};
        m_PresentCommands = new CommandBuffer {name="StreamingMesh GPU presentation"};
        m_Tiles.Capacity = m_Count;
        m_State = new ComputeBuffer(m_Count, 16);
        m_PackedIndices = new ComputeBuffer(m_Count, 4);
        m_MeshOffsets = new ComputeBuffer(Math.Max(1, meshes.Count), 4);
        m_MeshOffsets.SetData(m_Offsets);
        for (int i=0; i<slots; i++)
        {
          var frame = new Frame(); m_Frames.Add(frame);
          frame.input = new ComputeBuffer(words, 4, ComputeBufferType.Raw);
          frame.tiles = new ComputeBuffer(m_Count, 16);
          frame.vertices = new ComputeBuffer(m_Count, 16);
          frame.upload = new uint[words];
          UploadArrayBytes += (long)words * 4;
        }
        PoolBytes = bytesPerSlot * slots;
        for (int i=0; i<meshes.Count; i++)
          CreateOutput(meshes[i], m_Offsets[i], recalculateNormals[i], recalculateTangents[i]);
      }
      catch { Dispose(); throw; }
    }

    static bool[] UniformFlags(int count, bool value)
    {
      var flags = new bool[count];
      for (int i=0; i<count; i++) flags[i] = value;
      return flags;
    }

    internal static bool CanRecalculateTangents(Mesh mesh)
    {
      return mesh.vertexCount > 0 && mesh.HasVertexAttribute(VertexAttribute.TexCoord0) &&
        mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0) >= 2 && mesh.triangles.Length >= 3;
    }

    void CreateOutput(Mesh mesh, int globalOffset, bool normals, bool tangents)
    {
      tangents = tangents && CanRecalculateTangents(mesh);
      var output = new Output {mesh=mesh, count=mesh.vertexCount, globalOffset=globalOffset,
        normals=normals || tangents, tangents=tangents};
      m_Outputs.Add(output);
      if (output.count == 0) return;
      var attributes = new List<VertexAttributeDescriptor> {
        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3)
      };
      if (tangents) attributes.Add(new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4));
      var uvChannels = new List<Vector4>[8];
      var dimensions = new int[8];
      int floats = tangents ? 10 : 6;
      for (int channel=0; channel<8; channel++)
      {
        var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
        if (!mesh.HasVertexAttribute(attribute)) continue;
        dimensions[channel] = mesh.GetVertexAttributeDimension(attribute);
        uvChannels[channel] = new List<Vector4>(); mesh.GetUVs(channel, uvChannels[channel]);
        attributes.Add(new VertexAttributeDescriptor(attribute, VertexAttributeFormat.Float32, dimensions[channel]));
        floats += dimensions[channel];
      }
      var positions = mesh.vertices;
      var initial = new float[output.count * floats];
      for (int i=0; i<output.count; i++)
      {
        int o=i*floats;
        initial[o]=positions[i].x; initial[o+1]=positions[i].y; initial[o+2]=positions[i].z;
        o+=6;
        if (tangents) { initial[o]=1; initial[o+3]=1; o+=4; }
        for (int c=0; c<8; c++)
          for (int d=0; d<dimensions[c]; d++) initial[o++]=uvChannels[c][i][d];
      }
      mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
      mesh.SetVertexBufferParams(output.count, attributes.ToArray());
      mesh.SetVertexBufferData(initial, 0, 0, initial.Length, 0, MeshUpdateFlags.DontRecalculateBounds);
      output.stride=mesh.GetVertexBufferStride(0);
      output.normalOffset=mesh.GetVertexAttributeOffset(VertexAttribute.Normal);
      if (tangents) output.tangentOffset=mesh.GetVertexAttributeOffset(VertexAttribute.Tangent);
      output.buffer=mesh.GetVertexBuffer(0);
      ModelBytes += (long)output.count * output.stride;
      if (!output.normals) return;
      var indices=mesh.triangles;
      output.faceCount=indices.Length/3;
      var offsets=new int[output.count+1];
      foreach (int index in indices) offsets[index+1]++;
      for (int i=1;i<offsets.Length;i++) offsets[i]+=offsets[i-1];
      var cursors=(int[])offsets.Clone();
      var adjacent=new int[Math.Max(1,indices.Length)];
      for (int i=0;i<indices.Length;i++) adjacent[cursors[indices[i]]++]=i/3;
      output.triangles=new ComputeBuffer(Math.Max(1,indices.Length),4);
      if(indices.Length>0) output.triangles.SetData(indices);
      output.faces=new ComputeBuffer(Math.Max(1,output.faceCount),16);
      output.offsets=new ComputeBuffer(offsets.Length,4); output.offsets.SetData(offsets);
      output.adjacency=new ComputeBuffer(adjacent.Length,4); output.adjacency.SetData(adjacent);
      ModelBytes += (long)output.triangles.count * 4 + (long)output.faces.count * 16 +
        (long)(output.offsets.count + output.adjacency.count) * 4;
      if (!tangents) return;
      // UVs and topology are fixed for a connection. Invalid/near-collinear UVs
      // contribute zero; the vertex kernel supplies a stable orthogonal basis.
      var uv = mesh.uv;
      var coefficients = new Vector4[Math.Max(1, output.faceCount)];
      for (int face=0; face<output.faceCount; face++)
      {
        Vector2 d1=uv[indices[face*3+1]]-uv[indices[face*3]];
        Vector2 d2=uv[indices[face*3+2]]-uv[indices[face*3]];
        double det=(double)d1.x*d2.y-(double)d1.y*d2.x;
        double scale=((double)d1.x*d1.x+(double)d1.y*d1.y)*
          ((double)d2.x*d2.x+(double)d2.y*d2.y);
        if (double.IsNaN(det) || double.IsInfinity(det) || det*det <= Math.Max(1e-60, 1e-12*scale)) continue;
        var coefficient=new Vector4((float)(d2.y/det), (float)(-d1.y/det),
          (float)(-d2.x/det), (float)(d1.x/det));
        if (Finite(coefficient.x) && Finite(coefficient.y) && Finite(coefficient.z) && Finite(coefficient.w))
          coefficients[face]=coefficient;
      }
      output.uvCoefficients=new ComputeBuffer(coefficients.Length,16); output.uvCoefficients.SetData(coefficients);
      output.faceTangents=new ComputeBuffer(coefficients.Length,16);
      output.faceBitangents=new ComputeBuffer(coefficients.Length,16);
      long scratchBytes=(long)coefficients.Length*48;
      ModelBytes += scratchBytes;
      TangentBytes += (long)output.count*16 + scratchBytes;
    }

    /// <summary>False with null error means bounded pool backpressure, not a broken frame.</summary>
    public bool TrySubmit(byte[] source, uint sequence, double time, out Frame frame, out string error)
    {
      return TrySubmit(source == null ? default(ArraySegment<byte>) : new ArraySegment<byte>(source), sequence, time, out frame, out error);
    }

    public bool TrySubmit(ArraySegment<byte> source, uint sequence, double time, out Frame frame, out string error)
    {
      frame=null; error=null;
      if(m_Disposed) throw new ObjectDisposedException(nameof(GpuVertexPipeline));
      foreach(var candidate in m_Frames)
        if(!candidate.held && (!candidate.submitted || candidate.IsReady)) {frame=candidate;break;}
      if(frame==null) return false;
      if(!Validate(source, out var bounds, out error)) {frame=null; m_HasKeyframe=false; return false;}
      if (m_MemoryDiagnostics)
      {
        MaximumObservedInputBytes = Math.Max(MaximumObservedInputBytes,source.Count);
        int heldSlots=1; foreach (var slot in m_Frames) if (slot.held && slot != frame) heldSlots++;
        PeakHeldSlots = Math.Max(PeakHeldSlots,heldSlots);
      }
      int words=(source.Count+3)/4;
#if UNITY_WEBGL && !UNITY_EDITOR
      frame.readbackDone=false; frame.readbackFailed=false;
#endif
      frame.upload[words-1]=0;
      Buffer.BlockCopy(source.Array,source.Offset,frame.upload,0,source.Count);
      frame.input.SetData(frame.upload,0,0,words);
      bool key=source[0]==0x0f;
      if(key) frame.tiles.SetData(m_Tiles);
      var commands = m_RestoreCommands; commands.Clear();
      frame.Sequence=sequence; frame.Time=time;
      {
        int k=key?m_KeyKernel:m_DeltaKernel;
        commands.SetComputeIntParam(m_Shader,"vertexCount",m_Count);
        commands.SetComputeIntParam(m_Shader,"headerSize",source[8]>=2?29:21);
        commands.SetComputeBufferParam(m_Shader,k,"inputBytes",frame.input);
        commands.SetComputeBufferParam(m_Shader,k,"packedIndices",m_PackedIndices);
        commands.SetComputeBufferParam(m_Shader,k,"stateVertices",m_State);
        if(key)
        {
          commands.SetComputeIntParam(m_Shader,"tileCount",m_Tiles.Count);
          commands.SetComputeIntParam(m_Shader,"halfPackage",m_PackageSize/2);
          float scale=(float)m_ContainerSize/(m_PackageSize/2);
          commands.SetComputeFloatParam(m_Shader,"tileScale",scale);
          commands.SetComputeFloatParam(m_Shader,"subTileScale",scale/32);
          commands.SetComputeBufferParam(m_Shader,k,"tiles",frame.tiles);
          commands.SetComputeBufferParam(m_Shader,k,"meshOffsets",m_MeshOffsets);
        }
        commands.DispatchCompute(m_Shader,k,Groups(m_Count),1,1);
        commands.SetComputeBufferParam(m_Shader,m_CopyKernel,"stateVertices",m_State);
        commands.SetComputeBufferParam(m_Shader,m_CopyKernel,"snapshotVertices",frame.vertices);
        commands.DispatchCompute(m_Shader,m_CopyKernel,Groups(m_Count),1,1);
#if UNITY_WEBGL && !UNITY_EDITOR
        commands.RequestAsyncReadback(frame.vertices, 16, 0, frame.readbackCallback);
#else
        frame.fence=commands.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation,SynchronisationStageFlags.AllGPUOperations);
#endif
        Graphics.ExecuteCommandBuffer(commands);
      }
      frame.Sequence=sequence; frame.Time=time; frame.Bounds=bounds;
      frame.RootPosition=new Vector3(BitConverter.ToSingle(source.Array,source.Offset+9),BitConverter.ToSingle(source.Array,source.Offset+13),BitConverter.ToSingle(source.Array,source.Offset+17));
      frame.held=true; frame.submitted=true;
      m_HasKeyframe=true; m_Bounds=bounds;
      return true;
    }

    bool Validate(ArraySegment<byte> source, out Bounds bounds, out string error)
    {
      bounds=m_Bounds; error="Invalid GPU frame payload.";
      if(source.Array==null || source.Count<21 || source.Count>m_MaxInputBytes) return false;
      int header=source[8]>=2?29:21;
      if(source.Count<header) return false;
      for(int p=9;p<21;p+=4) if(!Finite(BitConverter.ToSingle(source.Array,source.Offset+p))) return false;
      if(source[0]==0x0e)
      {
        if(!m_HasKeyframe || source.Count!=header+m_Count*3) return false;
        // A conservative CPU bound from encoded bytes, without expanding vertex positions.
        int maxX=0, maxY=0, maxZ=0;
        for(int i=header;i<source.Count;i+=3)
        {
          int x=source[i]-128,y=source[i+1]-128,z=source[i+2]-128;
          x*=x; y*=y; z*=z;
          if(x>maxX) maxX=x;
          if(y>maxY) maxY=y;
          if(z>maxZ) maxZ=z;
        }
        bounds.extents+=new Vector3(maxX/16384f,maxY/16384f,maxZ/16384f); error=null; return true;
      }
      if(source[0]!=0x0f) return false;
      Array.Clear(m_Seen,0,m_Seen.Length); m_Tiles.Clear();
      int packageCount=source[5]|source[6]<<8|source[7]<<16, offset=header, total=0;
      float scale=(float)m_ContainerSize/(m_PackageSize/2);
      bool first=true;
      for(int t=0;t<packageCount;t++)
      {
        if(offset>source.Count-6) return false;
        int n=source[offset+3]|source[offset+4]<<8|source[offset+5]<<16;
        if(n<=0 || n>m_Count-total || (long)n*5>source.Count-offset-6) return false;
        m_Tiles.Add(new Tile {start=(uint)total,count=(uint)n,data=(uint)(offset+6),origin=(uint)offset});
        var min=new Vector3(source[offset]-m_PackageSize/2,source[offset+1]-m_PackageSize/2,source[offset+2]-m_PackageSize/2)*scale;
        if(first) {bounds=new Bounds(min,Vector3.zero);first=false;}
        bounds.Encapsulate(min); bounds.Encapsulate(min+Vector3.one*scale);
        offset+=6;
        for(int v=0;v<n;v++,offset+=5)
        {
          int local=source[offset]|source[offset+1]<<8, mesh=source[offset+2];
          if(mesh>=m_Counts.Length || local>=m_Counts[mesh]) return false;
          int global=m_Offsets[mesh]+local;
          if(m_Seen[global]) return false;
          m_Seen[global]=true;
        }
        total+=n;
      }
      if(total!=m_Count || offset!=source.Count) return false;
      error=null; return true;
    }

    public void Release(Frame frame) { if(frame!=null) frame.held=false; }

    public void Present(Frame previous, Frame next, float interpolation)
    {
      if(m_Disposed) throw new ObjectDisposedException(nameof(GpuVertexPipeline));
      var bounds=previous.Bounds; bounds.Encapsulate(next.Bounds.min); bounds.Encapsulate(next.Bounds.max);
      var commands = m_PresentCommands; commands.Clear();
      {
        commands.SetComputeFloatParam(m_Shader,"interpolation",interpolation);
        commands.SetComputeBufferParam(m_Shader,m_PresentKernel,"previousVertices",previous.vertices);
        commands.SetComputeBufferParam(m_Shader,m_PresentKernel,"nextVertices",next.vertices);
        foreach(var o in m_Outputs)
        {
          if(o.count==0) continue;
          o.mesh.bounds=bounds;
          commands.SetComputeIntParam(m_Shader,"vertexCount",o.count);
          commands.SetComputeIntParam(m_Shader,"meshOffset",o.globalOffset);
          commands.SetComputeIntParam(m_Shader,"meshStride",o.stride);
          commands.SetComputeBufferParam(m_Shader,m_PresentKernel,"meshVertices",o.buffer);
          commands.DispatchCompute(m_Shader,m_PresentKernel,Groups(o.count),1,1);
          if(!o.normals) continue;
          commands.SetComputeIntParam(m_Shader,"normalOffset",o.normalOffset);
          commands.SetComputeIntParam(m_Shader,"faceCount",o.faceCount);
          commands.SetComputeBufferParam(m_Shader,m_FaceKernel,"meshVertices",o.buffer);
          commands.SetComputeBufferParam(m_Shader,m_FaceKernel,"triangleIndices",o.triangles);
          commands.SetComputeBufferParam(m_Shader,m_FaceKernel,"faceNormals",o.faces);
          if(o.faceCount>0) commands.DispatchCompute(m_Shader,m_FaceKernel,Groups(o.faceCount),1,1);
          commands.SetComputeBufferParam(m_Shader,m_NormalKernel,"meshVertices",o.buffer);
          commands.SetComputeBufferParam(m_Shader,m_NormalKernel,"faceNormals",o.faces);
          commands.SetComputeBufferParam(m_Shader,m_NormalKernel,"adjacencyOffsets",o.offsets);
          commands.SetComputeBufferParam(m_Shader,m_NormalKernel,"adjacencyFaces",o.adjacency);
          commands.DispatchCompute(m_Shader,m_NormalKernel,Groups(o.count),1,1);
          if(!o.tangents) continue;
          commands.SetComputeIntParam(m_Shader,"tangentOffset",o.tangentOffset);
          commands.SetComputeBufferParam(m_Shader,m_FaceTangentKernel,"meshVertices",o.buffer);
          commands.SetComputeBufferParam(m_Shader,m_FaceTangentKernel,"triangleIndices",o.triangles);
          commands.SetComputeBufferParam(m_Shader,m_FaceTangentKernel,"faceNormals",o.faces);
          commands.SetComputeBufferParam(m_Shader,m_FaceTangentKernel,"uvCoefficients",o.uvCoefficients);
          commands.SetComputeBufferParam(m_Shader,m_FaceTangentKernel,"faceTangents",o.faceTangents);
          commands.SetComputeBufferParam(m_Shader,m_FaceTangentKernel,"faceBitangents",o.faceBitangents);
          if(o.faceCount>0) commands.DispatchCompute(m_Shader,m_FaceTangentKernel,Groups(o.faceCount),1,1);
          commands.SetComputeBufferParam(m_Shader,m_VertexTangentKernel,"meshVertices",o.buffer);
          commands.SetComputeBufferParam(m_Shader,m_VertexTangentKernel,"faceTangents",o.faceTangents);
          commands.SetComputeBufferParam(m_Shader,m_VertexTangentKernel,"faceBitangents",o.faceBitangents);
          commands.SetComputeBufferParam(m_Shader,m_VertexTangentKernel,"adjacencyOffsets",o.offsets);
          commands.SetComputeBufferParam(m_Shader,m_VertexTangentKernel,"adjacencyFaces",o.adjacency);
          commands.DispatchCompute(m_Shader,m_VertexTangentKernel,Groups(o.count),1,1);
        }
        Graphics.ExecuteCommandBuffer(commands);
      }
    }

    static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    static int Groups(int count) {return (count+127)/128;}

    public void Dispose()
    {
      if(m_Disposed) return;
      m_Disposed=true;
      foreach(var frame in m_Frames) {frame.input?.Dispose();frame.tiles?.Dispose();frame.vertices?.Dispose();}
      foreach(var output in m_Outputs) output.Dispose();
      m_Frames.Clear(); m_Outputs.Clear();
      m_RestoreCommands?.Dispose(); m_PresentCommands?.Dispose();
      m_State?.Dispose();m_PackedIndices?.Dispose();m_MeshOffsets?.Dispose();
      if(m_Shader!=null)
      {
        if(Application.isPlaying) UnityEngine.Object.Destroy(m_Shader);
        else UnityEngine.Object.DestroyImmediate(m_Shader);
      }
    }
  }
}
