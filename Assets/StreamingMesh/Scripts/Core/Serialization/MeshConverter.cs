using System;
using System.Collections.Generic;
using UnityEngine;

namespace StreamingMesh.Core.Serialization
{
  public class MeshConverter
  {
    public static Mesh DeserializeFromBinary(byte[] data, int offsetBytes, int dataSize, int containerSize, out List<string> refMaterials)
    {
      byte[] buffer = new byte[dataSize];
      Buffer.BlockCopy(data, offsetBytes, buffer, 0, dataSize);
      MeshInfo meshInfo = InfoConverter.Deserialize<MeshInfo>(buffer);

      Mesh mesh = new Mesh();
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
      refMaterials = meshInfo.materialNames;

      mesh.uv = meshInfo.uv;
      mesh.uv2 = meshInfo.uv2;
      mesh.uv3 = meshInfo.uv3;
      mesh.uv4 = meshInfo.uv4;

      return mesh;
    }

  }
}