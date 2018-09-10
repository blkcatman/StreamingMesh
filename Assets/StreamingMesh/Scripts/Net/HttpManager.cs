using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Net;
using System.Collections;
using System.Threading;

using StreamingMesh.Core.Threading;

namespace StreamingMesh.Net
{
  [ExecuteInEditMode]
  public sealed class HttpManager
  {
    static HttpManager m_Instance;

    private HttpManager()
    {
    }
    
    public static HttpManager Instance
    {
      get
      {
        return (m_Instance == null ? m_Instance = new HttpManager() : m_Instance);
      }
    }

    public void Request(string url, bool isBinary, Action<byte[]> callbackData)
    {
      ThreadManager manager = ThreadManager.Instance;
      manager.PushHttpAction(() => {
        manager.StartCoroutine(_Request(url, isBinary, callbackData, null, () => {
          manager.FinishHttpAction(); // notify finishing to the manager;
        }));
      });
    }

    public void Request(string url, bool isBinary, Action<AudioClip> callbackAudio)
    {
      ThreadManager manager = ThreadManager.Instance;
      manager.PushHttpAction(() => {
        manager.StartCoroutine(_Request(url, isBinary, null, callbackAudio, () => {
          manager.FinishHttpAction(); // notify finishing to the manager;
        }));
      });
    }
    
    IEnumerator _Request(string url, bool isBinary, Action<byte[]> callbackData, Action<AudioClip> callbackAudio, Action callback) {
      UnityWebRequest request = null;
        if(callbackData != null)
        {
          request = new UnityWebRequest(url, "GET");
          request.downloadHandler = new DownloadHandlerBuffer();
          request.SetRequestHeader("Content-Type",  (isBinary ? "application/octet-stream" : "text/plain"));
        }
        else if(callbackAudio != null)
        {
          AudioType type = AudioType.UNKNOWN;
#if !UNITY_EDITOR && UNITY_IOS
          type = AudioType.AUDIOQUEUE;
#else
          type = AudioType.OGGVORBIS;
#endif
          request = UnityWebRequestMultimedia.GetAudioClip(url, type);
        }
        else
        {
#if STM_DEBUG
          Debug.LogError("No Callback!");
#endif
        }
#if STM_DEBUG_NET
        Debug.Log("STM_DEBUG_NET HttpManager::_Request() url:" + url);
#endif
        yield return request.SendWebRequest();

        if(callbackData != null)
        {
          if(request.responseCode == 200) {
            callbackData(request.downloadHandler.data);
          } else {
            callbackData(null);
          }
        }
        if (callbackAudio != null)
        {
          if(request.responseCode == 200) {
            callbackAudio(((DownloadHandlerAudioClip)request.downloadHandler).audioClip);
          } else {
            callbackAudio(null);
          }
        }

        callback();
    }


  }
}
