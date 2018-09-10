using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.IO;

using StreamingMesh.Core.Serialization;
using StreamingMesh.Core.Rendering;
using StreamingMesh.Lib;
using StreamingMesh.Net;
using StreamingMesh.Utils;

namespace StreamingMesh
{
  [System.Serializable]
  public class ShaderPair : Serialize.KeyAndValue<string, Shader>
  {
    public ShaderPair(string key, Shader value) : base(key, value) {
    }
  }

  [System.Serializable]
  public class ShaderTable : Serialize.TableBase<string, Shader, ShaderPair> {}

  public class Receiver : MonoBehaviour
  {
    [SerializeField]
    string m_ChannelAddress = "http://127.0.0.1:8000/channels/channel_UnityChanTest/";
    [SerializeField]
    string m_PlaylistName = "stream.json";

    [SerializeField]
    bool m_AutoPlay = true;

    //Shaders
    public Shader m_DefaultShader;
    public ShaderTable m_CustomShaders;

    //StreamingRenderers
    StreamingMeshRenderer m_MeshRenderer = null;
    string m_StreamPlayListName = ""; //"stream.stmj"
    string m_CurrentStreamPlaylistData = "";

    StreamingAudioRenderer m_AudioRenderer = null;
    string m_AudioPlayListName = ""; //"stream.stma"
    string m_CurrentAudioPlayListData = "";

    //Fetch Interval Settings
    float m_PollingInterval = 10.0f;
    float m_ElapsedTimeToPolling = 0.0f;

    //Timer Settings
    double m_CurrentTime = 0.0f;

    //Othres
    AudioSource m_AudioSource = null;
    
#if !UNITY_WEBGL
    readonly int m_AudioSampleRate = 44100;
    int m_AudioOffset = 0;
#endif

    string GetAbsoluteURL(string url)
    {
      return m_ChannelAddress + (m_ChannelAddress.EndsWith("/") ? "" : "/") + url;
    }

    IEnumerator Start()
    {

      if(m_AutoPlay && m_MeshRenderer == null)
      {
        yield return StartCoroutine(CreateInitialData());
        m_ElapsedTimeToPolling = m_PollingInterval;
        SetUpdateSettings();
#if STM_DEBUG
  #if UNITY_WEBGL
        Debug.Log("STM_DEBUG StreamingMesh uses Update() for tick time updating.");
  #else
        Debug.Log("STM_DEBUG StreamingMesh uses OnAudioFilterRead() for tick time updating.");
  #endif
#endif
      }

    }

    void Update()
    {
      //If initial data is not loaded, skip update
      if(m_MeshRenderer == null) return;

#if UNITY_WEBGL
      m_CurrentTime += Time.deltaTime;
#else
      m_CurrentTime = (double)m_AudioOffset / (double)(m_AudioSampleRate * 2);
#endif
      m_MeshRenderer.UpdateWithTime(m_CurrentTime);

      if(m_AudioRenderer != null ? m_AudioRenderer.IsPlayable : false)
      {

      }

      //Fetch playlists
      m_ElapsedTimeToPolling += Time.deltaTime;
      if(m_ElapsedTimeToPolling >= m_PollingInterval)
      {
        m_ElapsedTimeToPolling -= m_PollingInterval;
        StartCoroutine(FetchPlayLists());
      }

    }

#if !UNITY_WEBGL
    //INFO: If AudioListener is enabled in current scene, tick time is updated in OnAudioFilterRead
    void OnAudioFilterRead(float[] data, int channels)
    {
      if(m_AudioRenderer != null)
      {
        m_AudioRenderer.SetAudio(ref data, m_AudioOffset);
      }
      m_AudioOffset += data.Length;
    }
#endif

