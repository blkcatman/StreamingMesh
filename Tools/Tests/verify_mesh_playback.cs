// Run in Unity: unity command eval_file --file Tools/Tests/verify_mesh_playback.cs --json
var renderer = new StreamingMesh.Core.Rendering.StreamingMeshRenderer();
var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var frameType = typeof(StreamingMesh.Core.Rendering.StreamingMeshRenderer).GetNestedType("DecodedFrame", System.Reflection.BindingFlags.NonPublic);
void Check(bool condition, string message) { if (!condition) throw new System.Exception(message); }
Check(!renderer.CanPlayAt(0, 0.25), "Empty mesh buffer must block audio");
renderer.FrameInterval = 1f / 60;
int capacity = (int)typeof(StreamingMesh.Core.Rendering.StreamingMeshRenderer).GetField("m_MaxDecodedFrames", flags).GetValue(renderer);
Check((capacity - 1) * renderer.FrameInterval > StreamingMesh.Core.Rendering.StreamingMeshRenderer.ResumeBufferSeconds, "60 fps buffer must hold the resume margin without deadlocking");
renderer.FrameInterval = 0.1f;
typeof(StreamingMesh.Core.Rendering.StreamingMeshRenderer).GetMethod("EnsureDecodedStorage", flags).Invoke(renderer, null);
var frames = typeof(StreamingMesh.Core.Rendering.StreamingMeshRenderer).GetField("m_DecodedFrames", flags).GetValue(renderer);
var addFrame = frames.GetType().GetMethod("Add");
for (uint i = 0; i < 5; ++i) {
 var frame = System.Activator.CreateInstance(frameType, true);
 frameType.GetField("sequence").SetValue(frame, i);
 frameType.GetField("presentationTime").SetValue(frame, 1.0 + i * 0.1);
 addFrame.Invoke(frames, new[] {frame});
}
Check(!renderer.CanPlayAt(0.9, 0.25), "Audio before first mesh PTS must wait");
Check(renderer.CanPlayAt(1.0, 0.25), "Buffered common start must play");
Check(!renderer.CanPlayAt(1.38, 0.05), "Near end of buffer audio must pause");
Check(!renderer.CanPlayAt(1.2, 0.25), "Resuming requires extra buffer");
renderer.UpdateWithTime(1.15);
Check(System.Math.Abs(renderer.PresentedTime - 1.15) < 0.00001, "Mesh must interpolate at audio PTS");
renderer.UpdateWithTime(1.8);
Check(renderer.PlaybackState == StreamingMesh.Core.Rendering.StreamingPlaybackState.Holding, "Underrun must hold");
Check(System.Math.Abs(renderer.PresentedTime - 1.4) < 0.00001, "Underrun must keep latest available mesh");
renderer.Dispose();
return "PASS: empty/start/resume/underrun/interpolation";
