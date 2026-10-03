#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace StreamingMesh.Core.Serialization
{
  public static class InitialResourceExporter
  {
    public static void Export(ChannelInfo info, IList<MaterialInfo> materials, IList<MeshInfo> meshes,
      IList<Texture> textures, GpuTextureFormat format, Action<InitialDataPart, byte[]> publish)
    {
      if (info.materials.Count != materials.Count || info.meshes.Count != meshes.Count || info.textures.Count != textures.Count)
        throw new ArgumentException("Initial resource tables disagree.");
      info.materialSizes = new List<int>(); info.meshSizes = new List<int>(); info.textureSizes = new List<int>();
      info.texturePayloads = new List<TexturePayloadInfo>();
      var writer = new InitialDataPartWriter(publish);
      for (int i = 0; i < materials.Count; i++)
      {
        byte[] bytes = InfoConverter.Serialize(materials[i]); info.materialSizes.Add(bytes.Length);
        writer.Write(InitialDataParts.Material, i, bytes);
      }
      for (int i = 0; i < meshes.Count; i++)
      {
        byte[] bytes = InfoConverter.Serialize(meshes[i]); info.meshSizes.Add(bytes.Length);
        writer.Write(InitialDataParts.Mesh, i, bytes);
      }
      // Metadata finishes before texture files, so unused maps can be skipped.
      writer.Flush();
      for (int i = 0; i < textures.Count; i++)
      {
        Texture2D encoded = null;
        try
        {
          encoded = TextureConverter.ExportGpu(textures[i], format, out var payload);
          info.texturePayloads.Add(payload); info.textureSizes.Add(payload.ByteCount());
          using (var input = new NativeBufferStream(encoded.GetRawTextureData<byte>().AsReadOnly()))
            writer.Write(InitialDataParts.Texture, i, input, payload.ByteCount());
          Debug.Log($"StreamingMesh exported texture: {textures[i].name}, {payload.format}, {payload.width}x{payload.height}, mips={payload.mipCount}, bytes={payload.ByteCount()}");
        }
        finally { if (encoded != null) UnityEngine.Object.DestroyImmediate(encoded); }
      }
      writer.Flush(); info.initial_data = writer.Parts;
      InitialDataParts.Validate(info.initial_data, info.materialSizes, info.meshSizes, info.textureSizes);
    }
  }
}
#endif
