using UnityEngine;
using UnityEngine.Networking;
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

  [Serializable]
  public class MaterialTemplateBinding
  {
    public string materialName;
    public string materialId;
    public Material template;
  }

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

    [SerializeField, Tooltip("Start playback automatically after the initial audio and mesh buffer is ready.")]
    bool m_AutoPlayAfterBuffering = true;

    public double CurrentTimeSeconds { get { return m_CurrentTime; } }
    public double DurationSeconds { get { return m_DurationSeconds; } }
    public bool IsPlaying { get { return m_AudioPlaying; } }
    public bool IsPlaybackRequested { get { return m_WantsToPlay; } }
    public bool SupportsTransport { get { return m_UseFmp4Audio && m_Fmp4AudioPlayer != null; } }

    /// <summary>Configure an inactive receiver before it starts loading.</summary>
    public void ConfigurePlayback(bool autoPlayAfterBuffering, double startTimeSeconds = 0)
    {
      if (gameObject.activeInHierarchy)
        throw new InvalidOperationException("Configure playback before activating the receiver.");
      m_AutoPlayAfterBuffering = autoPlayAfterBuffering;
      m_WantsToPlay = autoPlayAfterBuffering;
      m_PlayIntentConfigured = true;
      m_RequestedStartTime = Math.Max(0, startTimeSeconds);
      m_CurrentTime = m_RequestedStartTime;
    }

    public void Play()
    {
      m_WantsToPlay = true;
      m_PlayIntentConfigured = true;
    }

    public void Pause()
    {
      m_WantsToPlay = false;
      m_PlayIntentConfigured = true;
      m_Fmp4AudioPlayer?.SetPlaying(false);
      m_AudioPlaying = false;
      if (SupportsTransport) ConnectionStatus = "Paused";
    }

    public void Stop()
    {
      Pause();
      Seek(0);
    }

    /// <summary>Rebuffer from the keyframe chunk before the requested time.</summary>
    public void Seek(double timeSeconds)
    {
      if (double.IsNaN(timeSeconds) || double.IsInfinity(timeSeconds))
        throw new ArgumentOutOfRangeException(nameof(timeSeconds));
      m_RequestedStartTime = Math.Max(0, timeSeconds);
      if (m_DurationSeconds > 0)
        m_RequestedStartTime = Math.Min(m_RequestedStartTime, Math.Max(0, m_DurationSeconds - 0.05));
      m_CurrentTime = m_RequestedStartTime;
      StopAllCoroutines();
      ResetPlaybackData();
      if (isActiveAndEnabled) StartCoroutine(InitializePlayback());
    }

    [SerializeField] ReceiverDecodeBackend m_DecodeBackend = ReceiverDecodeBackend.Auto;
    [SerializeField, Tooltip("Reconnect after changing vertex settings. Tangent reconstruction enables normals on target meshes even when this is None.")]
    ReceiverNormalMode m_NormalMode = ReceiverNormalMode.Auto;
    [SerializeField, Tooltip("Auto uses the explicit Tangent Material IDs list. Recalculate processes all meshes with UV0 and triangles. Reconnect after changing this setting.")]
    ReceiverTangentMode m_TangentMode = ReceiverTangentMode.Auto;
    [SerializeField, Tooltip("Stream material IDs whose shaders require tangents (any submesh enables its entire mesh). Used in Auto mode; reconnect after editing.")]
    string[] m_TangentMaterialIds = new string[0];

    //Shaders
    public Shader m_DefaultShader;
    public ShaderTable m_CustomShaders;
    [SerializeField, Tooltip("Clone a local material for this stream material ID, then apply Sender properties and state. References also keep the sample's shader variants in builds.")]
    MaterialTemplateBinding[] m_MaterialTemplates = new MaterialTemplateBinding[0];
    [SerializeField, Tooltip("Use the Sender shader when no material template or explicit shader mapping is set. The shader and required variants must be included in the Receiver build.")]
    bool m_UseSenderShader;

    [SerializeField, Tooltip("Build templates without local textures. Enable only when the Sender supplies every required texture property.")]
    bool m_StreamTexturesOnlyTemplates;

#if UNITY_EDITOR
    void OnValidate()
    {
      var identities = new ResourceIdentityRegistry();
      foreach (var binding in m_MaterialTemplates ?? new MaterialTemplateBinding[0])
        if (binding != null && binding.template != null && string.IsNullOrEmpty(binding.materialId))
          binding.materialId = identities.GetId(binding.template);
    }
