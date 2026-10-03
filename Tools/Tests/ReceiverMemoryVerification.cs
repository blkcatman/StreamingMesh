#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using StreamingMesh.Core;
using StreamingMesh.Core.Rendering;
using StreamingMesh.Lib;

public static class ReceiverMemoryVerification
{
  static int checks;
  static void Check(bool value, string text) { checks++; if (!value) throw new Exception(text); }
  static byte[] Frame(uint sequence, bool key)
  {
    var data = new byte[key ? 50 : 38]; data[0] = (byte)(key ? 15 : 14); data[8] = 2;
    Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, data, 1, 4);
    Buffer.BlockCopy(BitConverter.GetBytes(1.5f), 0, data, 9, 4);
    Buffer.BlockCopy(BitConverter.GetBytes((long)sequence * 1000000), 0, data, 21, 8);
    if (key)
    {
      data[5] = 1; data[29] = data[30] = data[31] = 64; data[32] = 3;
      for (int i = 0; i < 3; i++) { data[35 + i * 5] = (byte)i; data[38 + i * 5] = (byte)(i == 2 ? 32 : i); }
    }
    else for (int i = 29; i < data.Length; i += 3) { data[i] = 129; data[i + 1] = 127; data[i + 2] = 128; }
    return data;
  }
  static byte[] Chunk(params byte[][] frames)
  {
    using (var raw = new MemoryStream())
    using (var writer = new BinaryWriter(raw))
    {
      writer.Write(frames.Length); foreach (var frame in frames) writer.Write(frame.Length);
      foreach (var frame in frames) writer.Write(frame);
      return ExternalTools.Compress(raw.ToArray());
    }
  }
  static Mesh Mesh() { return new Mesh {vertices = new[] {Vector3.zero, Vector3.right, Vector3.up}, uv = new[] {Vector2.zero,Vector2.right,Vector2.up}, triangles = new[] {0,1,2}}; }
  static StreamingMeshRenderer Renderer(bool tangents = false)
  {
    var renderer = new StreamingMeshRenderer {DecodeBackend=ReceiverDecodeBackend.CPU,
      NormalMode=tangents ? ReceiverNormalMode.Recalculate : ReceiverNormalMode.None,
      TangentMode=tangents ? ReceiverTangentMode.Recalculate : ReceiverTangentMode.None, CombinedFrames=2, FrameInterval=.1f};
    renderer.AddMesh("fixture", Mesh()); renderer.CreateVertexBuffer(); renderer.CreateVertexContainer(128, 4, false);
    return renderer;
  }
  public static async void Run()
  {
    int exit = 1;
    try
    {
      var key = Frame(0, true); var delta = Frame(1, false);
      // Prefix/suffix guards ensure slice decoding never reads another frame.
      var storage = new byte[key.Length + 53]; Buffer.BlockCopy(key,0,storage,17,key.Length);
      var keySlice = new ArraySegment<byte>(storage,17,key.Length);
      var deltaSlice = new ArraySegment<byte>(delta);
      var output = new[] {new float[9]};
      using (var decoder = new VertexContainer(128,4,false))
      {
        Check(decoder.DecodeInto(keySlice, output, out var root, out var error), error);
        Check(root.x == 1.5f, "Slice root offset incorrect");
        Check(decoder.DecodeInto(deltaSlice, output, out root, out error), error);
        Check(Math.Abs(output[0][0] - 1f/16384) < 1e-8, "Integer delta math changed");
        Check(Math.Abs(output[0][1] + 1f/16384) < 1e-8, "Negative delta math changed");
        for (int i=0;i<100;i++) decoder.DecodeInto(keySlice,output,out root,out error);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i=0;i<10000;i++) decoder.DecodeInto((i%10)==0 ? keySlice : deltaSlice,output,out root,out error);
        long allocated = GC.GetAllocatedBytesForCurrentThread()-before;
        Check(allocated == 0, "CPU vertex restore allocates " + allocated + " bytes");
        Debug.Log("Receiver CPU restore: 10000 frames, managed allocations=" + allocated + " bytes");
      }

      var gpuMesh = Mesh();
      try
      {
        using (var gpu = new GpuVertexPipeline(new[] {gpuMesh},128,4,4,new[] {true},new[] {true}))
        using (var vertexBuffer = gpuMesh.GetVertexBuffer(0))
        {
          Check(gpu.TrySubmit(keySlice,10,0,out var a,out var error),error);
          Check(gpu.TrySubmit(deltaSlice,11,.1,out var b,out error),error);
          gpu.Present(a,b,.5f);
          var values = new float[gpuMesh.vertexCount * gpuMesh.GetVertexBufferStride(0) / 4];
          vertexBuffer.GetData(values);
          Check(Math.Abs(values[0] - .5f/16384) < 1e-8,"GPU slice offset/delta changed");
          for(int i=0;i<20;i++) gpu.Present(a,b,.5f);
          long before = GC.GetAllocatedBytesForCurrentThread();
          for(int i=0;i<1000;i++) gpu.Present(a,b,.5f);
          long allocated = GC.GetAllocatedBytesForCurrentThread()-before;
          Check(allocated==0,"GPU presentation allocates " + allocated + " bytes");
          Debug.Log("Receiver GPU presentation: 1000 frames, managed allocations=" + allocated + " bytes");
          gpu.Release(a); gpu.Release(b); vertexBuffer.GetData(values);
          // Batch Editor does not advance the graphics-fence frame counter.
          // Measure both restored packet types in the remaining two fixed slots;
          // browser replay below exercises continuous slot retirement/reuse.
          before = GC.GetAllocatedBytesForCurrentThread();
          bool submittedKey = gpu.TrySubmit(keySlice,20,0,out a,out error);
          bool submittedDelta = gpu.TrySubmit(deltaSlice,21,.1,out b,out error);
          allocated = GC.GetAllocatedBytesForCurrentThread()-before;
          Check(submittedKey && submittedDelta,"GPU restore admission failed");
          Check(allocated==0,"GPU restore allocates " + allocated + " bytes");
          Debug.Log("Receiver GPU restore: keyframe/delta, managed allocations=" + allocated + " bytes");
        }
      }
      finally { UnityEngine.Object.DestroyImmediate(gpuMesh); }

      using (var renderer = Renderer(true))
      {
        renderer.CombinedFrames = 300;
        var frames = new byte[300][];
        for (int i=0;i<frames.Length;i++) frames[i]=Frame((uint)i,i%10==0);
        renderer.AddVertexData("0",Chunk(frames),0);
        for(int i=0;i<20;i++) renderer.UpdateWithTime(i*.1);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=20;i<250;i++) renderer.UpdateWithTime(i*.1);
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(allocated==0,"Receiver CPU decode/interpolation allocates " + allocated + " bytes");
        Check(renderer.PresentedTime>24,"CPU update did not present advancing frames");
        Debug.Log("Receiver CPU update with normals/tangents: 230 updates, managed allocations=" + allocated + " bytes");
      }

      using (var renderer = Renderer())
      {
        // Out-of-order arrival, duplicate chunks, ring wrap and keyframe recovery.
        renderer.AddVertexData("1", Chunk(Frame(2,true),Frame(3,false)), 0);
        renderer.AddVertexData("0", Chunk(key,delta), 0);
        renderer.AddVertexData("0", Chunk(key,delta), 0);
        Check(renderer.EncodedFrameCount == 4,"Duplicate or reordered batch lost frames");
        for(int i=0;i<8;i++) renderer.UpdateWithTime(0);
        Check(renderer.CanPlayAt(0,.25),"Ordered decode/prebuffer failed");
        for(int n=2;n<120;n++)
        {
          renderer.AddVertexData(n.ToString(),Chunk(Frame((uint)n*2,true),Frame((uint)n*2+1,false)),0);
          for(int i=0;i<4;i++) renderer.UpdateWithTime(n*.2);
          Check(renderer.EncodedFrameCount <= 16,"Encoded backpressure exceeded");
          Check(renderer.BufferedFrameCount <= 15,"Decoded pool grew unbounded");
        }
        long pooled = renderer.EncodedPoolBytes;
        Check(pooled <= 4 * 65536,"Chunk pool grew while wrapping");
        Debug.Log("Receiver chunk reuse: 120 chunks, pooled bytes=" + pooled);
      }
      using (var renderer = Renderer())
      {
        var good = Chunk(key,delta); var raw = ExternalTools.Decompress(good);
        Buffer.BlockCopy(BitConverter.GetBytes(-1),0,raw,8,4);
        Check(!await renderer.AddVertexDataAsync("0",ExternalTools.Compress(raw),0),"Malformed final frame accepted");
        Check(renderer.EncodedFrameCount==0,"Malformed batch partially committed");
        Check(await renderer.AddVertexDataAsync("0",good,0),"Rejected chunk retry failed");
      }
      var disposed = Renderer(); var pending = disposed.AddVertexDataAsync("0",Chunk(key,delta),0); disposed.Dispose();
      Check(!await pending && disposed.EncodedFrameCount==0,"Worker resurrected disposed buffers");

      var full = Renderer(); var held = new byte[19][];
      for(int n=0;n<held.Length;n++) held[n]=Chunk(Frame((uint)n*2,true),Frame((uint)n*2+1,false));
      for(int n=0;n<8;n++) Check(await full.AddVertexDataAsync(n.ToString(),held[n],0),"Early backpressure");
      Check(!full.CanAcceptChunk && !await full.AddVertexDataAsync("8",held[8],0),"Full ring accepted another batch");
      full.Dispose();
      Debug.Log("PASS receiver memory: " + checks + " checks"); exit=0;
    }
    catch (Exception exception) { Debug.LogException(exception); }
    finally { EditorApplication.Exit(exit); }
  }
}
#endif
