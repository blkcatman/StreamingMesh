using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

using StreamingMesh;
using StreamingMesh.Core.Rendering;

namespace StreamingMesh.Core.Serialization
{
public class MaterialConverter
  {
    public static Material DeserializeFromBinary(
      byte[] data, int offsetBytes, int dataSize,
      ShaderTable shaderTable, Shader defaultShader, Dictionary<string, Texture2D> textures
    )
    {
      byte[] buffer = new byte[dataSize];
      Buffer.BlockCopy(data, offsetBytes, buffer, 0, dataSize);
      MaterialInfo materialInfo = InfoConverter.Deserialize<MaterialInfo>(buffer);
      string name = materialInfo.name;

      Shader shader = null;
      Material material = new Material(
        shaderTable.GetTable().TryGetValue(name.TrimEnd('\0'), out shader) ? shader : defaultShader
      );
      material.name = name;

      foreach (MaterialPropertyInfo info in materialInfo.properties)
      {
        switch (info.type)
        {
          case 0://ShaderUtil.ShaderPropertyType.Color:
            Color col = JsonUtility.FromJson<Color>(info.value);
            material.SetColor(info.name, col);
            break;
          case 1://ShaderUtil.ShaderPropertyType.Vector:
            Vector4 vec = JsonUtility.FromJson<Vector4>(info.value);
            material.SetVector(info.name, vec);
            break;
          case 2://ShaderUtil.ShaderPropertyType.Float:
            float fValue = JsonUtility.FromJson<float>(info.value);
            material.SetFloat(info.name, fValue);
            break;
          case 3://ShaderUtil.ShaderPropertyType.Range:
            float rValue = JsonUtility.FromJson<float>(info.value);
            material.SetFloat(info.name, rValue);
            break;
          case 4://ShaderUtil.ShaderPropertyType.TexEnv:
            Texture2D texture = null;
            if(textures.TryGetValue(info.value, out texture))
            {
              material.SetTexture(info.name, texture);
            }
            break;
        }
      }

      return material;
    }
  }
  
}