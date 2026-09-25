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
        texture.LoadImage(buffer);
      }
      catch(Exception e)
      {
        Debug.LogError("Broken Texture Received in TextureConverter::Deserialize");
        return null;
      }

      return texture;
    }
  }

}