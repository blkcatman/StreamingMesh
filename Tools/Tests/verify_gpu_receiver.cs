// Run in Play mode with UnityPipeline eval_file. Test-only readback; writes completion asynchronously.
var dir="DevData/channels/channel_KAGURA/";
var report="Logs/iOSKagura/gpu-receiver-verification.json";
var info=JsonUtility.FromJson<StreamingMesh.Core.Serialization.ChannelInfo>(System.IO.File.ReadAllText(dir+"stream.json"));
var metadata=StreamingMesh.Lib.ExternalTools.Decompress(System.IO.File.ReadAllBytes(dir+"stream.bin"));
var meshes=new System.Collections.Generic.List<Mesh>();
int offset=info.textureSizes.Sum()+info.materialSizes.Sum();
foreach(int size in info.meshSizes) {
 System.Collections.Generic.List<string> names;
 var mesh=StreamingMesh.Core.Serialization.MeshConverter.DeserializeFromBinary(metadata,offset,size,info.container_size,out names);
 meshes.Add(mesh);offset+=size;
}
var uv=meshes.Select(m=>m.uv).ToArray();
var triangles=meshes.Select(m=>m.triangles).ToArray();
var layout=meshes.Select(m=>new float[m.vertexCount*3]).ToArray();
var gpu=new StreamingMesh.Core.Rendering.GpuVertexPipeline(meshes,info.package_size,info.container_size,12,true);
var cpu=new StreamingMesh.Core.VertexContainer(info.package_size,info.container_size,false);
var chunk=StreamingMesh.Lib.ExternalTools.Decompress(System.IO.File.ReadAllBytes(dir+"000000.stmv"));
int count=BitConverter.ToInt32(chunk,0), pos=4*(count+1);
var encoded=new System.Collections.Generic.List<byte[]>();
for(int i=0;i<count;i++) {int n=BitConverter.ToInt32(chunk,4*(i+1));var b=new byte[n];Buffer.BlockCopy(chunk,pos,b,0,n);pos+=n;encoded.Add(b);}
var frames=new System.Collections.Generic.Queue<StreamingMesh.Core.Rendering.GpuVertexPipeline.Frame>();
var expected=new System.Collections.Generic.Queue<float[][]>();
int submitted=0,verified=0,peakPending=0;
float maxError=0,maxNormalError=0;
var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
var snapshotField=typeof(StreamingMesh.Core.Rendering.GpuVertexPipeline.Frame).GetField("vertices",flags);
var readback=new Vector4[meshes.Sum(m=>m.vertexCount)];
var start=UnityEditor.EditorApplication.timeSinceStartup;
bool cleanup=false;
UnityEditor.EditorApplication.CallbackFunction tick=null;
tick=()=> {
 try {
  UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
  UnityEditor.SceneView.RepaintAll();
  if(UnityEditor.EditorApplication.timeSinceStartup-start>90) throw new Exception("GPU verification timeout");
  while(frames.Count>0 && frames.Peek().IsReady) {
   var f=frames.Dequeue();var e=expected.Dequeue();
   ((ComputeBuffer)snapshotField.GetValue(f)).GetData(readback);
   int global=0;
   for(int m=0;m<e.Length;m++) for(int v=0;v<e[m].Length/3;v++,global++) {
    var p=new Vector3(e[m][3*v],e[m][3*v+1],e[m][3*v+2]);
    maxError=Mathf.Max(maxError,Mathf.Abs(p.x-readback[global].x),Mathf.Abs(p.y-readback[global].y),Mathf.Abs(p.z-readback[global].z));
    if(!f.Bounds.Contains(p)) throw new Exception("Bounds excludes restored vertex");
   }
   if(maxError>1e-5f) throw new Exception("CPU/GPU mismatch: "+maxError);
   if(verified==0 || verified==29 || verified==30 || verified==count-1) {
    var other = frames.Count>0 && frames.Peek().IsReady ? frames.Peek() : f;
    float blend = other==f ? 0 : 0.37f;
    if(blend>0) {
     var second=expected.Peek();
     e=e.Select((a,m)=>a.Select((v,i)=>Mathf.LerpUnclamped(v,second[m][i],blend)).ToArray()).ToArray();
     global=0;
     for(int m=0;m<e.Length;m++) for(int v=0;v<e[m].Length/3;v++,global++)
      readback[global]=new Vector4(e[m][v*3],e[m][v*3+1],e[m][v*3+2],1);
    }
    gpu.Present(f,other,blend);
    global=0;
    for(int m=0;m<meshes.Count;m++) {
     var mesh=meshes[m];int stride=mesh.GetVertexBufferStride(0)/4;
     var actual=new float[mesh.vertexCount*stride];
     using(var b=mesh.GetVertexBuffer(0)) b.GetData(actual);
     var normal=new Vector3[mesh.vertexCount];
     var index=triangles[m];
     for(int t=0;t<index.Length;t+=3) {
      int a=index[t],b=index[t+1],c=index[t+2];
      var pa=new Vector3(actual[a*stride],actual[a*stride+1],actual[a*stride+2]);
      var pb=new Vector3(actual[b*stride],actual[b*stride+1],actual[b*stride+2]);
      var pc=new Vector3(actual[c*stride],actual[c*stride+1],actual[c*stride+2]);
      var edgeA=pb-pa;var edgeB=pc-pa;var n=Vector3.Cross(edgeA,edgeB);if(n.sqrMagnitude<=Mathf.Max(1e-30f,1e-12f*edgeA.sqrMagnitude*edgeB.sqrMagnitude)) n=Vector3.zero;normal[a]+=n;normal[b]+=n;normal[c]+=n;
     }
     int uvOffset=mesh.GetVertexAttributeOffset(UnityEngine.Rendering.VertexAttribute.TexCoord0)/4;
     int nOffset=mesh.GetVertexAttributeOffset(UnityEngine.Rendering.VertexAttribute.Normal)/4;
     for(int v=0;v<mesh.vertexCount;v++,global++) {
      var p=new Vector3(actual[v*stride],actual[v*stride+1],actual[v*stride+2]);
      if(Vector3.Distance(p,(Vector3)readback[global])>1e-5f) throw new Exception("Raw GPU output mismatch");
      if(Mathf.Abs(actual[v*stride+uvOffset]-uv[m][v].x)>1e-6 || Mathf.Abs(actual[v*stride+uvOffset+1]-uv[m][v].y)>1e-6) throw new Exception("UV changed");
      var n=new Vector3(actual[v*stride+nOffset],actual[v*stride+nOffset+1],actual[v*stride+nOffset+2]);
      var reference=normal[v].sqrMagnitude>1e-20f?normal[v]/Mathf.Sqrt(normal[v].sqrMagnitude):Vector3.zero;
      maxNormalError=Mathf.Max(maxNormalError,Vector3.Distance(n,reference));
      if(Vector3.Distance(n,reference)>0.003f) throw new Exception("GPU normal mismatch: "+maxNormalError+" mesh="+m+" vertex="+v+" area2="+normal[v].sqrMagnitude+" actual="+n+" expected="+reference);
     }
    }
   }
   gpu.Release(f);verified++;
  }
  for(int batch=0;batch<8 && submitted<count;batch++) {
   var b=encoded[submitted];
   StreamingMesh.Core.Rendering.GpuVertexPipeline.Frame frame;string error;
   if(!gpu.TrySubmit(b,(uint)submitted,submitted/60.0,out frame,out error)) {
    if(error!=null) throw new Exception(error+" frame="+submitted);
    break;
   }
   StreamingMesh.Core.VertexDecodeResult result=null;
   cpu.DecodeAsync(b,layout,r=>result=r);
   if(result==null || !result.succeeded) throw new Exception("CPU reference decode failed");
   frames.Enqueue(frame);expected.Enqueue(result.vertices);submitted++;
   peakPending=Math.Max(peakPending,frames.Count);
  }
  System.IO.File.WriteAllText(report,"{\"running\":true,\"submitted\":"+submitted+",\"verified\":"+verified+"}");
  if(verified==count) {
   // A corrupt frame must invalidate the delta base; a full keyframe restores it.
   var bad=(byte[])encoded[0].Clone();bad[5]=255;bad[6]=255;bad[7]=255;
   StreamingMesh.Core.Rendering.GpuVertexPipeline.Frame f;string error;
   if(gpu.TrySubmit(bad,999,0,out f,out error) || error==null) throw new Exception("Bad keyframe accepted");
   if(gpu.TrySubmit(encoded[1],1000,0,out f,out error) || error==null) throw new Exception("Delta accepted after invalid keyframe");
   if(!gpu.TrySubmit(encoded[0],1001,0,out f,out error)) throw new Exception("Keyframe recovery failed: "+error);
   var json="{\"passed\":true,\"frames\":"+verified+",\"maxVertexError\":"+maxError.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+",\"maxNormalError\":"+maxNormalError.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+",\"peakPending\":"+peakPending+",\"poolBytes\":"+gpu.PoolBytes+"}";
   System.IO.File.WriteAllText(report,json);cleanup=true;
  }
 } catch(Exception ex) {System.IO.File.WriteAllText(report,"{\"passed\":false,\"error\":\""+ex.ToString().Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n","\\n").Replace("\r","")+"\"}");cleanup=true;}
 if(cleanup) {UnityEditor.EditorApplication.update-=tick;gpu.Dispose();cpu.Dispose();foreach(var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);}
};
System.IO.File.WriteAllText(report,"{\"running\":true}");
UnityEditor.EditorApplication.update+=tick;
return new {started=true,frames=count,report};
