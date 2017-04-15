using UnityEngine;
using System.Collections.Generic;
using System;
using System.IO;
using System.Text;

namespace StreamingMesh
{
  using StreamingMesh.Core;

  [System.Serializable]
  public class ShaderPair : Serialize.KeyAndValue<string, Shader>
  {
    public ShaderPair(string key, Shader value) : base(key, value) {
    }
  }

  [System.Serializable]
  public class ShaderTable : Serialize.TableBase<string, Shader, ShaderPair> {}

  [RequireComponent(typeof(STMHttpSerializer))]
  public class STMHttpMeshReceiver : MonoBehaviour
  {
    public string m_streamFile = "stream.json";
    public int m_interpolateFrames = 5;

    STMHttpSerializer m_serializer;

    public readonly Queue<Action> executeOnUpdate = new Queue<Action>();
    bool waitProcess = false;

    int m_currentAudioDataCnt = 0;

    // buffer size and name of mesh, material, texture

    List<int> m_meshSizes = new List<int>();
    List<int> m_materialSizes = new List<int>();
    List<int> m_textureSizes = new List<int>();
    List<string> m_meshNames = new List<string>();
    List<string> m_materialNames = new List<string>();
    List<string> m_textureNames = new List<string>();

    // stored material and texture data

    List<KeyValuePair<string, Material>> m_materialList = new List<KeyValuePair<string, Material>>();
    List<KeyValuePair<string, Texture2D>> m_textureList = new List<KeyValuePair<string, Texture2D>>();

    //

    string m_combinedDataURL = null;
    string m_streamInfoURL = null;

#if UNITY_WEBGL
    string m_audioURL = null;
#else
    string m_audioInfoURL = null;
#endif

    int m_areaRange = 4;
    float m_frameInterval = 0.1f;
    //int m_subframesPerKeyframe = 4;
    int m_combinedFrames = 100;

    float m_streamRefreshInterval = 10.0f;

    public bool IsPlayable {
      get; private set;
    }
    public bool IsPlayEnd {
      get; private set;
    }
    float m_playerCurrentTime = 0f;
    float m_playerEndTime = 1.0f;

    float m_currentStreamWait = 0.0f;
    float m_currentInterpolateWait = 0.0f;

    List<long> m_combineddBufferStartTicks = new List<long>(); // buffer end ticks
    List<long> m_combinedBufferEndTicks = new List<long>(); // buffer end ticks

    int m_currentCombinedBufferIndex = 0;

    List<int> m_streamBufferList = new List<int>();
    List<KeyValuePair<double, byte[]>> m_bufferedStream = new List<KeyValuePair<double, byte[]>>();

    #pragma warning disable 0414
    AudioSource m_audioSource;
    #pragma warning restore 0414

    List<int> m_audioBufferList = new List<int>();
    List<float> m_audioSampleData = new List<float>();
    int m_audioOffset = 0;
    int m_audioSampleRate = 44100;
    int m_currentBufferIndex = 0;

    long m_currentLocalTicks = 0;

    #pragma warning disable 0414
    bool remainPlayableStream = false; // streamRemain
    #pragma warning restore 0414

    bool getErrorData = false;

    //temporary buffers
    List<int[]> indicesBuf = new List<int[]>();
    int currentMesh = 0;
    float[][] vertsBuf, vertsBuf_old;
    float pos_x, pos_y, pos_z, old_x, old_y, old_z;　　
　　VertexPack vPack;

    //gameobjects and meshes
    List<GameObject> meshObjects = new List<GameObject>();
    GameObject localRoot = null;
    List<Mesh> meshBuf = new List<Mesh>();

    bool isRequestComplete = false;

    float timeWeight = 0.0f;

    //Shaders
    public Shader defaultShader;
    public ShaderTable customShaders;

    public bool autoPlayOnAwake = true;
    public bool autoRepeat = true;