    IEnumerator CreateInitialData()
    {
      HttpWrapper wrapper = new HttpWrapper();

      //Get ChannelInfo
      ChannelInfo channelInfo = null;
      wrapper.RequestInfo<ChannelInfo>(GetAbsoluteURL(m_PlaylistName), info => {channelInfo = info;});
      yield return new WaitUntil(wrapper.RequestFinished);

      if(channelInfo != null)
      {
        //Get PlayLists and others
        m_StreamPlayListName = channelInfo.stream_info;
        m_AudioPlayListName = channelInfo.audio_info;
        m_PollingInterval = channelInfo.frame_interval * channelInfo.combined_frames;

        //Get CombinedData
        byte[] combinedData = null;
        wrapper.RequestBinary(GetAbsoluteURL(channelInfo.data), bin => {
          if(bin != null) {
            combinedData = ExternalTools.Decompress(bin);
          }
        });
        yield return new WaitUntil(wrapper.RequestFinished);

        if(combinedData == null) yield break;

        StreamingMeshRenderer meshRenderer = new StreamingMeshRenderer
        {
          ContainerSize = channelInfo.container_size,
          PackageSize = channelInfo.package_size,
          FrameInterval = channelInfo.frame_interval,
          CombinedFrames = channelInfo.combined_frames
        };

        StreamingAudioRenderer audioRenderer = new StreamingAudioRenderer
        {
          FrameInterval = channelInfo.frame_interval,
          CombinedFrames = channelInfo.combined_frames
        };
#if !UNITY_WEBGL
        if(!string.IsNullOrEmpty(channelInfo.audio_info))
          m_AudioRenderer = audioRenderer;
#else
        if(!string.IsNullOrEmpty(channelInfo.audio_clip))
          m_AudioRenderer = audioRenderer;
#endif

        //Split Textures and Materials and Mesh from CombinedData
        int offsetBytes = 0;

        //Split Textures
        List<string> textureNames = channelInfo.textures;
        List<int> textureSizes = channelInfo.textureSizes;
        for(int i = 0; i < textureSizes.Count; i++)
        {
          int size = textureSizes[i];
          Texture2D texture = TextureConverter.DeserializeFromBinary(combinedData, offsetBytes, size);
          string name = textureNames[i];
          if(texture != null) {
            meshRenderer.AddTexture(name, texture);
          }
          offsetBytes += size;
          yield return null;
        }

        //Split Materials
        List<string> materialNames = channelInfo.materials;
        List<int> materialSizes = channelInfo.materialSizes;
        for(int i = 0; i < materialSizes.Count; i++)
        {
          int size = materialSizes[i];
          Material material = MaterialConverter.DeserializeFromBinary(
            combinedData, offsetBytes, size, m_CustomShaders, m_DefaultShader, meshRenderer.TextureDictionary);
          string name = material.name;
          meshRenderer.AddMaterial(name, material);
          offsetBytes += size;
          yield return null;
        }

        GameObject rootGameObject = new GameObject("RootGameObject");
        rootGameObject.transform.SetParent(transform, false);

        //Split Meshes
        List<string> meshNames = channelInfo.meshes;
        List<int> meshSizes = channelInfo.meshSizes;
        for(int i = 0; i < meshSizes.Count; i++)
        {
          string name = meshNames[i];
          int size = meshSizes[i];
          List<string> refMaterials = null;
          Mesh mesh = MeshConverter.DeserializeFromBinary(
            combinedData, offsetBytes, size, channelInfo.container_size, out refMaterials);

          List<Material> materials = new List<Material>();
          for(int j = 0; j < refMaterials.Count; j++)
          {
            Material material = null;
            if(meshRenderer.MaterialDictionary.TryGetValue(refMaterials[i], out material))
            {
              materials.Add(material);
              yield return null;
            }
          }

          GameObject obj = new GameObject("Mesh_" + name);
          obj.transform.SetParent(rootGameObject.transform, false);
          MeshFilter meshFilter = obj.AddComponent<MeshFilter>();
          MeshRenderer renderer = obj.AddComponent<MeshRenderer>();

          meshFilter.mesh = mesh;
          renderer.materials = materials.ToArray();
          meshRenderer.AddMesh(name, mesh);
          offsetBytes += size;
          yield return null;
        }

        //CreateVertexBuffer;
        meshRenderer.CreateVertexBuffer();
        meshRenderer.CreateVertexContainer(channelInfo.package_size, channelInfo.container_size);
        meshRenderer.RootGameObject = rootGameObject;
        m_MeshRenderer = meshRenderer;
      }
    }

    void SetUpdateSettings()
    {
#if !UNITY_WEBGL
      m_AudioSource = gameObject.AddComponent<AudioSource>();
#else
      m_audioSource = gameObject.AddComponent<WebGLStreamingAudioSource>();
#endif
    }

    IEnumerator FetchPlayLists()
    {
      List<StreamInfo> streamPlayList = new List<StreamInfo>();
      List<AudioInfo> audioPlayList = new List<AudioInfo>();
      
      if(!string.IsNullOrEmpty(m_StreamPlayListName))
      {
        HttpWrapper wrapper = new HttpWrapper();
        wrapper.RequestPlaylistDiff<StreamInfo>(GetAbsoluteURL(m_StreamPlayListName), m_CurrentStreamPlaylistData, (list, newData) => {
          if(list != null) {
            streamPlayList.AddRange(list);
            m_CurrentStreamPlaylistData = newData;
          }
        });
        yield return new WaitUntil(wrapper.RequestFinished);
      }

      if(!string.IsNullOrEmpty(m_AudioPlayListName))
      {
        HttpWrapper wrapper = new HttpWrapper();
        wrapper.RequestPlaylistDiff<AudioInfo>(GetAbsoluteURL(m_AudioPlayListName), m_CurrentAudioPlayListData, (list, newData) => {
          if(list != null) {
            audioPlayList.AddRange(list);
            m_CurrentAudioPlayListData = newData;
          }
        });
        yield return new WaitUntil(wrapper.RequestFinished);
      }

      int length = (streamPlayList.Count > audioPlayList.Count) ? streamPlayList.Count : audioPlayList.Count;
      for(int i = 0; i < length; i++)
      {
        if(i <= streamPlayList.Count - 1 && m_MeshRenderer != null) {
          HttpWrapper wrapper = new HttpWrapper();
          wrapper.RequestBinary(GetAbsoluteURL(streamPlayList[i].video), data => {
            m_MeshRenderer.AddVertexData(Path.GetFileNameWithoutExtension(streamPlayList[i].video), data, streamPlayList[i].startTicks);
          });
          yield return new WaitUntil(wrapper.RequestFinished);
        }
        if(i <= audioPlayList.Count - 1 && m_AudioRenderer != null) {
          HttpWrapper wrapper = new HttpWrapper();
          wrapper.RequestAudio(GetAbsoluteURL(audioPlayList[i].audio), audio => {
            m_AudioRenderer.AddAudioData(Path.GetFileNameWithoutExtension(audioPlayList[i].audio), audio);
          });
          yield return new WaitUntil(wrapper.RequestFinished);
        }
      }

    }

  }
}