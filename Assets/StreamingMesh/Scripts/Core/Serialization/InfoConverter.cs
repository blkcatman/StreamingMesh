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
      string json = Encoding.UTF8.GetString(data);
      T info = JsonUtility.FromJson<T>(json);
      return info;
    }

    public static T DeserializeFromString<T>(string json) where T : BaseInfo
    {
      T info = JsonUtility.FromJson<T>(json);
      return info;
    }
    
  }

  public class BaseInfo 
  {
  }

  [Serializable]
  public class ChannelInfo : BaseInfo
  {
    public int container_size;
    public int package_size;
    public float frame_interval;
    public int combined_frames;
    public List<string> meshes;
    public List<string> materials;
    public List<string> textures;
    public List<int> meshSizes;
    public List<int> materialSizes;
    public List<int> textureSizes;
    public string data;
    public string stream_info;
    public string audio_info;
    public string audio_clip;
  }

  [Serializable]
  public class MeshInfo : BaseInfo
  {
    public string name;
    public int vertexCount;
    public int subMeshCount;
    public List<string> materialNames;
    public List<int> indicesCounts;
    public List<int> indices;
    public Vector2[] uv;
    public Vector2[] uv2;
    public Vector2[] uv3;
    public Vector2[] uv4;
  }

  public class MaterialInfo : BaseInfo
  {
    public string name;
    public List<MaterialPropertyInfo> properties;
  }

  [Serializable]
  public class MaterialPropertyInfo : BaseInfo
  {
    public string name;
    public int type;
    public string value;
  }

  [Serializable]
  public class StreamInfo : BaseInfo
  {
    public string video;
    public long startTicks;
    public long endTicks;
  }
  
  public class AudioInfo : BaseInfo
  {
    public string audio;
  }

  [Serializable]
  public class StatusInfo : BaseInfo
  {
    public string stat;
  }
}