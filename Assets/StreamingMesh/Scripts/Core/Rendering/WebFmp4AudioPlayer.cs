using System;
using System.Runtime.InteropServices;
using StreamingMesh.Core.Serialization;
using UnityEngine;

namespace StreamingMesh.Core.Rendering
{
  public sealed class WebFmp4AudioPlayer : IStreamingAudioPlayer
  {
    int handle;

    public bool IsInitialized { get { return handle > 0; } }

    public void ConfigureBuffering(int aheadChunks, double backSeconds)
    {
      ReceiverPrefetchWindow.ValidateChunks(aheadChunks);
      ReceiverPrefetchWindow.Validate(backSeconds, true);
#if UNITY_WEBGL && !UNITY_EDITOR
      if (handle > 0) STM_Fmp4_ConfigureBuffering(handle, aheadChunks, backSeconds);
#endif
    }

    public bool Initialize(
      string channelUrl,
      string initFile,
      string playlistFile,
      string mimeType,
      string codec,
      long timebaseHz = TimeSpan.TicksPerSecond)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
      if (handle > 0)
        return true;
      string baseUrl = channelUrl.EndsWith("/") ? channelUrl : channelUrl + "/";
      string type = mimeType + "; codecs=\"" + codec + "\"";
      handle = STM_Fmp4_Create(baseUrl, initFile, playlistFile, type, timebaseHz);
      return handle > 0;
#else
      return false;
#endif
    }

    public bool Initialize(string channelUrl, ChannelInfo channelInfo)
    {
      return Initialize(
        channelUrl,
        channelInfo.audioInit,
        channelInfo.audioInfo,
        string.IsNullOrEmpty(channelInfo.audioMimeType) ? "audio/mp4" : channelInfo.audioMimeType,
        string.IsNullOrEmpty(channelInfo.audioCodec) ? "mp4a.40.2" : channelInfo.audioCodec,
        channelInfo.timebaseHz);
    }

    public bool TryGetTime(out double time)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
      if (handle > 0)
      {
        time = STM_Fmp4_GetTime(handle);
        return time >= 0.0;
      }
#endif
      time = 0.0;
      return false;
    }

    public int State
    {
      get
      {
#if UNITY_WEBGL && !UNITY_EDITOR
        return handle > 0 ? STM_Fmp4_GetState(handle) : 0;
#else
        return 0;
#endif
      }
    }

    public void Seek(double time)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
      if (handle > 0)
        STM_Fmp4_Seek(handle, time);
#endif
    }

    public void Dispose()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
      if (handle > 0)
        STM_Fmp4_Destroy(handle);
#endif
      handle = 0;
    }

    public void SetPlaying(bool playing)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
      if (handle > 0)
        STM_Fmp4_SetPlaying(handle, playing ? 1 : 0);
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    static extern void STM_Fmp4_ConfigureBuffering(int playerHandle, int aheadChunks, double backSeconds);

    [DllImport("__Internal")]
    static extern int STM_Fmp4_Create(
      string channelUrl,
      string initFile,
      string playlistFile,
      string mimeType, double timebaseHz);

    [DllImport("__Internal")]
    static extern double STM_Fmp4_GetTime(int playerHandle);

    [DllImport("__Internal")]
    static extern int STM_Fmp4_GetState(int playerHandle);

    [DllImport("__Internal")]
    static extern void STM_Fmp4_Seek(int playerHandle, double time);

    [DllImport("__Internal")]
    static extern void STM_Fmp4_SetPlaying(int playerHandle, int playing);

    [DllImport("__Internal")]
    static extern void STM_Fmp4_Destroy(int playerHandle);
#endif
  }
}
