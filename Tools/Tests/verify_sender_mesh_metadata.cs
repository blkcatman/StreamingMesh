// Run with the KAGURA sender scene playing: unity command eval_file --file Tools/Tests/verify_sender_mesh_metadata.cs --json
if (!Application.isPlaying) throw new System.Exception("Run this check in Play mode to reproduce non-readable imported meshes.");
var sender = UnityEngine.Object.FindFirstObjectByType<StreamingMesh.STMHttpSender>();
var serializer = sender.GetComponent<StreamingMesh.STMHttpSerializer>();
var reports = new System.Collections.Generic.List<object>();
foreach (var renderer in sender.targetGameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
 var info = serializer.CreateMeshInfo(renderer);
 if (info.indices.Count == 0 || info.uv.Length != info.vertexCount)
  throw new System.Exception("Missing triangles or UVs: " + renderer.name);
 if (info.indicesCounts.Sum() != info.indices.Count || info.indices.Any(i => i < 0 || i >= info.vertexCount))
  throw new System.Exception("Invalid triangle indices: " + renderer.name);
 var data = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(info));
 System.Collections.Generic.List<string> materials;
 var mesh = StreamingMesh.Core.Serialization.MeshConverter.DeserializeFromBinary(data, 0, data.Length, 4, out materials);
 if (mesh.triangles.Length != info.indices.Count || mesh.uv.Length != info.vertexCount)
  throw new System.Exception("Receiver topology round trip failed: " + renderer.name);
 UnityEngine.Object.Destroy(mesh);
 reports.Add(new { name=renderer.name, readable=renderer.sharedMesh.isReadable, vertices=info.vertexCount, triangles=info.indices.Count/3, uv=info.uv.Length });
}
return reports;