    void Reset()
    {
      if (localRoot != null)
      {
        DestroyImmediate(localRoot);
      }

      foreach (GameObject obj in meshObjects)
      {
        DestroyImmediate(obj);
      }

      m_streamBufferList.Clear();
      m_combineddBufferStartTicks.Clear();
      m_combinedBufferEndTicks.Clear();
      m_bufferedStream.Clear();

      meshBuf.Clear();
      m_materialList.Clear();
      m_textureList.Clear();
      m_combinedDataURL = null;
      m_streamInfoURL = null;

#if UNITY_WEBGL
      string m_audioURL = null;
#else
      m_audioBufferList.Clear();
      m_audioInfoURL = null;
#endif

      indicesBuf.Clear();
      vertsBuf = null;
      vertsBuf_old = null;

      //isConnected = false;
      isRequestComplete = false;

      IsPlayable = false;
      IsPlayEnd = false;

      m_currentStreamWait = m_streamRefreshInterval - 1.0f;
    }

    public void LoadAndPlay() {
      if(m_serializer == null) {
        Reset();
        InitializeReceiver();
      }

      m_serializer.Request(m_streamFile);
      m_playerCurrentTime = 0f;

      //Debug.Log("Audio Sample Rate:" + AudioSettings.outputSampleRate);

      IsPlayable = false;
      IsPlayEnd = false;  
    }

    // Use this for initialization
    void Start()
    {
      Reset();
      InitializeReceiver();
#if UNITY_WEBGL
      m_audioSource = gameObject.AddComponent<WebGLStreamingAudioSource>();
#else
      m_audioSource = gameObject.AddComponent<AudioSource>();
      m_audioSampleRate = AudioSettings.outputSampleRate;
#endif
      if(autoPlayOnAwake) {
        LoadAndPlay();
      }

    }

    // Update is called once per frame
    void Update()
    {
      if(Application.isPlaying) {
        if(executeOnUpdate.Count > 0 && !waitProcess) {
          executeOnUpdate.Dequeue().Invoke();
          waitProcess = true;
        }
      }

      if (!isRequestComplete)
        return;

      float delta = Time.deltaTime;

      //update stream
      m_currentStreamWait += delta;
      if (m_currentStreamWait > m_streamRefreshInterval)
      {
        if (m_streamInfoURL != null)
          m_serializer.Request(m_streamInfoURL);
#if UNITY_WEBGL
        if(m_audioURL != null && !m_audioSource.IsPlaying) {
#if !UNITY_EDITOR
          m_audioSource.clipURL = m_audioURL;
          m_audioSource.Play();
#endif
        }
#else
        if (m_audioInfoURL != null)
          m_serializer.Request(m_audioInfoURL);
#endif
        m_currentStreamWait -= m_streamRefreshInterval;
      }

      if(m_bufferedStream.Count == 0)
        return;

#if UNITY_WEBGL
      if(m_audioURL != null) {
        if(m_audioSource.clipURL == null)
          return;
        if(m_audioSource.IsPlaying) {
          m_playerCurrentTime = m_audioSource.CurrentTime;
        } else {
          m_playerCurrentTime += delta;
          if(m_audioSource.Duration > 0.1f && m_audioSource.CurrentTime > m_audioSource.Duration) {
            IsPlayEnd = true;
            if(autoRepeat == true) {
              SeekToZero();
            }
          }
        }
      } else {
        m_playerCurrentTime += delta;
      }
#else
      if(m_audioInfoURL != null) {
        if(m_audioSampleData.Count == 0)
          return;
        m_playerCurrentTime = (float)m_audioOffset / (float)(m_audioSampleRate * 2);
        if(IsPlayEnd && autoRepeat) {
          SeekToZero();
        }
      } else {
        m_playerCurrentTime += delta;
      }
#endif

      IsPlayable = true;

      //update mesh
      int currentCombinedBufferIndex = 0;
      float currentFrameInterval = 0.1f;
      float playingFromStartTime = 0.0f;
      float playingToEndTime = 10.0f;
      int currentBufferIndex = 0;
      for(int i = m_currentCombinedBufferIndex; i < m_combinedBufferEndTicks.Count; i++) {
        playingToEndTime = (m_combinedBufferEndTicks[i] / 10000) / 1000f;
        if(playingToEndTime > m_playerCurrentTime) {
          currentCombinedBufferIndex = i;
          float currentBufferInterval = 0.1f;
          if(i > 0) {
            playingFromStartTime = (m_combinedBufferEndTicks[i-1] / 10000) / 1000f;
            currentBufferInterval =
              ((m_combinedBufferEndTicks[i] - m_combinedBufferEndTicks[i-1]) / 10000) / 1000f;
          } else {
            currentBufferInterval = playingToEndTime;
          }
          currentFrameInterval = currentBufferInterval / m_combinedFrames;
          break;
        }
      }

      if(currentCombinedBufferIndex != m_currentCombinedBufferIndex) {
        m_currentCombinedBufferIndex = currentCombinedBufferIndex;
      }

      int offsetBufferFrames = m_combinedFrames * currentCombinedBufferIndex;
      for(int i = 0; i <= m_combinedFrames + offsetBufferFrames; i++){
        if((playingFromStartTime + i * currentFrameInterval) > m_playerCurrentTime) {
          currentBufferIndex = offsetBufferFrames + i;
          break;
        }
      }

      if(currentBufferIndex != m_currentBufferIndex && currentBufferIndex > m_combinedBufferEndTicks.Count) {
        VerticesReceived(m_bufferedStream[currentBufferIndex - 1].Value);
        m_currentBufferIndex = currentBufferIndex;
        timeWeight = 0f;

      }
      //Debug.Log(currentCombinedBufferIndex+","+m_currentBufferIndex);
      m_currentInterpolateWait += delta;
      if (m_currentInterpolateWait > currentFrameInterval / m_interpolateFrames)
      {
        m_currentInterpolateWait -= currentFrameInterval / m_interpolateFrames;
        UpdateVertsInterpolate();
      }
    }

