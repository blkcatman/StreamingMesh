using UnityEngine;
using System.Collections.Generic;
using System;
using System.Text;
using System.Linq;

namespace StreamingMesh.Core.Rendering
{
  public class StreamingMeshRenderer
  {
    Dictionary<string, Texture2D> m_TextureDictionary = new Dictionary<string, Texture2D>();
    Dictionary<string, Material> m_MaterialDictionary = new Dictionary<string, Material>();
    Dictionary<string, Mesh> m_MeshDictionary = new Dictionary<string, Mesh>();
    List<Mesh> m_MeshList = new List<Mesh>();
    float[][] m_VertexBuffer = null;
    float[][] m_VertexBuffer_old = null;
    float m_PosX, m_PosY, m_PosZ, m_OldX, m_OldY, m_OldZ;

    GameObject m_RootGameObject = null;

    bool m_IsPlayable = false;

    int m_ContainerSize = 4;
    int m_PackageSize = 128;
    float m_FrameInterval = 0.1f;
    int m_CombinedFrames = 100;

    List<int> m_KeyedVertexIndecies = new List<int>();
    List<KeyValuePair<double, List<KeyValuePair<double, byte[]>>>> m_KeyedVertexDatas = new  List<KeyValuePair<double, List<KeyValuePair<double, byte[]>>>>();
    VertexContainer m_VertexContainer = null;

    int m_LastAddedVertexIndex = 0;

    double m_CurrentFrameTime = 0.0f;
    int m_CurrentKeyedIndex = 0;

    bool m_IsLastFrameReached = false;

    public bool IsPlayable
    {
      get { return m_IsPlayable; }
    }

    public int ContainerSize
    {
      get { return m_ContainerSize; }
      set { m_ContainerSize = value; }
    }

    public int PackageSize
    {
      get { return m_PackageSize; }
      set { m_PackageSize = value; }
    }

    public float FrameInterval
    {
      get { return m_FrameInterval; }
      set { m_FrameInterval = value; }
    }

    public int CombinedFrames
    {
      get { return m_CombinedFrames; }
      set { m_CombinedFrames = value; }
    }

    public void AddTexture(string name, Texture2D texture)
    {
      if(!m_TextureDictionary.ContainsKey(name))
        m_TextureDictionary.Add(name, texture);
    }

    public void AddMaterial(string name, Material material)
    {
      if(!m_MaterialDictionary.ContainsKey(name))
        m_MaterialDictionary.Add(name, material);
    }

    public void AddMesh(string name, Mesh mesh)
    {
      if(!m_MeshDictionary.ContainsKey(name))
      {
        m_MeshDictionary.Add(name, mesh);
        m_MeshList.Add(mesh);
      }
    }

    public Dictionary<string, Texture2D> TextureDictionary
    {
      get { return m_TextureDictionary; }
    }

    public Dictionary<string, Material> MaterialDictionary
    {
      get { return m_MaterialDictionary; }
    }

    public GameObject RootGameObject
    {
      set { m_RootGameObject = value; }
    }

    public void CreateVertexContainer(int packageSize, int containerSize)
    {
      m_VertexContainer = new VertexContainer(packageSize, containerSize);
    }

    public void CreateVertexBuffer()
    {
      if(m_MeshList.Count > 0)
      {
        m_VertexBuffer = new float[m_MeshList.Count][];
        m_VertexBuffer_old = new float[m_MeshList.Count][];
        for(int i = 0; i < m_MeshList.Count; i++)
        {
          m_VertexBuffer[i] = new float[m_MeshList[i].vertexCount * 3];
          m_VertexBuffer_old[i] = new float[m_MeshList[i].vertexCount * 3];
        }
      }
    }

    public void AddVertexData(string name, byte[] data, long ticks)
    {
      if(data == null)
      {
        Debug.LogError("data is null, abort StreamingMeshRenderer::AddVertexData");
      }

      int index;
      if(int.TryParse(name, out index))
      {
        if(!m_KeyedVertexIndecies.Contains(index))
        {
          int subIndex = index - m_LastAddedVertexIndex;

          // if AudioRenderer gets backward
          if(subIndex < 0)
          {
            // TODO: add subroutine
            InsertVertexData(index, data);
            return;
          }
          if(subIndex > 1)
          {
            // TODO: add module
            //
          }

          List<byte> rawBuffer = new List<byte>(StreamingMesh.Lib.ExternalTools.Decompress(data));
          //Acquire number of contained frames and byte sizes
          //sizeBuffer = [frameCnt(int), 0frame(int), 1frame(int), ... , nframe(int)] = (frameCnt + 1) * sizeof(int)
          int frameCnt = BitConverter.ToInt32(rawBuffer.GetRange(0, sizeof(int)).ToArray(), 0);
          byte[] sizeBuffer = rawBuffer.GetRange(0, (frameCnt + 1) * sizeof(int)).ToArray();
          rawBuffer.RemoveRange(0, sizeBuffer.Length);
          //Split frame data from combined data
          List<KeyValuePair<double, byte[]>> keyedList = null;
          double headTime = (double)ticks / (10000000.0);
          for (int i = 1; i < frameCnt + 1; i++)
          {
            int size = BitConverter.ToInt32(sizeBuffer, i * sizeof(int));
            byte[] buf = rawBuffer.GetRange(0, size).ToArray();
            double time = headTime + (i-1) * m_FrameInterval;
            if(buf[0] == 0x0F)
            {
              keyedList = new List<KeyValuePair<double, byte[]>>();
              keyedList.Add(new KeyValuePair<double, byte[]>(time, buf));
              m_KeyedVertexDatas.Add(new KeyValuePair<double, List<KeyValuePair<double, byte[]>>>(time, keyedList));
            }
            else
            {
              if(keyedList != null)
              {
                keyedList.Add(new KeyValuePair<double, byte[]>(time, buf));
              }
              else
              {
                Debug.LogError("wrong buffer, abort StreamingMeshRenderer::AddVertexData");
              }
            }
            rawBuffer.RemoveRange(0, size);
          }
          m_KeyedVertexIndecies.Add(index);
        }
      }
    }

