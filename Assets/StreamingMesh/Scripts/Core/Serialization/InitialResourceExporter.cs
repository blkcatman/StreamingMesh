#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace StreamingMesh.Core.Serialization
{
  public static class InitialResourceExporter
  {
    public static void ExportVariants(ChannelInfo info, IList<MaterialInfo> materials, IList<MeshInfo> meshes,
      IList<Texture> textures, IList<GpuTextureFormat> formats, Action<InitialDataPart, byte[]> publish,
      int partBytes = InitialDataParts.DefaultPartBytes)
    {
      if (formats == null || formats.Count == 0 || formats.Count > 7) throw new ArgumentException("Select GPU texture formats.");
      var seen = new HashSet<GpuTextureFormat>();
      foreach (var format in formats) if (!Enum.IsDefined(typeof(GpuTextureFormat), format) || !seen.Add(format))
        throw new ArgumentException("Invalid/duplicate GPU texture format.");
      var variants = new List<TextureVariantInfo>();
      info.textureVariants = null;
      foreach (var format in formats)
      {
        Export(info, materials, meshes, textures, format, publish, partBytes);
        variants.Add(new TextureVariantInfo { format = format.ToString(), textureSizes = info.textureSizes,
          texturePayloads = info.texturePayloads, initialData = info.initialData });
      }
      info.textureSizes = variants[0].textureSizes; info.texturePayloads = variants[0].texturePayloads;
      info.initialData = variants[0].initialData; info.textureVariants = variants;
    }

    public static void Export(ChannelInfo info, IList<MaterialInfo> materials, IList<MeshInfo> meshes,
      IList<Texture> textures, GpuTextureFormat format, Action<InitialDataPart, byte[]> publish,
      int partBytes = InitialDataParts.DefaultPartBytes)
    {
      if (info.materials.Count != materials.Count || info.meshes.Count != meshes.Count || info.textures.Count != textures.Count)
        throw new ArgumentException("Initial resource tables disagree.");
      info.textureVariants = null;
      info.materialSizes = new List<int>(); info.meshSizes = new List<int>(); info.textureSizes = new List<int>();
      info.texturePayloads = new List<TexturePayloadInfo>();
      using (var writer = new InitialDataPartWriter(publish, partBytes))
      {
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
        // Whole resources share files. The target is checked only after each
        // resource; no texture is sliced to fill a physical file exactly.
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
        writer.Flush(); info.initialData = writer.Parts;
        InitialDataParts.Validate(info.initialData, info.materialSizes, info.meshSizes, info.textureSizes);
      }
    }
  }
}
#endif
