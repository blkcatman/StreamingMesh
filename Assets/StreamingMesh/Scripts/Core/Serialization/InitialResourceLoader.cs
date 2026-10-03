using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEngine;

namespace StreamingMesh.Core.Serialization
{
  public sealed class InitialResourceLoader : IDisposable
  {
    readonly ChannelInfo info;
    readonly Func<MaterialInfo, Shader> resolveShader;
    readonly Action<string, Texture2D> addTexture;
    readonly Dictionary<string, MaterialPropertyInfo> required = new Dictionary<string, MaterialPropertyInfo>();
    readonly HashSet<string> textureIds;
    byte[] metadata;
    Texture2D pendingTexture;
    public readonly MaterialInfo[] Materials;
    public readonly MeshInfo[] Meshes;

    public InitialResourceLoader(ChannelInfo info, Func<MaterialInfo, Shader> resolveShader, Action<string, Texture2D> addTexture)
    {
      this.info = info; this.resolveShader = resolveShader; this.addTexture = addTexture;
      textureIds = new HashSet<string>(info.textures, StringComparer.Ordinal);
      Materials = new MaterialInfo[info.materials.Count]; Meshes = new MeshInfo[info.meshes.Count];
    }

    public bool NeedsPart(InitialDataPart part)
    {
      foreach (var segment in part.records)
        if (segment.kind != InitialDataParts.Texture || required.ContainsKey(info.textures[segment.index])) return true;
      return false;
    }

    public void Consume(InitialDataSegment segment, int offset, byte[] scratch, int count)
    {
      if (segment.kind != InitialDataParts.Texture)
      {
        int size = (segment.kind == InitialDataParts.Material ? info.materialSizes : info.meshSizes)[segment.index];
        if (offset == 0) metadata = new byte[size];
        if (metadata == null || offset > metadata.Length - count) throw new InvalidDataException("Incomplete metadata record.");
        Buffer.BlockCopy(scratch, 0, metadata, offset, count);
        if (offset + count == size)
        {
          if (segment.kind == InitialDataParts.Material) ReadMaterial(segment.index);
          else Meshes[segment.index] = InfoConverter.Deserialize<MeshInfo>(metadata);
          metadata = null;
        }
        return;
      }

      string id = info.textures[segment.index];
      if (!required.TryGetValue(id, out var settings)) return;
      var payload = info.texturePayloads[segment.index];
      int bytes = info.textureSizes[segment.index];
      if (offset == 0)
      {
        if (pendingTexture != null) throw new InvalidDataException("Unfinished texture upload.");
        if (settings.hasTextureSettings && (settings.textureLinear != payload.linear || settings.textureMipChain != (payload.mipCount > 1)))
          throw new InvalidDataException("Texture payload disagrees with material settings: " + info.textureNames[segment.index]);
        pendingTexture = payload.Create(); pendingTexture.name = info.textureNames[segment.index];
      }
      if (pendingTexture == null) throw new InvalidDataException("Missing texture prefix.");
      // Native view belongs to Texture; it is never kept across Apply.
      var target = pendingTexture.GetRawTextureData<byte>();
      NativeArray<byte>.Copy(scratch, 0, target, offset, count);
      if (offset + count == bytes)
      {
        pendingTexture.Apply(false, true);
        addTexture(id, pendingTexture);
        Debug.Log($"StreamingMesh GPU texture ready: {pendingTexture.name}, {payload.format}, {payload.width}x{payload.height}, mips={payload.mipCount}, readable={pendingTexture.isReadable}");
        pendingTexture = null;
      }
    }

    void ReadMaterial(int index)
    {
      var material = InfoConverter.Deserialize<MaterialInfo>(metadata);
      if (material == null || material.properties == null || material.version != MaterialConverter.CurrentVersion || material.id != info.materials[index])
        throw new InvalidDataException("Material record does not match its ID table.");
      Materials[index] = material;
      Shader shader = resolveShader(material);
      foreach (var property in material.properties)
      {
        if (property == null || string.IsNullOrEmpty(property.name)) throw new InvalidDataException("Invalid material property.");
        if (property.type != 4 || string.IsNullOrEmpty(property.textureId)) continue;
        if (!textureIds.Contains(property.textureId)) throw new InvalidDataException("Material references a missing texture ID.");
        int propertyIndex = shader.FindPropertyIndex(property.name);
        if (propertyIndex < 0 || shader.GetPropertyType(propertyIndex) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
        if (required.TryGetValue(property.textureId, out var previous) &&
            (previous.textureLinear != property.textureLinear || previous.textureMipChain != property.textureMipChain ||
             previous.textureFilterMode != property.textureFilterMode || previous.textureWrapU != property.textureWrapU ||
             previous.textureWrapV != property.textureWrapV || previous.textureAnisoLevel != property.textureAnisoLevel ||
             previous.textureMipMapBias != property.textureMipMapBias))
          throw new InvalidDataException("Conflicting settings for shared texture: " + property.value);
        required[property.textureId] = property;
      }
    }

    public void Dispose()
    {
      metadata = null;
      if (pendingTexture != null)
      {
        if (Application.isPlaying) UnityEngine.Object.Destroy(pendingTexture); else UnityEngine.Object.DestroyImmediate(pendingTexture);
        pendingTexture = null;
      }
    }
  }
}
