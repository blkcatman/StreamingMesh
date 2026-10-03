using System;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace StreamingMesh.Core.Serialization
{
  public enum GpuTextureFormat { BC7, DXT1, DXT5, ETC2_RGB, ETC2_RGBA8, ASTC_4x4, ASTC_6x6 }

  [Serializable]
  public sealed class TexturePayloadInfo
  {
    public string format;
    public int width, height, mipCount;
    public bool linear;

    public TextureFormat UnityFormat()
    {
      switch (format)
      {
        case "BC7": return TextureFormat.BC7;
        case "DXT1": return TextureFormat.DXT1;
        case "DXT5": return TextureFormat.DXT5;
        case "ETC2_RGB": return TextureFormat.ETC2_RGB;
        case "ETC2_RGBA8": return TextureFormat.ETC2_RGBA8;
        case "ASTC_4x4": return TextureFormat.ASTC_4x4;
        case "ASTC_6x6": return TextureFormat.ASTC_6x6;
        default: throw new InvalidDataException("Unknown GPU texture format: " + format);
      }
    }

    public int ByteCount()
    {
      UnityFormat();
      if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || mipCount <= 0 || mipCount > 15)
        throw new InvalidDataException("Invalid GPU texture dimensions/mip count.");
      int block = format == "ASTC_6x6" ? 6 : 4;
      int blockBytes = format == "DXT1" || format == "ETC2_RGB" ? 8 : 16;
      long total = 0;
      int w = width, h = height;
      for (int mip = 0; mip < mipCount; mip++)
      {
        total += (long)((w + block - 1) / block) * ((h + block - 1) / block) * blockBytes;
        if (w == 1 && h == 1 && mip + 1 < mipCount) throw new InvalidDataException("Too many GPU texture mip levels.");
        w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
      }
      if (total > InitialDataParts.MaximumResourceBytes) throw new InvalidDataException("GPU texture exceeds its budget.");
      return (int)total;
    }

    public Texture2D Create()
    {
      ByteCount();
      var unityFormat = UnityFormat();
      var graphicsFormat = GraphicsFormatUtility.GetGraphicsFormat(unityFormat, !linear);
      if (width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize ||
          !SystemInfo.SupportsTextureFormat(unityFormat) || !SystemInfo.IsFormatSupported(graphicsFormat, GraphicsFormatUsage.Sample))
        throw new NotSupportedException("GPU texture " + format + " is unsupported on " + SystemInfo.graphicsDeviceType + ". Export a format supported by this Receiver.");
      var texture = new Texture2D(width, height, unityFormat, mipCount, linear);
      try
      {
        if (texture.format != unityFormat || texture.graphicsFormat != graphicsFormat || texture.GetRawTextureData<byte>().Length != ByteCount())
          throw new InvalidDataException("Unexpected GPU texture allocation.");
        return texture;
      }
      catch { if (Application.isPlaying) UnityEngine.Object.Destroy(texture); else UnityEngine.Object.DestroyImmediate(texture); throw; }
    }
  }
}
