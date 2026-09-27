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

    /// <summary>Configure an inactive receiver before activating it and starting requests.</summary>
    public void ConfigureChannel(string address)
    {
      if (gameObject.activeInHierarchy)
        throw new InvalidOperationException("Configure the channel before activating the receiver.");
      if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
          (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
          !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        throw new ArgumentException("Enter an HTTP(S) channel directory URL.", nameof(address));
      m_ChannelAddress = uri.AbsoluteUri.TrimEnd('/') + "/";
    }
    [SerializeField]
    string m_PlaylistName = "stream.json";

    [SerializeField]
    bool m_AutoPlay = true;

    [SerializeField] ReceiverDecodeBackend m_DecodeBackend = ReceiverDecodeBackend.Auto;
    [SerializeField] ReceiverNormalMode m_NormalMode = ReceiverNormalMode.Auto;

    //Shaders
    public Shader m_DefaultShader;
    public ShaderTable m_CustomShaders;

    //StreamingRenderers
    StreamingMeshRenderer m_MeshRenderer = null;
    StreamingMeshRenderer m_InitializingMeshRenderer;
    public bool IsInitializing { get; private set; }
    public string ConnectionStatus { get; private set; } = "Waiting to connect";
    string m_StreamPlayListName = ""; //"stream.stmj"
    string m_CurrentStreamPlaylistData = "";

    StreamingAudioRenderer m_AudioRenderer = null;
    IStreamingAudioPlayer m_Fmp4AudioPlayer = null;
    bool m_UseFmp4Audio = false;
    string m_AudioPlayListName = ""; //"stream.stma"
    string m_CurrentAudioPlayListData = "";

    //Fetch Interval Settings
    float m_PollingInterval = 10.0f;
    float m_ElapsedTimeToPolling = 0.0f;

    //Timer Settings
    double m_CurrentTime = 0.0f;
    bool m_PlaybackClockStarted = false;
    bool m_AudioSeekRequested;
    bool m_AudioPlaying;
    float m_NextSyncLogTime;
    int m_StatsFrames;
    double m_StatsSeconds;
    float m_StatsWorstFrame;

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

    void Awake()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
      string overrideAddress = GetQueryValue(Application.absoluteURL, "channel");
      if (!string.IsNullOrEmpty(overrideAddress))
        m_ChannelAddress = overrideAddress;
#endif
    }

    static string GetQueryValue(string absoluteUrl, string key)
    {
      if (string.IsNullOrEmpty(absoluteUrl))
        return null;

      int queryStart = absoluteUrl.IndexOf('?');
      if (queryStart < 0 || queryStart == absoluteUrl.Length - 1)
        return null;

      string[] pairs = absoluteUrl.Substring(queryStart + 1).Split('&');
      for (int i = 0; i < pairs.Length; i++)
      {
        string[] parts = pairs[i].Split(new[] { '=' }, 2);
        if (parts.Length == 2 && string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
          return Uri.UnescapeDataString(parts[1]);
      }
      return null;
    }

    IEnumerator Start()
    {

      if(m_AutoPlay && m_MeshRenderer == null)
      {
        IsInitializing = true;
        try { yield return StartCoroutine(CreateInitialData()); }
        finally { IsInitializing = false; }
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

    void OnDestroy()
    {
      m_InitializingMeshRenderer?.Dispose();
      m_InitializingMeshRenderer = null;
      if(m_MeshRenderer != null)
      {
        m_MeshRenderer.Dispose();
        m_MeshRenderer = null;
      }
      if (m_Fmp4AudioPlayer != null)
      {
        m_Fmp4AudioPlayer.Dispose();
        m_Fmp4AudioPlayer = null;
      }
    }

    void Update()
    {
      //If initial data is not loaded, skip update
      if(m_MeshRenderer == null) return;
      m_StatsFrames++;
      m_StatsSeconds += Time.unscaledDeltaTime;
      m_StatsWorstFrame = Mathf.Max(m_StatsWorstFrame, Time.unscaledDeltaTime);

      if (m_UseFmp4Audio && m_Fmp4AudioPlayer != null)
      {
        UpdateSynchronizedPlayback();
      }
#if UNITY_WEBGL
      else if (!m_PlaybackClockStarted)
      {
        double startTime;
        if (m_MeshRenderer.TryGetPlaybackStartTime(out startTime))
        {
          m_CurrentTime = startTime;
          m_PlaybackClockStarted = true;
        }
      }
#else
      else if (!m_UseFmp4Audio)
      {
        m_CurrentTime = (double)m_AudioOffset / (double)(m_AudioSampleRate * 2);
      }
#endif
      if (!m_UseFmp4Audio)
        m_MeshRenderer.UpdateWithTime(m_CurrentTime);
#if UNITY_WEBGL
      if (!m_UseFmp4Audio && m_PlaybackClockStarted &&
          m_MeshRenderer.PlaybackState == StreamingPlaybackState.Playing)
        m_CurrentTime += Time.deltaTime;
#endif

      //Fetch playlists
      m_ElapsedTimeToPolling += Time.deltaTime;
      if(m_ElapsedTimeToPolling >= m_PollingInterval)
      {
        m_ElapsedTimeToPolling -= m_PollingInterval;
        StartCoroutine(FetchPlayLists());
      }

    }

    void UpdateSynchronizedPlayback()
    {
      // Decode and fetch continue while the audio clock is paused.
      if (!m_PlaybackClockStarted)
      {
        m_Fmp4AudioPlayer.SetPlaying(false);
        m_MeshRenderer.UpdateWithTime(m_CurrentTime);
        if (!m_AudioSeekRequested)
        {
          ConnectionStatus = "Buffering mesh and audio...";
          double startTime;
          if (m_MeshRenderer.BufferedFrameCount < 2 ||
              !m_MeshRenderer.TryGetPlaybackStartTime(out startTime) ||
              m_Fmp4AudioPlayer.State < 1)
            return;
          m_CurrentTime = startTime;
          m_Fmp4AudioPlayer.Seek(startTime);
          m_AudioSeekRequested = true;
          return;
        }
      }

      double audioTime;
      if (!m_Fmp4AudioPlayer.TryGetTime(out audioTime))
      {
        m_Fmp4AudioPlayer.SetPlaying(false);
        m_AudioPlaying = false;
        ConnectionStatus = "Waiting for audio...";
        if (Debug.isDebugBuild && Time.realtimeSinceStartup >= m_NextSyncLogTime)
        {
          m_NextSyncLogTime = Time.realtimeSinceStartup + 5;
          Debug.Log($"StreamingMesh audio wait: state={m_Fmp4AudioPlayer.State}, seekRequested={m_AudioSeekRequested}, mesh={m_MeshRenderer.PresentedTime:F3}, bufferedUntil={m_MeshRenderer.BufferedUntil:F3}, gpu={m_MeshRenderer.IsGpuResident}");
        }
        return;
      }

      m_CurrentTime = audioTime;
      m_MeshRenderer.UpdateWithTime(audioTime);
      // A recovered keyframe can start after the paused audio position. Seek to
      // the new common range instead of waiting forever for the missing poses.
      if (!m_AudioPlaying && m_MeshRenderer.TryGetPlaybackStartTime(out double recoveredStart) &&
          recoveredStart > audioTime + 0.001)
      {
        m_Fmp4AudioPlayer.SetPlaying(false);
        m_Fmp4AudioPlayer.Seek(recoveredStart);
        m_CurrentTime = recoveredStart;
        ConnectionStatus = "Recovering at keyframe...";
        return;
      }
      // Pause before exhausting the decoded range; resume with a larger margin.
      double lead = m_AudioPlaying ? 0.05 : Math.Max(StreamingMeshRenderer.ResumeBufferSeconds, m_MeshRenderer.FrameInterval * 2);
      bool ready = m_MeshRenderer.CanPlayAt(audioTime, lead);
      m_Fmp4AudioPlayer.SetPlaying(ready);
      if (ready != m_AudioPlaying)
        Debug.Log($"StreamingMesh sync: {(ready ? "play" : "buffer")}, audio={audioTime:F3}, mesh={m_MeshRenderer.PresentedTime:F3}, bufferedUntil={m_MeshRenderer.BufferedUntil:F3}");
      m_AudioPlaying = ready;
      if (ready) m_PlaybackClockStarted = true;
      ConnectionStatus = ready ? "Playing / audio and mesh synchronized" : "Buffering / audio paused";
      if (Debug.isDebugBuild && Time.realtimeSinceStartup >= m_NextSyncLogTime)
      {
        m_NextSyncLogTime = Time.realtimeSinceStartup + 5;
        Debug.Log($"StreamingMesh sync clock: audio={audioTime:F3}, mesh={m_MeshRenderer.PresentedTime:F3}, bufferedUntil={m_MeshRenderer.BufferedUntil:F3}, playing={ready}, gpu={m_MeshRenderer.IsGpuResident}, pending={m_MeshRenderer.PendingGpuFrameCount}, queued={m_MeshRenderer.EncodedFrameCount}, fps={m_StatsFrames / Math.Max(0.001, m_StatsSeconds):F1}, worstFrameMs={m_StatsWorstFrame * 1000:F1}");
        m_StatsFrames = 0;
        m_StatsSeconds = 0;
        m_StatsWorstFrame = 0;
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
      ConnectionStatus = "Loading channel information...";
      HttpWrapper wrapper = new HttpWrapper();

      //Get ChannelInfo
      ChannelInfo channelInfo = null;
      wrapper.RequestInfo<ChannelInfo>(GetAbsoluteURL(m_PlaylistName), info => {channelInfo = info;});
      yield return new WaitUntil(wrapper.RequestFinished);

      if (channelInfo == null)
      {
        ConnectionStatus = "Channel request failed. Check the URL, server and local network permission.";
        yield break;
      }

      if(channelInfo != null)
      {
        //Get PlayLists and others
        m_StreamPlayListName = channelInfo.stream_info;
        m_AudioPlayListName = channelInfo.audio_info;
        m_PollingInterval = channelInfo.frame_interval * channelInfo.combined_frames;

        if (string.Equals(channelInfo.audio_format, "fmp4", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(channelInfo.audio_init) &&
            !string.IsNullOrEmpty(channelInfo.audio_info))
        {
          m_Fmp4AudioPlayer = StreamingAudioPlayerFactory.Create();
          m_UseFmp4Audio = m_Fmp4AudioPlayer != null &&
            m_Fmp4AudioPlayer.Initialize(m_ChannelAddress, channelInfo);
        }

        //Get CombinedData
        ConnectionStatus = "Downloading model and textures. Please wait...";
        byte[] combinedData = null;
        wrapper.RequestBinary(GetAbsoluteURL(channelInfo.data), bin => {
          if(bin != null) {
            combinedData = ExternalTools.Decompress(bin);
          }
        });
        yield return new WaitUntil(wrapper.RequestFinished);

        if(combinedData == null)
        {
          ConnectionStatus = "Model download failed. Reconnect to retry.";
          yield break;
        }

        StreamingMeshRenderer meshRenderer = new StreamingMeshRenderer
        {
          ContainerSize = channelInfo.container_size,
          PackageSize = channelInfo.package_size,
          FrameInterval = channelInfo.frame_interval,
          CombinedFrames = channelInfo.combined_frames
        };
        m_InitializingMeshRenderer = meshRenderer;

        StreamingAudioRenderer audioRenderer = new StreamingAudioRenderer
        {
          FrameInterval = channelInfo.frame_interval,
          CombinedFrames = channelInfo.combined_frames
        };
#if !UNITY_WEBGL
        // StreamingAudioRenderer is the legacy Ogg path. Native fMP4 playback
        // needs a platform demuxer and must not accidentally request *.m4s.ogg.
        if(!string.IsNullOrEmpty(channelInfo.audio_info) &&
           !string.Equals(channelInfo.audio_format, "fmp4", StringComparison.OrdinalIgnoreCase))
          m_AudioRenderer = audioRenderer;
#else
        if(!string.IsNullOrEmpty(channelInfo.audio_clip))
          m_AudioRenderer = audioRenderer;
#endif

        //Split Textures and Materials and Mesh from CombinedData
        int offsetBytes = 0;

        // Material records follow the texture payloads. Inspect them first so a
        // fallback shader doesn't allocate large maps it never samples.
        var requiredTextures = new HashSet<string>();
        int materialOffset = 0;
        foreach (int size in channelInfo.textureSizes) materialOffset += size;
        foreach (int size in channelInfo.materialSizes)
        {
          var bytes = new byte[size];
          Buffer.BlockCopy(combinedData, materialOffset, bytes, 0, size);
          var info = InfoConverter.Deserialize<MaterialInfo>(bytes);
          Shader shader;
          if (!m_CustomShaders.GetTable().TryGetValue(info.name.TrimEnd('\0'), out shader))
            shader = m_DefaultShader;
          foreach (var property in info.properties)
          {
            int index = shader.FindPropertyIndex(property.name);
            if (property.type == 4 && index >= 0 &&
                shader.GetPropertyType(index) == UnityEngine.Rendering.ShaderPropertyType.Texture)
              requiredTextures.Add(property.value);
          }
          materialOffset += size;
        }

        //Split Textures
        List<string> textureNames = channelInfo.textures;
        List<int> textureSizes = channelInfo.textureSizes;
        for(int i = 0; i < textureSizes.Count; i++)
        {
          ConnectionStatus = "Loading textures...";
          int size = textureSizes[i];
          string name = textureNames[i];
          Texture2D texture = requiredTextures.Contains(name)
            ? TextureConverter.DeserializeFromBinary(combinedData, offsetBytes, size) : null;
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
            if(meshRenderer.MaterialDictionary.TryGetValue(refMaterials[j], out material))
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
        meshRenderer.DecodeBackend = m_DecodeBackend;
        meshRenderer.NormalMode = m_NormalMode;
        meshRenderer.CreateVertexContainer(channelInfo.package_size, channelInfo.container_size);
        meshRenderer.RootGameObject = rootGameObject;
        m_MeshRenderer = meshRenderer;
        m_InitializingMeshRenderer = null;
        ConnectionStatus = "Model ready / receiving stream";
        Debug.Log($"StreamingMesh ready: {channelInfo.meshes.Count} meshes, {meshRenderer.TextureDictionary.Count} textures.");
      }
    }

    void SetUpdateSettings()
    {
#if !UNITY_WEBGL
      m_AudioSource = gameObject.AddComponent<AudioSource>();
#endif
    }

    bool m_FetchingPlaylists;
    IEnumerator FetchPlayLists()
    {
      if (m_FetchingPlaylists) yield break;
      m_FetchingPlaylists = true;
      try { yield return FetchPlayListsCore(); }
      finally { m_FetchingPlaylists = false; }
    }

    IEnumerator FetchPlayListsCore()
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

      if(!string.IsNullOrEmpty(m_AudioPlayListName) && !m_UseFmp4Audio)
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
          var renderer = m_MeshRenderer;
          var info = streamPlayList[i];
          // Bound decoded payload memory when joining a long-running channel.
          while (!renderer.CanAcceptChunk)
          {
            if (this == null || m_MeshRenderer != renderer) yield break;
            yield return null;
          }
          bool accepted = false;
          for (int attempt = 0; attempt < 3 && !accepted; attempt++)
          {
            byte[] bytes = null;
            var wrapper = new HttpWrapper();
            // This callback owns only a local byte array, not a Receiver that
            // may already have been destroyed by Connect / Reconnect.
            wrapper.RequestBinary(GetAbsoluteURL(info.video), data => bytes = data);
            yield return new WaitUntil(wrapper.RequestFinished);
            if (this == null || m_MeshRenderer != renderer) yield break;
            if (bytes != null)
            {
              var parsed = renderer.AddVertexDataAsync(Path.GetFileNameWithoutExtension(info.video), bytes, info.startTicks);
              yield return new WaitUntil(() => parsed.IsCompleted);
              if (this == null || m_MeshRenderer != renderer) yield break;
              accepted = !parsed.IsFaulted && !parsed.IsCanceled && parsed.Result;
            }
            if (!accepted) yield return new WaitForSecondsRealtime(0.25f * (attempt + 1));
          }
          if (!accepted) Debug.LogWarning("StreamingMesh chunk download failed after retries: " + info.video);
        }
        if(i <= audioPlayList.Count - 1 && m_AudioRenderer != null) {
          HttpWrapper wrapper = new HttpWrapper();
          wrapper.RequestAudio(GetAbsoluteURL(audioPlayList[i].audio), audio => {
            if (this != null && m_AudioRenderer != null) m_AudioRenderer.AddAudioData(Path.GetFileNameWithoutExtension(audioPlayList[i].audio), audio);
          });
          yield return new WaitUntil(wrapper.RequestFinished);
        }
      }

    }

  }
}