    int GetCurrentBufferIndex(float currentTime) {
      int currentCombinedBufferIndex = 0;
      float currentFrameInterval = 0.1f;
      float playingFromStartTime = 0.0f;
      float playingToEndTime = 10.0f;
      for(int i = 0; i < m_combinedBufferEndTicks.Count; i++) {
        playingToEndTime = (m_combinedBufferEndTicks[i] / 10000) / 1000f;
        if(playingToEndTime > currentTime) {
          currentCombinedBufferIndex = i;
          float currentBufferInterval = 0.1f;
          if(i > 0) {
            playingFromStartTime = (m_combinedBufferEndTicks[i-1] / 10000) / 1000f;
            currentBufferInterval =
              ((m_combinedBufferEndTicks[i] - m_combinedBufferEndTicks[i-1]) / 10000) / 1000f;
          } else {
            currentBufferInterval = playingToEndTime;
          }
          currentFrameInterval = currentBufferInterval / m_combinedFrames;
          break;
        }
      }
      int offsetBufferFrames = m_combinedFrames * currentCombinedBufferIndex;
      for(int i = 0; i <= m_combinedFrames + offsetBufferFrames; i++){
        if((playingFromStartTime + i * currentFrameInterval) > currentTime) {
          int currentBufferIndex = offsetBufferFrames + i;
          return currentBufferIndex;
        }
      }
      return -1;
    }

#if !UNITY_WEBGL
    void OnAudioFilterRead(float[] data, int channels) {
      if(m_audioInfoURL == null)
        return;

      if(m_audioSampleData.Count == 0 || data.Length == 0) {
        return;
      }

      if(m_audioSampleData.Count <= m_audioOffset) {
        IsPlayEnd = true;
      }

      if(!IsPlayable) {
        return;
      }

      long oldTicks = m_currentLocalTicks;
      if(IsPlayEnd == false) {
        m_currentLocalTicks = DateTime.Now.Ticks;
      }

      for (int i = 0; i < data.Length; i++) {
        if(m_audioSampleData.Count <= m_audioOffset + i) {
          data[i] = 0;
        } else {
          data[i] = m_audioSampleData[m_audioOffset + i];
        }
      }

      m_audioOffset += data.Length;
    }
#endif

