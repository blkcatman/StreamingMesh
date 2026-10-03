// Integration regression using a recorded channel: unity command eval_file --file Tools/Tests/verify_irregular_frame_buffer.cs --json
var dir="DevData/channels/channel_KAGURA/";
var info=JsonUtility.FromJson<StreamingMesh.Core.Serialization.ChannelInfo>(System.IO.File.ReadAllText(dir+"stream.json"));
var bytes=StreamingMesh.Lib.ExternalTools.Decompress(System.IO.File.ReadAllBytes(dir+"stream.bin"));
var renderer=new StreamingMesh.Core.Rendering.StreamingMeshRenderer {FrameInterval=1f/60,CombinedFrames=info.combined_frames};
int offset=info.textureSizes.Sum()+info.materialSizes.Sum();
foreach(int size in info.meshSizes) {
 System.Collections.Generic.List<string> names;
 var mesh=StreamingMesh.Core.Serialization.MeshConverter.DeserializeFromBinary(bytes,offset,size,info.container_size,out names);
 renderer.AddMesh(mesh.name,mesh);offset+=size;
}
renderer.CreateVertexBuffer();
var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
typeof(StreamingMesh.Core.Rendering.StreamingMeshRenderer).GetField("m_VertexContainer",flags).SetValue(renderer,new StreamingMesh.Core.VertexContainer(info.package_size,info.container_size,false));
try {
 var chunk=StreamingMesh.Lib.ExternalTools.Decompress(System.IO.File.ReadAllBytes(dir+"000000.stmv"));
 int count=BitConverter.ToInt32(chunk,0), pos=4*(count+1);
 // Simulate a capture burst: advertised 60 fps, but actual PTS are 13 ms apart.
 for(int i=0;i<count;i++) {
  Buffer.BlockCopy(BitConverter.GetBytes((long)(i*0.013*TimeSpan.TicksPerSecond)),0,chunk,pos+21,8);
  pos+=BitConverter.ToInt32(chunk,4*(i+1));
 }
 renderer.AddVertexData("000000",StreamingMesh.Lib.ExternalTools.Compress(chunk),0);
 for(int i=0;i<40 && !renderer.CanPlayAt(0,0.25);i++) renderer.UpdateWithTime(0);
 if(!renderer.CanPlayAt(0,0.25)) throw new Exception("Deadlock: full frame buffer did not reach the required PTS horizon");
 int cap=(int)typeof(StreamingMesh.Core.Rendering.StreamingMeshRenderer).GetField("m_MaxDecodedFrames",flags).GetValue(renderer);
 if(renderer.BufferedFrameCount>cap) throw new Exception("Buffer memory limit exceeded");
 // Feed playback at 30 render updates/sec while source PTS advance faster than 60 fps.
 // This requires multiple sequential decodes per Update, not one frame per Update.
 for(int i=1;i<=60;i++) {
  double time=i/30.0;
  renderer.UpdateWithTime(time);
  if(!renderer.CanPlayAt(time,0.05)) throw new Exception("Decoder failed to refill at playback time " + time);
 }
 return new {passed=true,frames=renderer.BufferedFrameCount,capacity=cap,bufferedUntil=renderer.BufferedUntil};
} finally {renderer.Dispose();}
