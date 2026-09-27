using System;
using System.Runtime.InteropServices;
using StreamingMesh.Core.Serialization;
using UnityEngine;

namespace StreamingMesh.Core.Rendering
{
  public sealed class AppleFmp4AudioPlayer : IStreamingAudioPlayer
  {
    int handle;
    string playlistUrl;
    float nextRetryTime;

    public bool Initialize(string channelUrl, ChannelInfo channelInfo)
    {
#if UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
      Dispose();
      if (channelInfo == null || string.IsNullOrEmpty(channelInfo.audio_playlist))
        return false;

      string separator = channelUrl.EndsWith("/", StringComparison.Ordinal) ? "" : "/";
      playlistUrl = channelUrl + separator + channelInfo.audio_playlist;
      nextRetryTime = 0.0f;
      handle = STM_AppleAudio_Create(playlistUrl);
      if (handle <= 0)
        Debug.LogError("StreamingMesh could not create the AVFoundation audio player.");
      return handle > 0;
#else
      return false;
#endif
    }

    public bool TryGetTime(out double time)
    {
#if UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
      RetryFailedPlayer();
      time = handle > 0 ? STM_AppleAudio_GetTime(handle) : -1.0;
      return time >= 0.0;
#else
      time = -1.0;
      return false;
#endif
    }

    public int State
    {
      get
      {
#if UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
        RetryFailedPlayer();
        return handle > 0 ? STM_AppleAudio_GetState(handle) : 0;
#else
        return 0;
#endif
      }
    }

    public void Seek(double time)
    {
#if UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
      if (handle > 0)
        STM_AppleAudio_Seek(handle, Math.Max(0.0, time));
#endif
    }

    public void SetPlaying(bool playing)
    {
#if UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
      if (handle > 0)
        STM_AppleAudio_SetPlaying(handle, playing ? 1 : 0);
#endif
    }

    public void Dispose()
    {
#if UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
      if (handle > 0)
      {
        STM_AppleAudio_Destroy(handle);
        handle = 0;
      }
      playlistUrl = null;
#endif
    }

#if UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
    void RetryFailedPlayer()
    {
      if (handle > 0 && STM_AppleAudio_GetState(handle) < 0)
      {
        STM_AppleAudio_Destroy(handle);
        handle = 0;
        nextRetryTime = Time.realtimeSinceStartup + 1.0f;
      }
      if (handle == 0 && !string.IsNullOrEmpty(playlistUrl) && Time.realtimeSinceStartup >= nextRetryTime)
      {
        handle = STM_AppleAudio_Create(playlistUrl);
        nextRetryTime = Time.realtimeSinceStartup + 1.0f;
      }
    }
#endif

#if UNITY_IOS && !UNITY_EDITOR
    const string PluginName = "__Internal";
#else
    const string PluginName = "StreamingMeshAppleAudio";
#endif

#if UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
    [DllImport(PluginName)]
    static extern int STM_AppleAudio_Create(string playlistUrl);

    [DllImport(PluginName)]
    static extern double STM_AppleAudio_GetTime(int playerHandle);

    [DllImport(PluginName)]
    static extern int STM_AppleAudio_GetState(int playerHandle);

    [DllImport(PluginName)]
    static extern void STM_AppleAudio_Seek(int playerHandle, double time);

    [DllImport(PluginName)]
    static extern void STM_AppleAudio_SetPlaying(int playerHandle, int playing);

    [DllImport(PluginName)]
    static extern void STM_AppleAudio_Destroy(int playerHandle);
#endif
  }
}
