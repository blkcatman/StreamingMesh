using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace StreamingMesh.Core.Rendering
{
  public sealed class WebFmp4AudioPlayer : IDisposable
  {
    int handle;

    public bool IsInitialized { get { return handle > 0; } }

    public bool Initialize(
      string channelUrl,
      string initFile,
      string playlistFile,
      string mimeType,
      string codec)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
      if (handle > 0)
        return true;
      string baseUrl = channelUrl.EndsWith("/") ? channelUrl : channelUrl + "/";
      string type = mimeType + "; codecs=\"" + codec + "\"";
      handle = STM_Fmp4_Create(baseUrl, initFile, playlistFile, type);
      return handle > 0;
#else
      return false;
#endif
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

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    static extern int STM_Fmp4_Create(
      string channelUrl,
      string initFile,
      string playlistFile,
      string mimeType);

    [DllImport("__Internal")]
    static extern double STM_Fmp4_GetTime(int playerHandle);

    [DllImport("__Internal")]
    static extern int STM_Fmp4_GetState(int playerHandle);

    [DllImport("__Internal")]
    static extern void STM_Fmp4_Seek(int playerHandle, double time);

    [DllImport("__Internal")]
    static extern void STM_Fmp4_Destroy(int playerHandle);
#endif
  }
}
