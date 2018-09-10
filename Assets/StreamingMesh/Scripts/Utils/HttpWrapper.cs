using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

using StreamingMesh.Core.Serialization;
using StreamingMesh.Net;

namespace StreamingMesh.Utils
{
  public sealed class HttpWrapper
  {
    public Func<bool> RequestFinished;
    bool m_RequestFinished = false;

    public HttpWrapper()
    {
      RequestFinished = (() => { return m_RequestFinished; });
    }

    public void RequestBinary(string url, Action<byte[]> callback)
    {
      m_RequestFinished = false;

      HttpManager.Instance.Request(url, true, data => {
        m_RequestFinished = true;
        callback(data);
      });
    }

    public void RequestAudio(string url, Action<AudioClip> callback)
    {
      m_RequestFinished = false;

#if !UNITY_EDITOR && UNITY_IOS
      string audioExt = ".m4a";
#else
      string audioExt = ".ogg";
#endif

      HttpManager.Instance.Request(url + audioExt, true, audio => {
        m_RequestFinished = true;
        callback(audio);
      });
    }

    public void RequestInfo<T>(string url, Action<T> callback) where T : BaseInfo
    {
      m_RequestFinished = false;

      HttpManager.Instance.Request(url, false, data => {
        T info = default(T);
        if(data != null) 
        {
          info = InfoConverter.Deserialize<T>(data);
        }
        m_RequestFinished = true;
        callback(info);
      });
    }
    
    public void RequestPlaylist<T>(string url, Action<List<T>> callback) where T : BaseInfo
    {
      m_RequestFinished = false;

      HttpManager.Instance.Request(url, false, data => {
        List<T> arrayData = null;
        if(data != null)
        {
          string stringData = Encoding.UTF8.GetString(data);
          string[] splitData = stringData.Split('\n');

          arrayData = new List<T>();
          for(int i = 0; i < splitData.Length; i++)
          {
            if(!string.IsNullOrEmpty(splitData[i]))
            {
              T info = InfoConverter.DeserializeFromString<T>(splitData[i]);
              if(info != null)
              {
                arrayData.Add(info);
              }
            }
          }
        }
        m_RequestFinished = true;
        callback(arrayData);
      });
    }

    //TODO: create RequestPlaylistDiff
    public void RequestPlaylistDiff<T>(string url, string currentData, Action<List<T>, string> callback) where T : BaseInfo
    {
      m_RequestFinished = false;

      HttpManager.Instance.Request(url, false, data => {
        List<T> arrayData = null;
        string newData = null;
        if(data != null)
        {
          newData = Encoding.UTF8.GetString(data);
          if(newData.Length <= currentData.Length) {
            callback(null, null);
          }
          string stringData = newData.Substring(currentData.Length);
          string[] splitData = stringData.Split('\n');

          arrayData = new List<T>();
          for(int i = 0; i < splitData.Length; i++)
          {
            if(!string.IsNullOrEmpty(splitData[i]))
            {
              T info = InfoConverter.DeserializeFromString<T>(splitData[i]);
              if(info != null)
              {
                arrayData.Add(info);
              }
            }
          }
        }
        m_RequestFinished = true;
        callback(arrayData, newData);
      });
    }

  }
}