    public void SeekScaledTime(float scaledTime) {
      float seekTime = scaledTime * m_playerEndTime;
      int bufferedTime = (int)(scaledTime * m_bufferedStream.Count);
      if(Mathf.Abs((bufferedTime - m_currentBufferIndex)*m_frameInterval) <= 1.0f) {
        return;
      }
      SeekTo(seekTime);
    }

    public void SeekToZero()
    {
      SeekTo(0);
    }

    public void SeekTo(float time) {
      m_playerCurrentTime = time;
#if UNITY_WEBGL
      if(m_audioSource.clipURL != null) {
        m_audioSource.CurrentTime = time;
        if(time == 0f) {
#if !UNITY_EDITOR
          m_audioSource.Play();
#endif
        }
      }
#else
      if( m_audioInfoURL != null) {
        int seekAudioValue = (int)(m_audioSampleRate * 2 * time);
        if(m_audioSampleData.Count > seekAudioValue) {
          m_audioOffset = seekAudioValue;
        }
      }
#endif
      int currentBufferIndex = GetCurrentBufferIndex(time);
      if(currentBufferIndex > -1) {
        for(int i = 0; i < m_interpolateFrames; i++) {
          if(VerticesReceived(m_bufferedStream[currentBufferIndex - 1 - i].Value)) {
            for(int j = i; j == 0; j--) {
              VerticesReceived(m_bufferedStream[currentBufferIndex - 1 - j].Value);
            }
            timeWeight = 1.0f;
            UpdateVertsInterpolate();
            break;
          }
        }

        m_currentBufferIndex = currentBufferIndex;
      }
      m_currentCombinedBufferIndex = 0;
      IsPlayEnd = false;
    }

    void Play() {

    }

    void Pause() {

    }

    void InitializeReceiver()
    {
      m_serializer = gameObject.GetComponent<STMHttpSerializer>();

      m_serializer.OnChannelInfoReceived = OnChannelInfoReceived;
      //m_serializer.OnMeshInfoReceived = OnMeshInfoReceived;
      //m_serializer.OnMaterialInfoReceived = OnMaterialInfoReceived;
      //m_serializer.OnTextureReceived = OnTextureReceived;
      m_serializer.OnCombinedDataReceived = OnCombinedDataReceived;

      m_serializer.OnStreamListReceived = OnStreamListReceived;
      m_serializer.OnStreamReceived = OnStreamDataReceived;
#if !UNITY_WEBGL
      m_serializer.OnAudioListReceived = OnAudioListReceived;
#endif
      m_serializer.OnAudioReceived = OnAudioDataReceived;

    }

    void TryRequestCombinedData() {
      if(m_combinedDataURL != null) {
        m_serializer.Request(m_combinedDataURL);
      } else {
        Debug.LogWarning("CombinedDataURL is null, abort to request");
      }
    }

    void OnChannelInfoReceived(string name, ChannelInfo info)
    {
      m_areaRange = info.area_range;
      //m_packageSize = info.package_size;
      m_frameInterval = info.frame_interval;
      m_combinedFrames = info.combined_frames;
      m_streamInfoURL = info.stream_info;

      vPack = new VertexPack(info.package_size, info.area_range);

#if UNITY_WEBGL
      m_audioURL = info.audio_clip;
#else
      m_audioInfoURL = info.audio_info;
#endif
      vertsBuf = new float[info.meshes.Count][];
      vertsBuf_old = new float[info.meshes.Count][];

      m_meshSizes = info.meshSizes;
      m_materialSizes = info.materialSizes;
      m_textureSizes = info.textureSizes;
      m_meshNames = info.meshes;
      m_materialNames = info.materials;
      m_textureNames = info.textures;

      m_combinedDataURL = info.data;
      Invoke("TryRequestCombinedData",0.1f);

      m_streamRefreshInterval = 1.0f;
    }

