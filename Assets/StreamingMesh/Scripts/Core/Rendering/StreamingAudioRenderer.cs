using UnityEngine;
using System.Collections.Generic;
using System;

namespace StreamingMesh.Core.Rendering
{
  public class StreamingAudioRenderer
  {
    List<int> m_AudioSampleIndecies = new List<int>();
    List<float> m_AudioSampleData = new List<float>();

    bool m_IsPlayable = false;

    float m_FrameInterval = 0.1f;
    int m_CombinedFrames = 100;

    int m_LastAddedAudioIndex = 0;

    public bool IsPlayable
    {
      get
      {
        return m_IsPlayable;
      }
    }

    public float FrameInterval
    {
      get
      {
        return m_FrameInterval;
      }
      set
      {
        m_FrameInterval = value;
      }
    }

    public int CombinedFrames
    {
      get
      {
        return m_CombinedFrames;
      }
      set
      {
        m_CombinedFrames = value;
      }
    }

    public void SetAudio(ref float[] data, int audioOffset)
    {
      if(m_AudioSampleData.Count == 0 || data.Length == 0)
        return;

      for (int i = 0; i < data.Length; i++) {
        if(m_AudioSampleData.Count <= audioOffset + i) {
          data[i] = 0;
        } else {
          data[i] = m_AudioSampleData[audioOffset + i];
        }
      }
    }

    public void AddAudioData(string name, AudioClip audio)
    {
      if(audio == null)
      {
        Debug.LogError("AudioClip is null, abort StreamingAudioRenderer::AddAudioData");
        return;
      }

      int index;
      if(int.TryParse(name, out index))
      {
        if(!m_AudioSampleIndecies.Contains(index))
        {
          int subIndex = index - m_LastAddedAudioIndex;
          int audioBufferSize = (int)(audio.frequency * audio.channels * (m_CombinedFrames * m_FrameInterval));

          // if AudioRenderer gets backward
          if(subIndex < 0)
          {
            // TODO: add subroutine
            InsertAudioData(index, audio);
            return;
          }
          if(subIndex > 1)
          {
            float[] zeroSample = new float[audioBufferSize * (subIndex - 1)];
            m_AudioSampleData.AddRange(zeroSample);
          }

          float[] sample = new float[audio.samples * audio.channels];
          audio.GetData(sample, 0);
          List<float> destSample = new List<float>(sample);
          m_AudioSampleData.AddRange(destSample.GetRange(0, audioBufferSize));

          m_AudioSampleIndecies.Add(index);
          m_IsPlayable = true;
        }
      }
    }

    void InsertAudioData(int index, AudioClip audio)
    {
      // TODO: add subroutine
    }


  }
}