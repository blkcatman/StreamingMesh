using System;
using UnityEngine;

namespace StreamingMesh.Core.Serialization
{
public class TextureConverter
  {
    public static byte[] SerializeToPNG(Texture texture)
    {
#if UNITY_EDITOR
      if (texture == null) return null;
      if (texture.dimension != UnityEngine.Rendering.TextureDimension.Tex2D)
        throw new ArgumentException("StreamingMesh PNG export requires a 2D texture.");
      var importer = UnityEditor.AssetImporter.GetAtPath(UnityEditor.AssetDatabase.GetAssetPath(texture)) as UnityEditor.TextureImporter;
      bool normal = importer != null && importer.textureType == UnityEditor.TextureImporterType.NormalMap;
      bool srgb = !normal && UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
      RenderTexture previous=RenderTexture.active;
      bool previousSrgbWrite=GL.sRGBWrite;
      RenderTexture temporary=null; Texture2D readable=null; Material exportNormal=null;
      try
      {
        temporary=RenderTexture.GetTemporary(texture.width,texture.height,0,RenderTextureFormat.ARGB32,
          srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        GL.sRGBWrite=srgb && QualitySettings.activeColorSpace==ColorSpace.Linear;
        if (normal)
        {
          var shader=Shader.Find("Hidden/StreamingMesh/ExportNormal");
          if (shader == null) throw new InvalidOperationException("StreamingMesh normal export shader is missing.");
          exportNormal=new Material(shader);
          Graphics.Blit(texture,temporary,exportNormal);
        }
        else Graphics.Blit(texture,temporary);
        RenderTexture.active=temporary;
        readable=new Texture2D(texture.width,texture.height,TextureFormat.RGBA32,false,!srgb);
        readable.ReadPixels(new Rect(0,0,texture.width,texture.height),0,0,false); readable.Apply(false,false);
        return readable.EncodeToPNG();
      }
      finally
      {
        RenderTexture.active=previous; GL.sRGBWrite=previousSrgbWrite;
        if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
        if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
        if (exportNormal != null) UnityEngine.Object.DestroyImmediate(exportNormal);
      }
#else
      return null;
#endif
    }

    public static Texture2D DeserializeFromBinary(byte[] data, int offsetBytes, int dataSize, bool linear = false, bool mipChain = true, bool memoryDiagnostics = false)
    {
      if (memoryDiagnostics) StreamingMesh.Core.Rendering.ReceiverMemoryDiagnostics.Log("texture/png-copy-before",dataSize);
      byte[] buffer = new byte[dataSize];
      Buffer.BlockCopy(data, offsetBytes, buffer, 0, dataSize);
      if (memoryDiagnostics) StreamingMesh.Core.Rendering.ReceiverMemoryDiagnostics.Log("texture/png-copy-after",dataSize);
      Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain, linear);
      try
      {
        if (memoryDiagnostics) StreamingMesh.Core.Rendering.ReceiverMemoryDiagnostics.Log("texture/load-image-before",dataSize);
        if (!texture.LoadImage(buffer, true))
        {
          UnityEngine.Object.Destroy(texture);
          return null;
        }
      }
      catch(Exception e)
      {
        UnityEngine.Object.Destroy(texture);
        Debug.LogError("Broken Texture Received in TextureConverter::Deserialize");
        return null;
      }

      if (memoryDiagnostics) StreamingMesh.Core.Rendering.ReceiverMemoryDiagnostics.Log("texture/load-image-after",dataSize,
        $"width={texture.width} height={texture.height} mipCount={texture.mipmapCount}");
      return texture;
    }
  }

}