#endif

    [SerializeField, Tooltip("Log allocating memory diagnostics during initialization and every two seconds of playback.")]
    bool m_LogMemoryDiagnostics;
    double m_NextMemoryLog;
    void MemoryCheckpoint(string stage, long bytes=0, string detail="", StreamingMeshRenderer renderer=null)
    {
      if (m_LogMemoryDiagnostics) ReceiverMemoryDiagnostics.Log(stage,bytes,detail,renderer);
    }

    //StreamingRenderers
    StreamingMeshRenderer m_MeshRenderer = null;
    StreamingMeshRenderer m_InitializingMeshRenderer;
    InitialResourceLoader m_InitialResourceLoader;
    UnityWebRequest m_InitialResourceRequest;
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
    bool m_WantsToPlay;
    bool m_PlayIntentConfigured;
    double m_RequestedStartTime;
    double m_DurationSeconds;
    GameObject m_StreamRoot;
    int m_PlaybackGeneration;
    // Positive values delay the mesh relative to the decoded audio clock.
    // This can compensate for device-specific acoustic output latency.
    public double MeshPresentationDelaySeconds { get; set; }
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
      if (!m_PlayIntentConfigured) m_WantsToPlay = m_AutoPlayAfterBuffering;
      if (m_AutoPlay && m_MeshRenderer == null)
        yield return InitializePlayback();
    }

    IEnumerator InitializePlayback()
    {
      IsInitializing = true;
      MemoryCheckpoint("initial/start");
      try { yield return CreateInitialData(); }
      finally
      {
        IsInitializing = false;
        if (m_MeshRenderer == null)
        {
          string status = ConnectionStatus;
          ResetPlaybackData();
          ConnectionStatus = status;
        }
      }
      if (m_MeshRenderer == null) yield break;
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

    void ResetPlaybackData()
    {
      m_PlaybackGeneration++;
      m_InitialResourceRequest?.Dispose();
      m_InitialResourceRequest = null;
      m_InitialResourceLoader?.Dispose();
      m_InitialResourceLoader = null;
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
      if (m_StreamRoot != null)
      {
        Destroy(m_StreamRoot);
        m_StreamRoot = null;
      }
      m_AudioRenderer = null;
      m_UseFmp4Audio = false;
      m_StreamPlayListName = "";
      m_AudioPlayListName = "";
      m_CurrentStreamPlaylistData = "";
      m_CurrentAudioPlayListData = "";
      m_FetchingPlaylists = false;
      m_PlaybackClockStarted = false;
      m_AudioSeekRequested = false;
      m_AudioPlaying = false;
      IsInitializing = false;
      m_PollingInterval = 10;
      m_ElapsedTimeToPolling = 0;
#if !UNITY_WEBGL
      m_AudioOffset = 0;
#endif
      ConnectionStatus = "Buffering mesh and audio...";
    }

    void OnDestroy()
    {
      ResetPlaybackData();
    }

    void Update()
    {
      if (m_LogMemoryDiagnostics && !IsInitializing && Time.realtimeSinceStartupAsDouble >= m_NextMemoryLog)
      {
        m_NextMemoryLog = Time.realtimeSinceStartupAsDouble + 2;
        MemoryCheckpoint("playback/sample",0,"",m_MeshRenderer ?? m_InitializingMeshRenderer);
      }
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
        m_MeshRenderer.UpdateWithTime(m_CurrentTime - MeshPresentationDelaySeconds);
        if (!m_AudioSeekRequested)
        {
          ConnectionStatus = "Buffering mesh and audio...";
          double startTime;
          if (m_MeshRenderer.BufferedFrameCount < 2 ||
              !m_MeshRenderer.TryGetPlaybackStartTime(out startTime) ||
              m_Fmp4AudioPlayer.State < 1)
            return;
          m_CurrentTime = Math.Max(m_RequestedStartTime,
            Math.Max(0.0, startTime + MeshPresentationDelaySeconds));
          m_Fmp4AudioPlayer.Seek(m_CurrentTime);
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
      double meshTime = audioTime - MeshPresentationDelaySeconds;
      m_MeshRenderer.UpdateWithTime(meshTime);
      // A recovered keyframe can start after the paused audio position. Seek to
      // the new common range instead of waiting forever for the missing poses.
      if (!m_AudioPlaying && m_MeshRenderer.TryGetPlaybackStartTime(out double recoveredStart) &&
          recoveredStart > meshTime + 0.001)
      {
        m_Fmp4AudioPlayer.SetPlaying(false);
        double recoveredAudioTime = Math.Max(0.0, recoveredStart + MeshPresentationDelaySeconds);
        m_Fmp4AudioPlayer.Seek(recoveredAudioTime);
        m_CurrentTime = recoveredAudioTime;
        ConnectionStatus = "Recovering at keyframe...";
        return;
      }
      // Pause before exhausting the decoded range; resume with a larger margin.
      double lead = m_AudioPlaying ? 0.05 : Math.Max(StreamingMeshRenderer.ResumeBufferSeconds, m_MeshRenderer.FrameInterval * 2);
      if (m_DurationSeconds > 0)
        lead = Math.Min(lead, Math.Max(0.0, m_DurationSeconds - meshTime - m_MeshRenderer.FrameInterval));
      bool ready = m_MeshRenderer.CanPlayAt(meshTime, lead);
      bool shouldPlay = ready && m_WantsToPlay;
      m_Fmp4AudioPlayer.SetPlaying(shouldPlay);
      if (shouldPlay != m_AudioPlaying)
        Debug.Log($"StreamingMesh sync: {(shouldPlay ? "play" : "pause")}, audio={audioTime:F3}, meshTarget={meshTime:F3}, mesh={m_MeshRenderer.PresentedTime:F3}, bufferedUntil={m_MeshRenderer.BufferedUntil:F3}");
      m_AudioPlaying = shouldPlay;
      if (ready) m_PlaybackClockStarted = true;
      ConnectionStatus = ready
        ? (m_WantsToPlay ? "Playing / audio and mesh synchronized" : "Paused / ready to play")
        : "Buffering / audio paused";
      if (Debug.isDebugBuild && Time.realtimeSinceStartup >= m_NextSyncLogTime)
      {
        m_NextSyncLogTime = Time.realtimeSinceStartup + 5;
        Debug.Log($"StreamingMesh sync clock: audio={audioTime:F3}, meshTarget={meshTime:F3}, mesh={m_MeshRenderer.PresentedTime:F3}, bufferedUntil={m_MeshRenderer.BufferedUntil:F3}, playing={ready}, gpu={m_MeshRenderer.IsGpuResident}, pending={m_MeshRenderer.PendingGpuFrameCount}, queued={m_MeshRenderer.EncodedFrameCount}, fps={m_StatsFrames / Math.Max(0.001, m_StatsSeconds):F1}, worstFrameMs={m_StatsWorstFrame * 1000:F1}");
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
      if (channelInfo.protocol_version != ChannelInfo.CurrentVersion)
      {
        ConnectionStatus = "Unsupported channel format. Recreate the channel with the current Sender.";
        yield break;
      }
      if (channelInfo.initial_data == null || channelInfo.initial_data.Count == 0)
      {
        ConnectionStatus = "Sender is uploading initial resources. Reconnect when the upload finishes.";
        yield break;
      }
      ValidateResourceTables(channelInfo);

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

        StreamingMeshRenderer meshRenderer = new StreamingMeshRenderer
        {
          ContainerSize = channelInfo.container_size,
          PackageSize = channelInfo.package_size,
          FrameInterval = channelInfo.frame_interval,
          CombinedFrames = channelInfo.combined_frames,
          LogMemoryDiagnostics = m_LogMemoryDiagnostics
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

        using (var loader = new InitialResourceLoader(channelInfo,
          info => MaterialConverter.ResolveShader(info, m_CustomShaders, m_DefaultShader, m_MaterialTemplates, m_UseSenderShader),
          meshRenderer.AddTexture))
        {
          m_InitialResourceLoader = loader;
          byte[] scratch = new byte[64 * 1024];
          for (int partIndex = 0; partIndex < channelInfo.initial_data.Count; partIndex++)
          {
            var part = channelInfo.initial_data[partIndex];
            if (!loader.NeedsPart(part)) continue;
            ConnectionStatus = $"Loading model resources {partIndex + 1}/{channelInfo.initial_data.Count}...";
            using (var request = UnityWebRequest.Get(GetAbsoluteURL(part.file)))
            {
              m_InitialResourceRequest = request;
              request.timeout = 60;
              yield return request.SendWebRequest();
              if (request.result != UnityWebRequest.Result.Success)
              {
                ConnectionStatus = "Initial resource download failed: " + part.file + ": " + request.error;
                yield break;
              }
              Exception error = null;
              try
              {
                MemoryCheckpoint("initial/part-before", part.compressedSize, part.file, meshRenderer);
                using (var input = new NativeBufferStream(request.downloadHandler.nativeData))
                  InitialDataParts.Read(part, input, scratch, loader.Consume);
                MemoryCheckpoint("initial/part-after", part.size, part.file, meshRenderer);
              }
              catch (Exception failure) { error = failure; }
              if (error != null)
              {
                ConnectionStatus = "Initial resource load failed: " + error.Message;
                Debug.LogError(ConnectionStatus);
                yield break;
              }
            }
            m_InitialResourceRequest = null;
            yield return null;
          }
          scratch = null;
          MemoryCheckpoint("initial/textures-ready", 0, "", meshRenderer);

          //Split Materials
          List<string> materialNames = channelInfo.materials;
          List<int> materialSizes = channelInfo.materialSizes;
          for(int i = 0; i < materialSizes.Count; i++)
          {
            Material material = MaterialConverter.Deserialize(loader.Materials[i],
              m_CustomShaders, m_DefaultShader, meshRenderer.TextureDictionary, m_MaterialTemplates, m_UseSenderShader);
            loader.Materials[i] = null;
            meshRenderer.AddMaterial(materialNames[i], material);
            yield return null;
          }

          MemoryCheckpoint("initial/materials-ready",0,"",meshRenderer);
          GameObject rootGameObject = new GameObject("RootGameObject");
          m_StreamRoot = rootGameObject;
          rootGameObject.transform.SetParent(transform, false);

          //Split Meshes
          List<string> meshNames = channelInfo.meshes;
          List<int> meshSizes = channelInfo.meshSizes;
          for(int i = 0; i < meshSizes.Count; i++)
          {
            string name = meshNames[i];
            List<string> refMaterials = null;
            Mesh mesh = MeshConverter.Deserialize(loader.Meshes[i],
              channelInfo.container_size, out refMaterials, meshRenderer.MaterialDictionary);
            loader.Meshes[i] = null;
            meshRenderer.AddMesh(name, mesh, refMaterials);

            List<Material> materials = new List<Material>();
            for(int j = 0; j < refMaterials.Count; j++)
            {
              if (string.IsNullOrEmpty(refMaterials[j])) { materials.Add(null); continue; }
              if (!ResourceIdentity.IsValid(refMaterials[j]) || !meshRenderer.MaterialDictionary.TryGetValue(refMaterials[j], out var material))
                throw new InvalidDataException("Mesh references a missing material ID.");
              materials.Add(material);
              yield return null;
            }

            GameObject obj = new GameObject("Mesh_" + name);
            obj.transform.SetParent(rootGameObject.transform, false);
            MeshFilter meshFilter = obj.AddComponent<MeshFilter>();
            MeshRenderer renderer = obj.AddComponent<MeshRenderer>();

            meshFilter.mesh = mesh;
            renderer.sharedMaterials = materials.ToArray();
            yield return null;
          }

        } // All resource streams and pending Texture CPU views are released.
        m_InitialResourceLoader = null;

        //CreateVertexBuffer;
        MemoryCheckpoint("buffers/cpu-before",0,"",meshRenderer);
        meshRenderer.CreateVertexBuffer();
        MemoryCheckpoint("buffers/cpu-after",0,"",meshRenderer);
        meshRenderer.DecodeBackend = m_DecodeBackend;
        meshRenderer.NormalMode = m_NormalMode;
        meshRenderer.TangentMode = m_TangentMode;
        if (m_TangentMaterialIds != null)
          foreach (string materialName in m_TangentMaterialIds)
            if (!string.IsNullOrEmpty(materialName)) meshRenderer.TangentMaterialIds.Add(materialName.TrimEnd('\0'));
        MemoryCheckpoint("buffers/gpu-before",0,"",meshRenderer);
        meshRenderer.CreateVertexContainer(channelInfo.package_size, channelInfo.container_size);
        MemoryCheckpoint("buffers/gpu-after",0,"",meshRenderer);
        meshRenderer.RootGameObject = m_StreamRoot;
        m_MeshRenderer = meshRenderer;
        m_InitializingMeshRenderer = null;
        MemoryCheckpoint("initial/ready",0,"",meshRenderer);
        ConnectionStatus = "Model ready / receiving stream";
        Debug.Log($"StreamingMesh ready: {channelInfo.meshes.Count} meshes, {meshRenderer.TextureDictionary.Count} textures.");
      }
    }

    static void ValidateResourceTables(ChannelInfo info)
    {
      if (info.meshes == null || info.meshSizes == null || info.meshes.Count != info.meshSizes.Count ||
          info.materials == null || info.materialSizes == null || info.materials.Count != info.materialSizes.Count ||
          info.textures == null || info.textureSizes == null || info.textureNames == null ||
          info.textures.Count != info.textureSizes.Count || info.textures.Count != info.textureNames.Count ||
          info.texturePayloads == null || info.texturePayloads.Count != info.textures.Count ||
          info.materials.Count > 4096 || info.textures.Count > 4096 || info.meshes.Count > 256)
        throw new InvalidDataException("Invalid initial-data resource tables.");
      var ids = new HashSet<string>(StringComparer.Ordinal);
      foreach (var id in info.materials) if (!ResourceIdentity.IsValid(id) || !ids.Add(id)) throw new InvalidDataException("Invalid/duplicate material ID.");
      ids.Clear();
      foreach (var id in info.textures) if (!ResourceIdentity.IsValid(id) || !ids.Add(id)) throw new InvalidDataException("Invalid/duplicate texture ID.");
      ids.Clear();
      foreach (var id in info.meshes) if (string.IsNullOrEmpty(id) || !ids.Add(id)) throw new InvalidDataException("Invalid/duplicate mesh key.");
      foreach (var sizes in new[] { info.materialSizes, info.meshSizes })
        foreach (int size in sizes)
          if (size <= 0 || size > InitialDataParts.MaximumPartBytes) throw new InvalidDataException("Invalid metadata size.");
      for (int i = 0; i < info.texturePayloads.Count; i++)
        if (info.texturePayloads[i] == null || info.texturePayloads[i].ByteCount() != info.textureSizes[i])
          throw new InvalidDataException("Texture payload size mismatch.");
      InitialDataParts.Validate(info.initial_data, info.materialSizes, info.meshSizes, info.textureSizes);
    }

    void SetUpdateSettings()
    {
#if !UNITY_WEBGL
      if (m_AudioSource == null) m_AudioSource = gameObject.AddComponent<AudioSource>();
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
      int generation = m_PlaybackGeneration;
      List<StreamInfo> streamPlayList = new List<StreamInfo>();
      List<AudioInfo> audioPlayList = new List<AudioInfo>();
      
      if(!string.IsNullOrEmpty(m_StreamPlayListName))
      {
        HttpWrapper wrapper = new HttpWrapper();
        wrapper.RequestPlaylistDiff<StreamInfo>(GetAbsoluteURL(m_StreamPlayListName), m_CurrentStreamPlaylistData, (list, newData) => {
          if(list != null && m_PlaybackGeneration == generation && m_MeshRenderer != null) {
            streamPlayList.AddRange(list);
            m_CurrentStreamPlaylistData = newData;
            foreach (var item in list)
              m_DurationSeconds = Math.Max(m_DurationSeconds,
                item.endTicks > 0 ? item.endTicks / (double)TimeSpan.TicksPerSecond
                  : item.startTicks / (double)TimeSpan.TicksPerSecond +
                    m_MeshRenderer.CombinedFrames * m_MeshRenderer.FrameInterval);
          }
        });
        yield return new WaitUntil(wrapper.RequestFinished);
      }

      if(!string.IsNullOrEmpty(m_AudioPlayListName) && !m_UseFmp4Audio)
      {
        HttpWrapper wrapper = new HttpWrapper();
        wrapper.RequestPlaylistDiff<AudioInfo>(GetAbsoluteURL(m_AudioPlayListName), m_CurrentAudioPlayListData, (list, newData) => {
          if(list != null && m_PlaybackGeneration == generation) {
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
          // A seek starts at a nearby chunk; older deltas are no longer needed.
          double chunkEnd = info.endTicks > 0
            ? info.endTicks / (double)TimeSpan.TicksPerSecond
            : info.startTicks / (double)TimeSpan.TicksPerSecond +
              renderer.CombinedFrames * renderer.FrameInterval;
          if (chunkEnd < m_RequestedStartTime -
              renderer.CombinedFrames * renderer.FrameInterval)
            continue;
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