    void OnCombinedDataReceived(string name, byte[] data) {
      if(data.Length == 0) {
        Debug.LogWarning("CombinedData is null, abort to process");
        Invoke("TryRequestCombinedData", 1.0f);
        return;
      }
      byte[] rawData = StreamingMesh.ExternalTools.Decompress(data);

      int offsetBytes = 0;
      for(int i = 0; i < m_textureSizes.Count; i++) {
        int size = m_textureSizes[i];
        byte[] buffer = new byte[size];
        Buffer.BlockCopy(rawData, offsetBytes, buffer, 0, size);
        Texture2D texture = new Texture2D(2, 2);
        texture.LoadImage(buffer);
        offsetBytes += size;
        OnTextureReceived(m_textureNames[i], texture);
      }

      for(int i = 0; i < m_materialSizes.Count; i++) {
        int size = m_materialSizes[i];
        byte[] buffer = new byte[size];
        Buffer.BlockCopy(rawData, offsetBytes, buffer, 0, size);
        string jsonString = Encoding.UTF8.GetString(buffer);
        MaterialInfo info = JsonUtility.FromJson<MaterialInfo>(jsonString);
        offsetBytes += size;
        OnMaterialInfoReceived(m_materialNames[i], info);
      }

      for(int i = 0; i < m_meshSizes.Count; i++) {
        int size = m_meshSizes[i];
        byte[] buffer = new byte[size];
        Buffer.BlockCopy(rawData, offsetBytes, buffer, 0, size);
        string jsonString = Encoding.UTF8.GetString(buffer);
        MeshInfo info = JsonUtility.FromJson<MeshInfo>(jsonString);
        offsetBytes += size;
        OnMeshInfoReceived(m_meshNames[i], info);
      }
      isRequestComplete = true;
    }

    void OnMeshInfoReceived(string name, MeshInfo info)
    {
      Mesh mesh = new Mesh();
      mesh.name = info.name + "_stm";
      mesh.MarkDynamic();

      Vector3[] verts = new Vector3[info.vertexCount];
      mesh.SetVertices(new List<Vector3>(verts));
      mesh.subMeshCount = info.subMeshCount;
      mesh.bounds = new Bounds(
          Vector3.zero, new Vector3(m_areaRange / 2, m_areaRange, m_areaRange / 2));

      List<int> multiIndices = info.indices;
      int offset = 0;

      List<Material> materials = new List<Material>();
      for (int i = 0; i < info.subMeshCount; i++)
      {
        int indicesCnt = info.indicesCounts[i];
        List<int> indices = multiIndices.GetRange(offset, indicesCnt);
        offset += indicesCnt;
        mesh.SetIndices(indices.ToArray(), MeshTopology.Triangles, i);

        Material mat = null;
        foreach (KeyValuePair<string, Material> pair in m_materialList)
        {
          if (pair.Key == info.materialNames[i])
          {
            mat = pair.Value;
            mat.name = pair.Key;
          }
        }
        materials.Add(mat);
      }

      mesh.uv = info.uv;
      mesh.uv2 = info.uv2;
      mesh.uv3 = info.uv3;
      mesh.uv4 = info.uv4;

      if (localRoot == null)
      {
        localRoot = new GameObject("ReceivedGameObject");
        localRoot.transform.SetParent(transform, false);
      }

      GameObject obj = new GameObject("Mesh" + meshBuf.Count);
      obj.transform.SetParent(localRoot.transform, false);
      MeshFilter filter = obj.AddComponent<MeshFilter>();
      MeshRenderer renderer = obj.AddComponent<MeshRenderer>();

      filter.mesh = mesh;
      renderer.materials = materials.ToArray();
      vertsBuf[currentMesh] = new float[mesh.vertexCount * 3];
      vertsBuf_old[currentMesh] = new float[mesh.vertexCount * 3];
      currentMesh++;

      meshBuf.Add(mesh);
      meshObjects.Add(obj);
    }

