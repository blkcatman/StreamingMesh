using System;
using System.Collections.Generic;
using UnityEngine;

namespace StreamingMesh.Core.Serialization
{
  public class MeshConverter
  {
    public static Mesh DeserializeFromBinary(byte[] data, int offsetBytes, int dataSize, int containerSize, out List<string> refMaterials, IDictionary<string, Material> materials = null)
    {
      MeshInfo meshInfo = InfoConverter.Deserialize<MeshInfo>(data, offsetBytes, dataSize);

      refMaterials = meshInfo.materialIds;
      if (refMaterials == null) throw new System.IO.InvalidDataException("Mesh material IDs are required.");
      foreach (var id in refMaterials)
        if (!string.IsNullOrEmpty(id) && (!ResourceIdentity.IsValid(id) || (materials != null && !materials.ContainsKey(id))))
          throw new System.IO.InvalidDataException("Invalid/missing mesh material ID.");

      Mesh mesh = new Mesh();
      try
      {
        mesh.name = meshInfo.name + "_stm";

        Vector3[] verts = new Vector3[meshInfo.vertexCount];
        mesh.SetVertices(new List<Vector3>(verts));
        mesh.bounds = new Bounds (
          Vector3.zero, new Vector3(containerSize / 2.0f, containerSize, containerSize / 2.0f)
        );
        List<int> multiIndices = meshInfo.indices;
        int offset = 0;

        mesh.subMeshCount = meshInfo.subMeshCount;
        for(int i = 0; i < meshInfo.subMeshCount; i++)
        {
          int indicesCnt = meshInfo.indicesCounts[i];
          List<int> indices = multiIndices.GetRange(offset, indicesCnt);
          offset += indicesCnt;
          mesh.SetIndices(indices.ToArray(), MeshTopology.Triangles, i);
        }

        mesh.uv = meshInfo.uv;
        mesh.uv2 = meshInfo.uv2;
        mesh.uv3 = meshInfo.uv3;
        mesh.uv4 = meshInfo.uv4;

        return mesh;
      }
      catch
      {
        if (Application.isPlaying) UnityEngine.Object.Destroy(mesh); else UnityEngine.Object.DestroyImmediate(mesh);
        throw;
      }
    }

  }
}
