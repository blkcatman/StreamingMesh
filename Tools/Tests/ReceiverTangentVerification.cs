// Copy into an Editor folder, then run -executeMethod ReceiverTangentVerification.Run.
// GPU readbacks below are verification-only. No recorded channel or sample assets are required.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using StreamingMesh.Core.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class ReceiverTangentVerification
{
  const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
  static int checks;
  static float maxCpuGpuError;

  public static void Run()
  {
    try
    {
      VerifyGpu();
      VerifySharedVertices();
      VerifySelectionAndCpu();
      VerifyRecovery();
      MeasureCpu();
      Debug.Log("PASS receiver tangents: " + checks + " checks, CPU/GPU error=" + maxCpuGpuError +
        ", device=" + SystemInfo.graphicsDeviceName);
    }
    catch (Exception exception)
    {
      Debug.LogException(exception);
      EditorApplication.Exit(1);
      throw;
    }
  }

  static void Check(bool condition, string message)
  {
    checks++;
    if (!condition) throw new InvalidOperationException(message);
  }

  static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

  static Mesh Fixture()
  {
    var mesh = new Mesh { name="tangent-fixture" };
    mesh.vertices = new[] {
      new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(0,1,0),
      new Vector3(2,0,0), new Vector3(3,0,0), new Vector3(2,1,0),
      new Vector3(0,2,0), new Vector3(1,2,0), new Vector3(0,3,0),
      new Vector3(2,2,0), new Vector3(2,2,0), new Vector3(2,2,0),
      new Vector3(0,0,2), // isolated
      new Vector3(0,0,0), new Vector3(0,1,0), new Vector3(0,0,1) // split hard edge
    };
    mesh.uv = new[] {
      Vector2.zero, Vector2.right, Vector2.up,
      Vector2.right, Vector2.zero, Vector2.one, // mirrored U
      Vector2.zero, Vector2.zero, Vector2.zero, // collapsed UV
      Vector2.zero, Vector2.right, Vector2.up,
      Vector2.zero,
      Vector2.zero, Vector2.right, Vector2.up
    };
    var uv3 = new List<Vector3>(); var uv4 = new List<Vector4>();
    for (int i=0; i<mesh.vertexCount; i++) { uv3.Add(new Vector3(i,2,3)); uv4.Add(new Vector4(i,4,5,6)); }
    mesh.SetUVs(2, uv3); mesh.SetUVs(3, uv4);
    mesh.subMeshCount=2;
    mesh.SetTriangles(new[] {0,1,2,3,4,5},0);
    mesh.SetTriangles(new[] {6,7,8,9,10,11,13,14,15},1);
    return mesh;
  }

  // Complete keyframes; each vertex has its own tile with exact 1/16-unit positions.
  static byte[] Keyframe(IList<Vector3[]> meshes, uint sequence=0)
  {
    using (var stream=new MemoryStream())
    using (var writer=new BinaryWriter(stream))
    {
      int count=0; foreach (var vertices in meshes) count+=vertices.Length;
      writer.Write((byte)0x0f); writer.Write(sequence);
      writer.Write((byte)count); writer.Write((byte)(count>>8)); writer.Write((byte)(count>>16));
      writer.Write((byte)2); writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write((long)sequence*10000000);
      for (int m=0; m<meshes.Count; m++) for (int v=0; v<meshes[m].Length; v++)
      {
        var p=meshes[m][v];
        writer.Write((byte)(64+Mathf.RoundToInt(p.x*16)));
        writer.Write((byte)(64+Mathf.RoundToInt(p.y*16)));
        writer.Write((byte)(64+Mathf.RoundToInt(p.z*16)));
        writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)v); writer.Write((byte)m); writer.Write((ushort)0);
      }
      return stream.ToArray();
    }
  }

  static byte[] Chunk(params byte[][] frames)
  {
    using (var stream=new MemoryStream())
    using (var writer=new BinaryWriter(stream))
    {
      writer.Write(frames.Length);
      foreach(var frame in frames) writer.Write(frame.Length);
      foreach(var frame in frames) writer.Write(frame);
      return StreamingMesh.Lib.ExternalTools.Compress(stream.ToArray());
    }
  }

  static float[] Read(Mesh mesh)
  {
    var data=new float[mesh.vertexCount*mesh.GetVertexBufferStride(0)/4];
    using (var buffer=mesh.GetVertexBuffer(0)) buffer.GetData(data);
    return data;
  }

  static Vector3 V3(float[] data, int index) { return new Vector3(data[index],data[index+1],data[index+2]); }

  static void VerifyGpu()
  {
    var mesh=Fixture(); var skip=Fixture(); var empty=new Mesh();
    var initial=mesh.vertices; var next=(Vector3[])initial.Clone();
    next[1]=new Vector3(0,1,1); next[2]=new Vector3(-1,0,0);
    var uv=mesh.uv;
    using(var gpu=new GpuVertexPipeline(new[] {mesh,skip,empty},128,4,4,
      new[] {false,false,false},new[] {true,false,true}))
    {
      Check(mesh.HasVertexAttribute(VertexAttribute.Tangent),"Missing GPU tangent attribute");
      Check(!skip.HasVertexAttribute(VertexAttribute.Tangent),"Unrequested mesh got tangent attribute");
      Check(gpu.TangentBytes==16*mesh.vertexCount+48*5,"Tangent memory accounting");
      Check(mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord2)==3 &&
        mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord3)==4,"UV dimensions changed");
      Check(mesh.subMeshCount==2 && mesh.GetTriangles(1).Length==9,"Submeshes changed");
      Check(gpu.TrySubmit(Keyframe(new[] {initial,initial,new Vector3[0]}),0,0,out var a,out var error),error);
      Check(gpu.TrySubmit(Keyframe(new[] {next,initial,new Vector3[0]},1),1,1,out var b,out error),error);
      foreach(float alpha in new[] {0f,0.5f,1f})
      {
        gpu.Present(a,b,alpha);
        var data=Read(mesh);
        int stride=mesh.GetVertexBufferStride(0)/4;
        int no=mesh.GetVertexAttributeOffset(VertexAttribute.Normal)/4;
        int to=mesh.GetVertexAttributeOffset(VertexAttribute.Tangent)/4;
        int uo=mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0)/4;
        var positions=new Vector3[mesh.vertexCount];
        for(int v=0;v<mesh.vertexCount;v++)
        {
          positions[v]=Vector3.LerpUnclamped(initial[v],next[v],alpha);
          Check(Vector3.Distance(V3(data,v*stride),positions[v])<1e-5f,"GPU presented wrong position");
          var n=V3(data,v*stride+no); var t=V3(data,v*stride+to);
          Check(Finite(t.x)&&Finite(t.y)&&Finite(t.z)&&Finite(data[v*stride+to+3]),"Non-finite tangent");
          Check(Mathf.Abs(t.magnitude-1)<1e-5f && Mathf.Abs(Vector3.Dot(n,t))<1e-5f,"Invalid tangent basis");
          Check(Mathf.Abs(data[v*stride+to+3])==1,"Invalid tangent handedness");
          Check(data[v*stride+uo]==uv[v].x && data[v*stride+uo+1]==uv[v].y,"UV0 corrupted");
        }
        Check(data[3*stride+to+3]==-1,"Mirrored UV handedness lost");
        Check(Vector3.Distance(V3(data,to), (positions[1]-positions[0]).normalized)<1e-5f,
          "Tangents must follow the presented shape, including PTS midpoint");
        Check(Vector3.Distance(V3(data,13*stride+to),Vector3.up)<1e-5f,"Split hard edge averaged");
        var cpu=Fixture(); cpu.vertices=positions; cpu.RecalculateNormals(); cpu.RecalculateTangents();
        foreach(int v in new[] {0,1,2,3,4,5,13,14,15})
        {
          float difference=Vector3.Distance(V3(data,v*stride+to),(Vector3)cpu.tangents[v]);
          maxCpuGpuError=Mathf.Max(maxCpuGpuError,difference);
          Check(difference<1e-5f && data[v*stride+to+3]==cpu.tangents[v].w,"CPU/GPU simple-face mismatch");
        }
        UnityEngine.Object.DestroyImmediate(cpu);
      }
      // Non-finite UV data is rejected at initialization; no NaNs reach output.
      gpu.Release(a); gpu.Release(b);
      // Invalid input invalidates the delta base, then a keyframe restores it.
      var invalid=Keyframe(new[] {initial,initial,new Vector3[0]}); invalid[5]=255;
      Check(!gpu.TrySubmit(invalid,2,2,out var ignored,out error)&&error!=null,"Bad frame accepted");
      Check(gpu.TrySubmit(Keyframe(new[] {initial,initial,new Vector3[0]},3),3,3,out a,out error),"Keyframe recovery failed");
      gpu.Present(a,a,0); Read(mesh);
    }
    UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(skip); UnityEngine.Object.DestroyImmediate(empty);
    foreach(bool badUv in new[] {false,true})
    {
      mesh=Fixture(); initial=mesh.vertices;
      if(badUv) {var values=mesh.uv; values[0]=new Vector2(float.NaN,float.PositiveInfinity); mesh.uv=values;}
      else { initial[1]=new Vector3(float.NaN,0,0); }
      using(var gpu=new GpuVertexPipeline(new[] {mesh},128,4,4,new[] {false},new[] {true}))
      {
        // Test-only injection exercises the non-finite geometry guard independently of wire validation.
        gpu.TrySubmit(Keyframe(new[] {mesh.vertices}),0,0,out var frame,out var error);
        var buffer=(ComputeBuffer)typeof(GpuVertexPipeline.Frame).GetField("vertices",Private).GetValue(frame);
        var positions=new Vector4[initial.Length]; for(int i=0;i<initial.Length;i++) positions[i]=initial[i];
        buffer.SetData(positions); gpu.Present(frame,frame,0);
        var data=Read(mesh); int stride=mesh.GetVertexBufferStride(0)/4, to=mesh.GetVertexAttributeOffset(VertexAttribute.Tangent)/4;
        for(int v=0;v<mesh.vertexCount;v++) for(int d=0;d<4;d++) Check(Finite(data[v*stride+to+d]),"Non-finite input escaped guard");
      }
      UnityEngine.Object.DestroyImmediate(mesh);
    }
  }

  static void VerifySelectionAndCpu()
  {
    foreach(var mode in new[] {ReceiverTangentMode.Auto,ReceiverTangentMode.None,ReceiverTangentMode.Recalculate})
    {
      using(var renderer=new StreamingMeshRenderer {DecodeBackend=ReceiverDecodeBackend.CPU,
        NormalMode=ReceiverNormalMode.None,TangentMode=mode})
      {
        var target=Fixture(); var unlit=Fixture(); var noUv=Fixture(); noUv.uv=null;
        renderer.AddMesh("target",target,new[] {"unlit","mapped\0"});
        renderer.AddMesh("unlit",unlit,new[] {"unlit"});
        renderer.AddMesh("no-uv",noUv,new[] {"mapped"});
        renderer.AddMesh("empty",new Mesh(),new[] {"mapped"});
        renderer.TangentMaterialIds.Add("mapped");
        renderer.CreateVertexBuffer(); renderer.CreateVertexContainer(128,4);
        var shape=target.vertices; var next=(Vector3[])shape.Clone(); next[1]=new Vector3(0,1,1); next[2]=new Vector3(-1,0,0);
        renderer.AddVertexData("0",Chunk(Keyframe(new[] {shape,shape,shape,new Vector3[0]}),
          Keyframe(new[] {next,shape,shape,new Vector3[0]},1)),0);
        for(int i=0;i<4;i++) renderer.UpdateWithTime(0.5);
        bool enabled=mode!=ReceiverTangentMode.None;
        Check(target.HasVertexAttribute(VertexAttribute.Tangent)==enabled,"CPU tangent mode selection");
        Check(unlit.HasVertexAttribute(VertexAttribute.Tangent)==(mode==ReceiverTangentMode.Recalculate),"Per-mesh selection");
        Check(!noUv.HasVertexAttribute(VertexAttribute.Tangent),"Missing UV must skip tangents");
        if(enabled)
        {
          Check(target.normals.Length==target.vertexCount,"Tangents did not enable normals");
          Check(Vector3.Distance((Vector3)target.tangents[0],new Vector3(0.5f,0.5f,0.5f).normalized)<1e-5f,"CPU midpoint tangents stale");
          foreach(var t in target.tangents) Check(Finite(t.x)&&Finite(t.y)&&Finite(t.z)&&Finite(t.w),"CPU degenerate tangent");
        }
      }
    }
  }

  static void VerifySharedVertices()
  {
    // Two differently oriented faces sharing an edge. Tangents sum uniformly;
    // normals retain the existing area weighting. This is not a MikkTSpace test.
    var mesh=new Mesh {vertices=new[] {Vector3.zero,Vector3.right,Vector3.up,new Vector3(1,1,1)},
      uv=new[] {Vector2.zero,Vector2.right,Vector2.up,Vector2.one},triangles=new[] {0,1,2,1,3,2}};
    var positions=mesh.vertices;
    using(var gpu=new GpuVertexPipeline(new[] {mesh},128,4,4,new[] {false},new[] {true}))
    {
      Check(gpu.TrySubmit(Keyframe(new[] {positions}),0,0,out var frame,out var error),error);
      gpu.Present(frame,frame,0);
      var data=Read(mesh); int stride=mesh.GetVertexBufferStride(0)/4;
      int no=mesh.GetVertexAttributeOffset(VertexAttribute.Normal)/4, to=mesh.GetVertexAttributeOffset(VertexAttribute.Tangent)/4;
      var normals=new Vector3[4]; var tangents=new Vector3[4]; var bitangents=new Vector3[4];
      var indices=mesh.triangles; var uv=mesh.uv;
      for(int f=0;f<indices.Length;f+=3)
      {
        int a=indices[f],b=indices[f+1],c=indices[f+2];
        Vector3 e1=positions[b]-positions[a],e2=positions[c]-positions[a];
        Vector2 d1=uv[b]-uv[a],d2=uv[c]-uv[a]; float det=d1.x*d2.y-d1.y*d2.x;
        Vector3 n=Vector3.Cross(e1,e2),t=(e1*d2.y-e2*d1.y)/det,bt=(e2*d1.x-e1*d2.x)/det;
        foreach(int v in new[] {a,b,c}) {normals[v]+=n; tangents[v]+=t; bitangents[v]+=bt;}
      }
      for(int v=0;v<4;v++)
      {
        var n=normals[v].normalized; var t=(tangents[v]-n*Vector3.Dot(n,tangents[v])).normalized;
        float w=Vector3.Dot(Vector3.Cross(n,t),bitangents[v])<0?-1:1;
        Check(Vector3.Distance(V3(data,v*stride+no),n)<1e-5f,"Shared normal aggregation");
        Check(Vector3.Distance(V3(data,v*stride+to),t)<1e-5f && data[v*stride+to+3]==w,"Shared tangent aggregation");
      }
    }
    UnityEngine.Object.DestroyImmediate(mesh);
  }

  static void MeasureCpu()
  {
    foreach(var mode in new[] {ReceiverTangentMode.None,ReceiverTangentMode.Recalculate})
    using(var renderer=new StreamingMeshRenderer {DecodeBackend=ReceiverDecodeBackend.CPU,
      NormalMode=ReceiverNormalMode.Recalculate,TangentMode=mode})
    {
      var mesh=Fixture(); renderer.AddMesh("measurement",mesh);
      renderer.CreateVertexBuffer(); renderer.CreateVertexContainer(128,4);
      var flat=new float[mesh.vertexCount*3]; var vertices=mesh.vertices;
      for(int i=0;i<vertices.Length;i++) {flat[i*3]=vertices[i].x;flat[i*3+1]=vertices[i].y;flat[i*3+2]=vertices[i].z;}
      var frames=new[] {flat};
      var apply=(Action<float[][],float[][],float>)typeof(StreamingMeshRenderer).GetMethod("ApplyVertices",Private)
        .CreateDelegate(typeof(Action<float[][],float[][],float>),renderer);
      for(int i=0;i<8;i++) apply(frames,frames,0);
      var watch=new System.Diagnostics.Stopwatch(); long allocated=GC.GetAllocatedBytesForCurrentThread();
      watch.Start(); for(int i=0;i<256;i++) apply(frames,frames,0); watch.Stop();
      allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
      Debug.Log("CPU tangent measurement: mode="+mode+", vertices="+mesh.vertexCount+
        ", iterations=256, elapsedMs="+watch.Elapsed.TotalMilliseconds+", managedBytes="+allocated);
    }
  }

  static void VerifyRecovery()
  {
    using(var renderer=new StreamingMeshRenderer {TangentMode=ReceiverTangentMode.Recalculate,NormalMode=ReceiverNormalMode.None})
    {
      var mesh=Fixture(); var shape=mesh.vertices;
      renderer.AddMesh("fixture",mesh); renderer.CreateVertexBuffer(); renderer.CreateVertexContainer(128,4);
      Check(renderer.IsGpuResident,"GPU initialization failed");
      var pipeline=(GpuVertexPipeline)typeof(StreamingMeshRenderer).GetField("m_GpuPipeline",Private).GetValue(renderer);
      pipeline.Dispose();
      renderer.UpdateWithTime(0); // Decoder exception triggers normal keyframe recovery.
      // Submit a keyframe after failure rather than applying an orphaned delta.
      renderer.AddVertexData("0",Chunk(Keyframe(new[] {shape}),Keyframe(new[] {shape},1)),0);
      for(int i=0;i<8;i++) renderer.UpdateWithTime(0.5);
      // PumpGpuDecoder requires input to exercise the disposed pipeline.
      if(renderer.IsGpuResident) for(int i=0;i<8;i++) renderer.UpdateWithTime(0.5);
      Check(!renderer.IsGpuResident,"Execution failure did not fall back");
      renderer.AddVertexData("1",Chunk(Keyframe(new[] {shape},2),Keyframe(new[] {shape},3)),20000000);
      for(int i=0;i<8;i++) renderer.UpdateWithTime(2.5);
      Check(mesh.tangents.Length==mesh.vertexCount && mesh.normals.Length==mesh.vertexCount,"CPU recovery lost tangent requirements");
      Check(Vector3.Distance((Vector3)mesh.tangents[0],Vector3.right)<1e-5f,"Recovery produced wrong tangent");
      renderer.CreateVertexContainer(128,4,false);
      Check(!renderer.IsGpuResident,"Explicit CPU reinitialization failed");
      Check(((bool[])typeof(StreamingMeshRenderer).GetField("m_RecalculateTangents",Private).GetValue(renderer))[0],"Reconnect lost tangent settings");
      renderer.TangentMode=ReceiverTangentMode.None;
      renderer.CreateVertexContainer(128,4);
      Check(renderer.IsGpuResident && !mesh.HasVertexAttribute(VertexAttribute.Tangent),"Reconnect did not rebuild tangent-free output");
    }
  }
}