    void OnMaterialInfoReceived(string name, MaterialInfo matInfo)
    {
      Material mat;
      Shader refShader = null;
      bool result = customShaders.GetTable().TryGetValue(name.TrimEnd('\0'), out refShader);
      if (result)
        mat = new Material(refShader);
      else
        mat = new Material(defaultShader);

      if (mat != null)
      {
        foreach (MaterialPropertyInfo info in matInfo.properties)
        {
          switch (info.type)
          {
            case 0://ShaderUtil.ShaderPropertyType.Color:
              Color col = JsonUtility.FromJson<Color>(info.value);
              mat.SetColor(info.name, col);
              break;
            case 1://ShaderUtil.ShaderPropertyType.Vector:
              Vector4 vec = JsonUtility.FromJson<Vector4>(info.value);
              mat.SetVector(info.name, vec);
              break;
            case 2://ShaderUtil.ShaderPropertyType.Float:
              float fValue = JsonUtility.FromJson<float>(info.value);
              mat.SetFloat(info.name, fValue);
              break;
            case 3://ShaderUtil.ShaderPropertyType.Range:
              float rValue = JsonUtility.FromJson<float>(info.value);
              mat.SetFloat(info.name, rValue);
              break;
            case 4://ShaderUtil.ShaderPropertyType.TexEnv:
              foreach (KeyValuePair<string, Texture2D> pair in m_textureList)
              {
                if (pair.Key == info.value)
                {
                  Texture2D texture = pair.Value;
                  mat.SetTexture(info.name, pair.Value);
                }
              }
              break;
          }
        }
        //end of foreach
      }
      else
        Debug.LogError("Not found ANY shader!");

      KeyValuePair<string, Material> matPair = new KeyValuePair<string, Material>(matInfo.name, mat);
      m_materialList.Add(matPair);
    }

    void OnTextureReceived(string name, Texture2D texture)
    {
      m_textureList.Add(new KeyValuePair<string, Texture2D>(name, texture));
    }

    void OnStreamListReceived(string name, string list)
    {
      string[] lines = System.Text.RegularExpressions.Regex.Split(list, "\n");
      bool listUpdated = false;
      foreach (string line in lines)
      {
        StreamInfo info = JsonUtility.FromJson<StreamInfo>(line);
        if (info != null)
        {
          int index;
          if (int.TryParse(Path.GetFileNameWithoutExtension(info.video), out index))
          {
            if (m_streamBufferList.Contains(index) == false)
            {
              listUpdated = true;
              m_streamBufferList.Add(index);
              m_combineddBufferStartTicks.Add(info.startTicks);
              m_combinedBufferEndTicks.Add(info.endTicks);
              m_serializer.Request(info.video);
              break;
            }
          }
        }
      }
      if(listUpdated) {
        m_streamRefreshInterval = 1.0f;
      } else {
        m_streamRefreshInterval = m_frameInterval * m_combinedFrames * 2;
      }

      if (listUpdated || m_currentBufferIndex < m_bufferedStream.Count - m_combinedFrames)
      {
        remainPlayableStream = true;
      }
      else
      {
        remainPlayableStream = false;
      }

      // End of OnStreamListReceived()
    }
    void OnStreamDataReceived(string name, byte[] data)
    {
      int index;
      if (int.TryParse(name, out index))
      {
        if (m_streamBufferList.Contains(index))
        {
          if(data.Length == 0) {
            Debug.LogWarning("StreamBuffer is null, abort processing.");
            return;
          }
          List<byte> rawBuffer = new List<byte>(ExternalTools.Decompress(data));
          int frameCnt = BitConverter.ToInt32(rawBuffer.GetRange(0, sizeof(int)).ToArray(), 0);
          byte[] sizeBuffer = rawBuffer.GetRange(0, (frameCnt + 1) * sizeof(int)).ToArray();
          rawBuffer.RemoveRange(0, sizeBuffer.Length);
          List<KeyValuePair<double, byte[]>> buffers = new List<KeyValuePair<double, byte[]>>();
          int cnt = 0;
          for (int i = 1; i < frameCnt + 1; i++)
          {
            int size = BitConverter.ToInt32(sizeBuffer, i * sizeof(int));
            double time = index * 10 + (double)cnt * 0.1;
            byte[] buf = rawBuffer.GetRange(0, size).ToArray();
            buffers.Add(new KeyValuePair<double, byte[]>(time, buf));
            rawBuffer.RemoveRange(0, size);
            cnt++;
          }
          /*
          for (int i = 0; i < m_streamBufferByteSize[index].Length; i++)
          {
            int size = m_streamBufferByteSize[index][i];
            double time = index * 10 + (double)cnt * 0.1;
            byte[] buf = rawBuffer.GetRange(0, size).ToArray();
            buffers.Add(new KeyValuePair<double, byte[]>(time, buf));
            rawBuffer.RemoveRange(0, size);
            cnt++;
            //Debug.Log("INDEX: " + index + ", time: " + time + ", size: " + size);
          }
          */
          m_bufferedStream.AddRange(buffers);
        }
      }
    }

#if !UNITY_WEBGL
    void OnAudioListReceived(string name, string list) {
      string[] lines = System.Text.RegularExpressions.Regex.Split(list, "\n");
      foreach (string line in lines)
      {
        AudioInfo info = JsonUtility.FromJson<AudioInfo>(line);
        if (info != null)
        {
          int index;
          if (int.TryParse(Path.GetFileNameWithoutExtension(info.audio), out index))
          {
            if (m_audioBufferList.Contains(index) == false)
            {
              m_audioBufferList.Add(index);
              m_serializer.Request(info.audio);
              break;
            }

          }
        }
      }
    }
#endif
    
