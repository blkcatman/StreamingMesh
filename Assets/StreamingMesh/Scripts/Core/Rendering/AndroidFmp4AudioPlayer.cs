using System;
using StreamingMesh.Core.Serialization;
using UnityEngine;

namespace StreamingMesh.Core.Rendering
{
  public sealed class AndroidFmp4AudioPlayer : IStreamingAudioPlayer
  {
#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject player;
#endif

    public bool Initialize(string channelUrl, ChannelInfo channelInfo)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
      Dispose();
      if (channelInfo == null || string.IsNullOrEmpty(channelInfo.audioPlaylist))
        return false;

      string playlistUrl = channelUrl.TrimEnd('/') + "/" + channelInfo.audioPlaylist;
      try
      {
        using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
          player = new AndroidJavaObject("com.streamingmesh.audio.StreamingMeshAndroidAudio", activity, playlistUrl);
        return player != null;
      }
      catch (Exception exception)
      {
        Debug.LogError("StreamingMesh Android audio initialization failed: " + exception);
        Dispose();
        return false;
      }
#else
      return false;
#endif
    }

    public bool TryGetTime(out double time)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
      if (player != null)
      {
        time = player.Call<double>("getTimeSeconds");
        return time >= 0.0;
      }
#endif
      time = -1.0;
      return false;
    }

    public int State
    {
      get
      {
#if UNITY_ANDROID && !UNITY_EDITOR
        return player != null ? player.Call<int>("getState") : 0;
#else
        return 0;
#endif
      }
    }

    public void Seek(double time)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
      if (player != null) player.Call("seek", Math.Max(0.0, time));
#endif
    }

    public void SetPlaying(bool playing)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
      if (player != null) player.Call("setPlaying", playing);
#endif
    }

    public void Dispose()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
      if (player != null)
      {
        player.Call("release");
        player.Dispose();
        player = null;
      }
#endif
    }
  }
}
