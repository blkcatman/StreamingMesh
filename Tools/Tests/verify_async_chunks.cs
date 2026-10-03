var report="Logs/iOSKagura/async-chunk-verification.json";
async System.Threading.Tasks.Task Verify() {
 var renderer=new StreamingMesh.Core.Rendering.StreamingMeshRenderer {CombinedFrames=300};
 var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
 int FrameCount(StreamingMesh.Core.Rendering.StreamingMeshRenderer r) {
  return r.EncodedFrameCount;
 }
 try {
  var bytes=System.IO.File.ReadAllBytes("DevData/channels/channel_KAGURA/000000.stmv");
  var raw=StreamingMesh.Lib.ExternalTools.Decompress(bytes);
  int count=BitConverter.ToInt32(raw,0);
  Buffer.BlockCopy(BitConverter.GetBytes(-1),0,raw,count*4,4);
  if(await renderer.AddVertexDataAsync("000000",StreamingMesh.Lib.ExternalTools.Compress(raw),0)) throw new Exception("Malformed chunk accepted");
  if(FrameCount(renderer)!=0) throw new Exception("Partial frame batch committed");
  if(!await renderer.AddVertexDataAsync("000000",bytes,0) || FrameCount(renderer)!=count) throw new Exception("Retry of rejected chunk failed");
  var disposed=new StreamingMesh.Core.Rendering.StreamingMeshRenderer {CombinedFrames=300};
  var inFlight=disposed.AddVertexDataAsync("000000",bytes,0);
  disposed.Dispose();
  if(await inFlight || FrameCount(disposed)!=0) throw new Exception("Late import resurrected disposed receiver");
  System.IO.File.WriteAllText(report,"{\"passed\":true,\"frames\":"+count+"}");
 } catch(Exception e) {System.IO.File.WriteAllText(report,"{\"passed\":false,\"error\":\""+e.Message.Replace("\"","'")+"\"}");}
 finally {renderer.Dispose();}
}
System.IO.File.WriteAllText(report,"{\"running\":true}");
_=Verify();return new {started=true,report};