    void InsertVertexData(int index, byte[] data)
    {
      // TODO: add subroutine
    }

    public void UpdateWithTime(double updateTime)
    {
      if(m_IsLastFrameReached)
        return;

      if(updateTime - m_CurrentFrameTime < (double)m_FrameInterval)
      {
        UpdateVertexInterpolate((float)(updateTime - m_CurrentFrameTime));
        return;
      }

      //Find index num based on updateTime
      int currentKeyedIndex = -1 + m_KeyedVertexDatas.FindIndex(m_CurrentKeyedIndex, it => {
        return it.Key > updateTime;
      });
      if(currentKeyedIndex < 0)
      {
        m_IsPlayable = false;
        return;
      }
      m_IsPlayable = true;

      double headTime = m_KeyedVertexDatas[currentKeyedIndex].Key;
      List<KeyValuePair<double, byte[]>> vertexDatas = m_KeyedVertexDatas[currentKeyedIndex].Value;
      int headIndex = vertexDatas.FindIndex( it => {
        return it.Key < updateTime && updateTime < it.Key + m_FrameInterval;
      });
      if(headIndex < 0) {
        return;
      }
      m_CurrentFrameTime = vertexDatas[headIndex].Key;

      KeyValuePair<double, byte[]> pairData;
      if(headIndex < vertexDatas.Count - 1)
        pairData = vertexDatas[headIndex + 1];
      else
      {
        if(currentKeyedIndex + 1 < m_KeyedVertexDatas.Count)
          pairData = m_KeyedVertexDatas[currentKeyedIndex + 1].Value[0];
        else {
          pairData = m_KeyedVertexDatas[currentKeyedIndex].Value.Last();
          //m_IsLastFrameReached = true;
        }
      }
      UpdateVertexBuffer(pairData.Value);
    }

    void UpdateVertexInterpolate(float updateLocalTime)
    {
      float scaledTime = updateLocalTime * m_FrameInterval;
      for( int i = 0; i < m_VertexBuffer.Length; i++)
      {
        int size = m_VertexBuffer[i].Length / 3;
        float[] b = m_VertexBuffer[i];
        float[] o = m_VertexBuffer_old[i];

        Vector3[] tempBuf = new Vector3[size];
        for (int j = 0; j < size; j++)
        {
          float ox = o[j * 3 + 0];
          float oy = o[j * 3 + 1];
          float oz = o[j * 3 + 2];

          tempBuf[j].x = ox + (b[j * 3] - ox) * scaledTime;
          tempBuf[j].y = oy + (b[j * 3 + 1] - oy) * scaledTime;
          tempBuf[j].z = oz + (b[j * 3 + 2] - oz) * scaledTime;
        }
        m_MeshList[i].vertices = tempBuf;
        tempBuf = null;
        m_MeshList[i].RecalculateNormals();
      }

      float x = (m_PosX - m_OldX) * scaledTime;
      float y = (m_PosY - m_OldY) * scaledTime;
      float z = (m_PosZ - m_OldZ) * scaledTime;
      m_RootGameObject.transform.localPosition = new Vector3(m_OldX + x, m_OldY + y, m_OldZ + z);
    }

    void UpdateVertexBuffer(byte[] data)
    {
      m_OldX = m_PosX;
      m_OldY = m_PosY;
      m_OldZ = m_PosZ;

      for(int i = 0; i < m_VertexBuffer.Length; i++)
        Buffer.BlockCopy(m_VertexBuffer[i], 0, m_VertexBuffer_old[i], 0, m_VertexBuffer[i].Length * sizeof(float));

      int keyFrame;
      int result = m_VertexContainer.Decode(
        data, ref m_VertexBuffer,
        out m_PosX, out m_PosY, out m_PosZ,
        out keyFrame
      );
    }


  }
}