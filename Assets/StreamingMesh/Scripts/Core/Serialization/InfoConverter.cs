using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace StreamingMesh.Core.Serialization
{
  public sealed class InfoConverter
  {
    public static byte[] Serialize<T>(T info) where T : BaseInfo
    {
      string json = JsonUtility.ToJson(info);
      byte[] data = Encoding.UTF8.GetBytes(json);
      return data;
    }

    public static T Deserialize<T>(byte[] data) where T : BaseInfo
    {
      return Deserialize<T>(data, 0, data.Length);
    }

    public static T Deserialize<T>(byte[] data, int offset, int count) where T : BaseInfo
    {
      string json = Encoding.UTF8.GetString(data, offset, count);
      T info = JsonUtility.FromJson<T>(json);
      return info;
    }

    public static T DeserializeFromString<T>(string json) where T : BaseInfo
    {
      T info = JsonUtility.FromJson<T>(json);
      return info;
    }
    
  }

  [Serializable]
  public class BaseInfo 
  {
  }

  [Serializable]
  public class ChannelInfo : BaseInfo
  {
    public const int CurrentVersion = 6;
    public int protocolVersion;
    public long timebaseHz;
    public int containerSize;
    public int packageSize;
    public float frameInterval;
    public int combinedFrames;
    public List<string> meshes;
    public List<string> materials;
    public List<string> textures;
    public List<string> textureNames;
    public List<int> meshSizes;
    public List<int> materialSizes;
    public List<int> textureSizes;
    public List<TexturePayloadInfo> texturePayloads;
    public List<InitialDataPart> initialData;
    public List<TextureVariantInfo> textureVariants;
    public string streamInfo;
    public string audioInfo;
    public string audioClip;
    public string audioFormat;
    public string audioMimeType;
    public string audioCodec;
    public int audioTimescale;
    public int audioSampleRate;
    public int audioChannels;
    public string audioInit;
    public string audioPlaylist;
  }

  [Serializable]
  public class MeshInfo : BaseInfo
  {
    public string name;
    public int vertexCount;
    public int subMeshCount;
    public List<string> materialIds;
    public List<int> indicesCounts;
    public List<int> indices;
    public Vector2[] uv;
    public Vector2[] uv2;
    public Vector2[] uv3;
    public Vector2[] uv4;
  }

  [Serializable]
  public class MaterialInfo : BaseInfo
  {
    public string id;
    public string name;
    public int version;
    public string shaderName;
    public string[] keywords;
    public int renderQueue;
    public bool enableInstancing;
    public bool doubleSidedGI;
    public int globalIlluminationFlags;
    public List<MaterialTagInfo> tags;
    public List<MaterialPassInfo> passes;
    public List<MaterialPropertyInfo> properties;
  }

  [Serializable]
  public class MaterialTagInfo { public string name, value; }

  [Serializable]
  public class MaterialPassInfo { public string name; public bool enabled; }

  [Serializable]
  public class MaterialPropertyInfo : BaseInfo
  {
    public string name;
    public int type;
    public string value;
    public string textureId;
    public bool hasTextureSettings;
    public Vector2 textureScale, textureOffset;
    public bool textureLinear;
    public bool textureMipChain;
    public int textureFilterMode, textureWrapU, textureWrapV, textureAnisoLevel;
    public float textureMipMapBias;
  }

  [Serializable]
  public class StreamInfo : BaseInfo
  {
    public string video;
    public long startTicks;
    public long endTicks;
    public uint firstSequence;
    public uint lastSequence;
  }
  
  [Serializable]
  public class AudioInfo : BaseInfo
  {
    public string audio;
    public long startTicks;
    public long endTicks;
    public long startSample;
    public int sampleCount;
    public bool discontinuity;
  }

  [Serializable]
  public class StatusInfo : BaseInfo
  {
    public string stat;
  }
}