    AudioClip m_test;
    void OnAudioDataReceived(string name, AudioClip audio) {
#if !UNITY_WEBGL
      int index;
      if (int.TryParse(name, out index))
      {
        if (m_audioBufferList.Contains(index))
        {
          if(audio == null) {
            Debug.LogWarning("AudioBuffer is null, abort processing.");
            return;
          }
          float[] srcSample = new float[audio.samples * audio.channels];
          audio.GetData(srcSample, 0);
          int audioBufferSize = (int)(audio.frequency * audio.channels * (m_combinedFrames * m_frameInterval));
          List<float> destSample = new List<float>(srcSample);
          m_audioSampleData.AddRange(destSample.GetRange(0, audioBufferSize));
        }
      }
#endif
    }

    void UpdateVertsInterpolate()
    {
      if (timeWeight < 1.0f)
      {
        for (int i = 0; i < vertsBuf.Length; i++)
        {
          int size = vertsBuf[i].Length / 3;

          float[] b = vertsBuf[i];
          float[] o = vertsBuf_old[i];

          Vector3[] tempBuf = new Vector3[size];

          for (int j = 0; j < size; j++)
          {
            float ox = o[j * 3 + 0];
            float oy = o[j * 3 + 1];
            float oz = o[j * 3 + 2];

            tempBuf[j].x = ox + (b[j * 3] - ox) * timeWeight;
            tempBuf[j].y = oy + (b[j * 3 + 1] - oy) * timeWeight;
            tempBuf[j].z = oz + (b[j * 3 + 2] - oz) * timeWeight;
          }
          meshBuf[i].vertices = tempBuf;
          tempBuf = null;
          meshBuf[i].RecalculateNormals();
        }

        float w_x = (pos_x - old_x) * timeWeight;
        float w_y = (pos_y - old_y) * timeWeight;
        float w_z = (pos_z - old_z) * timeWeight;
        localRoot.transform.localPosition = new Vector3(old_x + w_x, old_y + w_y, old_z + w_z);

        timeWeight += 1.0f / m_interpolateFrames;
      }
    }

    public bool VerticesReceived(byte[] data)
    {
      if (!isRequestComplete || data == null) return false;

      old_x = pos_x; old_y = pos_y; old_z = pos_z;

      for (int i = 0; i < vertsBuf.Length; i++)
        Buffer.BlockCopy(vertsBuf[i], 0, vertsBuf_old[i], 0, vertsBuf[i].Length * sizeof(float));

      int keyFrame;
      int result = vPack.Decode(
        data, ref vertsBuf,
        out pos_x, out pos_y, out pos_z,
        out keyFrame);

      timeWeight = 0.0f;
      return (keyFrame == 1);
    }
  }
}

