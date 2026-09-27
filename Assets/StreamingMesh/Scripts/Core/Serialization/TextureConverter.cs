using System;
using UnityEngine;

namespace StreamingMesh.Core.Serialization
{
public class TextureConverter
  {
    public static Texture2D DeserializeFromBinary(byte[] data, int offsetBytes, int dataSize)
    {
      byte[] buffer = new byte[dataSize];
      Buffer.BlockCopy(data, offsetBytes, buffer, 0, dataSize);
      Texture2D texture = new Texture2D(2, 2);
      try
      {
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

      return texture;
    }
  }

}
