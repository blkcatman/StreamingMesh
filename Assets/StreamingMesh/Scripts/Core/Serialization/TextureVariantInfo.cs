using System;
using System.Collections.Generic;
using System.IO;

namespace StreamingMesh.Core.Serialization
{
  [Serializable]
  public sealed class TextureVariantInfo
  {
    public string format;
    public List<int> textureSizes;
    public List<TexturePayloadInfo> texturePayloads;
    public List<InitialDataPart> initial_data;
  }

  public static class TextureVariants
  {
    // Order is Sender preference. Inspect capabilities before allocating any
    // Texture or downloading payload files from unselected variants.
    public static string Select(ChannelInfo info, string preferred = null,
      Func<TexturePayloadInfo, bool> supported = null)
    {
      var variants = info.texture_variants;
      if (variants == null || variants.Count == 0)
      {
        if (!string.IsNullOrEmpty(preferred) && info.texturePayloads != null)
          foreach (var payload in info.texturePayloads)
            if (payload != null && payload.format != preferred) throw new NotSupportedException("Requested texture format is unavailable: " + preferred);
        return null;
      }
      if (variants.Count > 7) throw new InvalidDataException("Too many texture variants.");
      var formats = new HashSet<string>(StringComparer.Ordinal);
      TextureVariantInfo selected = null;
      foreach (var variant in variants)
      {
        if (variant == null || !formats.Add(variant.format) || variant.texturePayloads == null ||
            variant.textureSizes == null || variant.texturePayloads.Count != info.textures.Count ||
            variant.textureSizes.Count != info.textures.Count)
          throw new InvalidDataException("Invalid texture variant tables.");
        new TexturePayloadInfo { format = variant.format }.UnityFormat();
        bool usable = string.IsNullOrEmpty(preferred) || preferred == variant.format;
        for (int i = 0; i < variant.texturePayloads.Count; i++)
        {
          var payload = variant.texturePayloads[i];
          if (payload == null || payload.format != variant.format || payload.ByteCount() != variant.textureSizes[i])
            throw new InvalidDataException("Invalid texture variant payload.");
          usable &= supported != null ? supported(payload) : payload.IsSupported();
        }
        InitialDataParts.Validate(variant.initial_data, info.materialSizes, info.meshSizes, variant.textureSizes);
        if (usable && selected == null) selected = variant;
      }
      if (selected == null) throw new NotSupportedException("No GPU texture variant supported by this Receiver" +
        (string.IsNullOrEmpty(preferred) ? "." : ": " + preferred));
      info.textureSizes = selected.textureSizes;
      info.texturePayloads = selected.texturePayloads;
      info.initial_data = selected.initial_data;
      // Keep only the selected model in the runtime snapshot and cache key.
      info.texture_variants = null;
      return selected.format;
    }
  }
}
