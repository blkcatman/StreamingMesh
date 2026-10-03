using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace StreamingMesh.Core.Serialization
{
  public static class MaterialConverter
  {
    public const int CurrentVersion = 2;
    // Wire type numbers describe values, not property indices.
    const int ColorType=0, VectorType=1, FloatType=2, RangeType=3, TextureType=4, IntegerType=5;
    static readonly string[] RenderTags = {"RenderType", "IgnoreProjector", "IgnoreProjection", "DisableBatching", "ForceNoShadowCasting"};

    public static MaterialInfo Serialize(Material material)
    {
      if (material == null || material.shader == null) return null;
      var shader = material.shader;
      var info = new MaterialInfo {
        name=material.name, version=CurrentVersion, shaderName=shader.name,
        keywords=material.shaderKeywords, renderQueue=material.renderQueue,
        enableInstancing=material.enableInstancing, doubleSidedGI=material.doubleSidedGI,
        globalIlluminationFlags=(int)material.globalIlluminationFlags,
        properties=new List<MaterialPropertyInfo>(), tags=new List<MaterialTagInfo>(), passes=new List<MaterialPassInfo>()
      };
      foreach (string tag in RenderTags)
        info.tags.Add(new MaterialTagInfo {name=tag,value=material.GetTag(tag,false,"")});
      var passNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      for (int pass=0; pass<shader.passCount; pass++)
      {
        // Enablement uses LightMode; include names toggled explicitly by scripts.
        string name=material.GetPassName(pass);
        string lightMode=shader.FindPassTagValue(pass,new ShaderTagId("LightMode")).name;
        foreach (string key in new[] {name,lightMode})
          if (!string.IsNullOrEmpty(key) && passNames.Add(key))
            info.passes.Add(new MaterialPassInfo {name=key,enabled=material.GetShaderPassEnabled(key)});
      }
      for (int index=0; index<shader.GetPropertyCount(); index++)
      {
        string name=shader.GetPropertyName(index);
        var property=new MaterialPropertyInfo {name=name};
        switch (shader.GetPropertyType(index))
        {
          case ShaderPropertyType.Color:
            property.type=ColorType; property.value=JsonUtility.ToJson(material.GetColor(name)); break;
          case ShaderPropertyType.Vector:
            property.type=VectorType; property.value=JsonUtility.ToJson(material.GetVector(name)); break;
          case ShaderPropertyType.Float:
          case ShaderPropertyType.Range:
            property.type=shader.GetPropertyType(index)==ShaderPropertyType.Float ? FloatType : RangeType;
            property.value=material.GetFloat(name).ToString("R",CultureInfo.InvariantCulture); break;
          case ShaderPropertyType.Int:
            property.type=IntegerType; property.value=material.GetInteger(name).ToString(CultureInfo.InvariantCulture); break;
          case ShaderPropertyType.Texture:
            property.type=TextureType;
            var texture=material.GetTexture(name);
            property.value=texture == null ? "" : texture.name;
            property.hasTextureSettings=true;
            property.textureScale=material.GetTextureScale(name);
            property.textureOffset=material.GetTextureOffset(name);
            if (texture != null)
            {
              property.textureLinear=!UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
              property.textureMipChain=texture is Texture2D texture2D && texture2D.mipmapCount > 1;
              property.textureFilterMode=(int)texture.filterMode;
              property.textureWrapU=(int)texture.wrapModeU; property.textureWrapV=(int)texture.wrapModeV;
              property.textureAnisoLevel=texture.anisoLevel; property.textureMipMapBias=texture.mipMapBias;
            }
            break;
          default: continue;
        }
        info.properties.Add(property);
      }
      return info;
    }

    static Material FindTemplate(MaterialInfo info, IList<MaterialTemplateBinding> templates)
    {
      if (templates != null)
        foreach (var binding in templates)
          if (binding != null && binding.template != null &&
              string.Equals(binding.materialName,info.name.TrimEnd('\0'),StringComparison.Ordinal)) return binding.template;
      return null;
    }

    public static Shader ResolveShader(MaterialInfo info, ShaderTable shaderTable, Shader defaultShader,
      IList<MaterialTemplateBinding> templates=null, bool useSenderShader=false)
    {
      var template=FindTemplate(info,templates);
      if (template != null) return template.shader;
      if (shaderTable != null && shaderTable.GetTable().TryGetValue(info.name.TrimEnd('\0'),out var custom) && custom != null)
        return custom;
      if (useSenderShader && !string.IsNullOrEmpty(info.shaderName))
      {
        var senderShader=Shader.Find(info.shaderName);
        if (senderShader != null) return senderShader;
        Debug.LogWarning("StreamingMesh Sender shader is unavailable: " + info.shaderName + "; using Receiver default.");
      }
      if (defaultShader == null) throw new InvalidOperationException("StreamingMesh Receiver needs a default shader or a material mapping.");
      return defaultShader;
    }

    public static Material DeserializeFromBinary(byte[] data, int offsetBytes, int dataSize,
      ShaderTable shaderTable, Shader defaultShader, Dictionary<string,Texture2D> textures,
      IList<MaterialTemplateBinding> templates=null, bool useSenderShader=false)
    {
      byte[] buffer=new byte[dataSize]; Buffer.BlockCopy(data,offsetBytes,buffer,0,dataSize);
      return Deserialize(InfoConverter.Deserialize<MaterialInfo>(buffer),shaderTable,defaultShader,textures,templates,useSenderShader);
    }

    public static Material Deserialize(MaterialInfo info, ShaderTable shaderTable, Shader defaultShader,
      Dictionary<string,Texture2D> textures, IList<MaterialTemplateBinding> templates=null, bool useSenderShader=false)
    {
      var template=FindTemplate(info,templates);
      Shader shader=ResolveShader(info,shaderTable,defaultShader,templates,useSenderShader);
      var material=template != null ? new Material(template) : new Material(shader);
      material.name=info.name;
      bool invalidScalar=false;
      try
      {
        if (info.properties != null) foreach (var property in info.properties)
        {
          int index=shader.FindPropertyIndex(property.name);
          if (index < 0) continue;
          var type=shader.GetPropertyType(index);
          switch (property.type)
          {
            case ColorType:
              if (type==ShaderPropertyType.Color || type==ShaderPropertyType.Vector)
                material.SetColor(property.name,JsonUtility.FromJson<Color>(property.value));
              break;
            case VectorType:
              if (type==ShaderPropertyType.Vector || type==ShaderPropertyType.Color)
                material.SetVector(property.name,JsonUtility.FromJson<Vector4>(property.value));
              break;
            case FloatType:
            case RangeType:
              if (type!=ShaderPropertyType.Float && type!=ShaderPropertyType.Range) break;
              // Legacy JsonUtility.ToJson(float) produced "{}": its value is lost.
              if (float.TryParse(property.value,NumberStyles.Float,CultureInfo.InvariantCulture,out float number) &&
                  !float.IsNaN(number) && !float.IsInfinity(number)) material.SetFloat(property.name,number);
              else invalidScalar=true;
              break;
            case IntegerType:
              if (type==ShaderPropertyType.Int && int.TryParse(property.value,NumberStyles.Integer,CultureInfo.InvariantCulture,out int integer))
                material.SetInteger(property.name,integer);
              else invalidScalar=true;
              break;
            case TextureType:
              if (type!=ShaderPropertyType.Texture) break;
              if (string.IsNullOrEmpty(property.value))
              {
                if (property.hasTextureSettings) material.SetTexture(property.name,null);
              }
              else if (textures != null && textures.TryGetValue(property.value,out var texture))
              {
                material.SetTexture(property.name,texture);
                if (property.hasTextureSettings)
                {
                  texture.filterMode=(FilterMode)property.textureFilterMode;
                  texture.wrapModeU=(TextureWrapMode)property.textureWrapU; texture.wrapModeV=(TextureWrapMode)property.textureWrapV;
                  texture.anisoLevel=property.textureAnisoLevel; texture.mipMapBias=property.textureMipMapBias;
                }
              }
              else if (info.version >= CurrentVersion)
                throw new InvalidOperationException("StreamingMesh material '" + info.name + "' is missing texture '" + property.value + "'.");
              if (property.hasTextureSettings)
              {
                material.SetTextureScale(property.name,property.textureScale);
                material.SetTextureOffset(property.name,property.textureOffset);
              }
              break;
          }
        }
        if (info.version >= CurrentVersion && string.Equals(shader.name,info.shaderName,StringComparison.Ordinal))
        {
          material.shaderKeywords=info.keywords ?? new string[0];
          material.renderQueue=info.renderQueue;
          material.enableInstancing=info.enableInstancing; material.doubleSidedGI=info.doubleSidedGI;
          material.globalIlluminationFlags=(MaterialGlobalIlluminationFlags)info.globalIlluminationFlags;
          if (info.tags != null) foreach (var tag in info.tags) material.SetOverrideTag(tag.name,tag.value);
          if (info.passes != null) foreach (var pass in info.passes) material.SetShaderPassEnabled(pass.name,pass.enabled);
        }
        if (invalidScalar)
          Debug.LogWarning("StreamingMesh material '" + info.name + "' contains lost/invalid numeric values; kept template/shader defaults. Recreate the channel with the updated Sender.");
        return material;
      }
      catch
      {
        if (Application.isPlaying) UnityEngine.Object.Destroy(material); else UnityEngine.Object.DestroyImmediate(material);
        throw;
      }
    }
  }
}
