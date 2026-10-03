// Play-mode integration: bounded GPU snapshots, dense PTS, 30Hz presentation, CPU recovery.
var dir="DevData/channels/channel_KAGURA/";
var report="Logs/iOSKagura/gpu-playback-verification.json";
var info=JsonUtility.FromJson<StreamingMesh.Core.Serialization.ChannelInfo>(System.IO.File.ReadAllText(dir+"stream.json"));
var bytes=StreamingMesh.Lib.ExternalTools.Decompress(System.IO.File.ReadAllBytes(dir+"stream.bin"));
var renderer=new StreamingMesh.Core.Rendering.StreamingMeshRenderer {FrameInterval=1f/60,CombinedFrames=info.combined_frames,NormalMode=StreamingMesh.Core.Rendering.ReceiverNormalMode.None};
int offset=info.textureSizes.Sum()+info.materialSizes.Sum();
foreach(int size in info.meshSizes) {
 System.Collections.Generic.List<string> names;
 var mesh=StreamingMesh.Core.Serialization.MeshConverter.DeserializeFromBinary(bytes,offset,size,info.container_size,out names);
 renderer.AddMesh(mesh.name,mesh);offset+=size;
}
renderer.CreateVertexBuffer();renderer.CreateVertexContainer(info.package_size,info.container_size);
if(!renderer.IsGpuResident) throw new Exception("GPU pipeline unavailable");
var chunk=StreamingMesh.Lib.ExternalTools.Decompress(System.IO.File.ReadAllBytes(dir+"000000.stmv"));
int count=BitConverter.ToInt32(chunk,0), pos=4*(count+1);
for(int i=0;i<count;i++) {Buffer.BlockCopy(BitConverter.GetBytes((long)(i*0.013*TimeSpan.TicksPerSecond)),0,chunk,pos+21,8);pos+=BitConverter.ToInt32(chunk,4*(i+1));}
renderer.AddVertexData("000000",StreamingMesh.Lib.ExternalTools.Compress(chunk),0);
var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
int cap=(int)typeof(StreamingMesh.Core.Rendering.StreamingMeshRenderer).GetField("m_MaxDecodedFrames",flags).GetValue(renderer);
int steps=0,peakPending=0;bool started=false,complete=false;
double wallStart=UnityEditor.EditorApplication.timeSinceStartup,last=wallStart;
UnityEditor.EditorApplication.CallbackFunction tick=null;
tick=()=> {
 try {
  double wall=UnityEditor.EditorApplication.timeSinceStartup;
  if(wall-wallStart>30) throw new Exception("GPU integration timeout");
  if(wall-last<1.0/30) return;
  last=wall;
  double t=steps/30.0;
  renderer.UpdateWithTime(t);
  peakPending=Math.Max(peakPending,renderer.PendingGpuFrameCount);
  if(renderer.BufferedFrameCount>cap) throw new Exception("Unbounded GPU snapshots");
  if(!started) started=renderer.CanPlayAt(0,0.25);
  else {
   if(!renderer.CanPlayAt(t,0.05)) throw new Exception("GPU buffer starvation at "+t);
   if(Math.Abs(renderer.PresentedTime-t)>0.00001) throw new Exception("PTS interpolation mismatch");
  }
  if(started) steps++;
  if(steps>60) {
   if(!renderer.IsGpuResident) throw new Exception("Unexpected CPU fallback");
   long poolBytes=renderer.GpuPoolBytes;
   var pipeline=(StreamingMesh.Core.Rendering.GpuVertexPipeline)typeof(StreamingMesh.Core.Rendering.StreamingMeshRenderer).GetField("m_GpuPipeline",flags).GetValue(renderer);
   pipeline.Dispose(); // Inject an execution failure; CPU must restart from a keyframe.
   renderer.UpdateWithTime(t+0.001); // Force a new presentation, bypassing the unchanged-pose cache.
   for(int i=0;i<12;i++) renderer.UpdateWithTime(t);
   if(renderer.IsGpuResident || !renderer.TryGetPlaybackStartTime(out double recovered)) throw new Exception("CPU keyframe recovery failed");
   for(int i=0;i<12;i++) renderer.UpdateWithTime(recovered);
   if(!renderer.CanPlayAt(recovered,0.25)) throw new Exception("Recovered keyframe cannot resume");
   System.IO.File.WriteAllText(report,"{\"passed\":true,\"steps\":"+steps+",\"peakPending\":"+peakPending+",\"capacity\":"+cap+",\"poolBytes\":"+poolBytes+"}");complete=true;
  }
 } catch(Exception ex) {System.IO.File.WriteAllText(report,"{\"passed\":false,\"error\":\""+ex.Message.Replace("\"","'")+"\"}");complete=true;}
 if(complete) {UnityEditor.EditorApplication.update-=tick;renderer.Dispose();}
};
System.IO.File.WriteAllText(report,"{\"running\":true}");UnityEditor.EditorApplication.update+=tick;
return new {started=true,report};
