using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;

#if UNITY_EDITOR
using UnityEditor;
#endif

using StreamingMesh;

using StreamingMesh.Core.Serialization;

namespace StreamingMesh {

  [ExecuteInEditMode, RequireComponent(typeof(STMHttpSerializer)), RequireComponent(typeof(STMAudioRecorder))]
  public class STMHttpSender : MonoBehaviour {
#if UNITY_EDITOR
    private ComputeShader tiling;
    private ComputeShader diff;

    struct TiledVertex {
      public int tileID;
      public int polyIndex;
      public int x;
      public int y;
      public int z;
    }

    struct FragmentVertex {
      public int x;
      public int y;
      public int z;
    }

    static int[] alignedVerts = { 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 };
    static string[] kernelNames = {
      "cs_main8", "cs_main8",
      "cs_main32", "cs_main32",
      "cs_main128", "cs_main128",
      "cs_main512", "cs_main512",
      "cs_main512", "cs_main512",
      "cs_main512", "cs_main512",
      "cs_main512"
    };
    static int[] dispatches = { 2, 4, 2, 4, 2, 4, 2, 4, 8, 16, 32, 64, 128 };

    STMHttpSerializer serializer;
    STMAudioRecorder audioRecorder;
    List<SkinnedMeshRenderer> renderers;

    public int containerSize = 4;
    public int packageSize = 128;
    public float frameInterval = 0.1f;
    public int subframesPerKeyframe = 4;
    public int combinedFrames = 100;

    Vector3[][] oldVertsBuf;
    float[][] oldMatrix;
    List<int> linedIndices;

    float currentTime;
    int frameCnt = 0;
    uint timeStamp = 0;

    List<int> byteSize;
    List<byte> combinedBinary;

    long recordStartTicks;
    long tempStartTicks;
    public GameObject targetGameObject;

    bool startRecord = false;
    public bool IsStartRecord {
        get {
            return startRecord;
        }
    }

    public void Record() {
      if(audioRecorder != null) {
        audioRecorder.RecordTimeSpan = (int)(frameInterval * combinedFrames);
        audioRecorder.Record();
      }
      if (startRecord == false) {
        startRecord = true;
        recordStartTicks = DateTime.Now.Ticks;
      }
    }

    public void Stop() {
      startRecord = false;
    }

    void Awake() {
      tiling = Resources.Load("TilingShader") as ComputeShader;
      diff = Resources.Load("DiffShader") as ComputeShader;
      startRecord = IsStartRecord;
    }

    public void Start() {
      audioRecorder = gameObject.GetComponent<STMAudioRecorder>();
      if(audioRecorder != null) {
        audioRecorder.OnGetEncodedAudioData = OnGetEncodedAudioData;
      }
      InitializeSender();
    }

    void OnValidate() {
      if (packageSize > 255) {
        packageSize = 255;
      }
    }

    void OnGetEncodedAudioData(long ticks, string encodingFilename, byte[] data) {
      AudioInfo audioinfo = serializer.CreateAudioInfo(ticks);
      serializer.Send(audioinfo, ticks);
      serializer.Send(data, "audio", encodingFilename);
    }

    // Use this for initialization
    public void CreateChannel() {
      //audioRecorder = gameObject.GetComponent<STMAudioRecorder>();
      //InitializeSender();
      CreateInfos();
    }

    void InitializeSender() {
      if (packageSize > 255) {
          packageSize = 255;
      }
      byteSize = new List<int>();
      combinedBinary = new List<byte>();

      renderers = new List<SkinnedMeshRenderer>();
      serializer = gameObject.GetComponent<STMHttpSerializer>();

      if (targetGameObject != null) {
        SkinnedMeshRenderer[] psmrs = targetGameObject.GetComponents<SkinnedMeshRenderer>();
        SkinnedMeshRenderer[] csmrs = targetGameObject.GetComponentsInChildren<SkinnedMeshRenderer>();
        if(psmrs.Length != 0) {
          renderers.AddRange(psmrs);
        }
        if(csmrs.Length != 0) {
          renderers.AddRange(csmrs);
        }
      } else {
        if(Application.isPlaying) {
          Debug.LogError("Target GameObject is null, set target GameObject.");
        } else {
          Debug.LogWarning("Target GameObject is null, set target GameObject.");
        }
        return;
      }

      oldVertsBuf = new Vector3[renderers.Count][];
      oldMatrix = new float[renderers.Count][];
      linedIndices = new List<int>();
    }

    void CreateInfos() {
      if(serializer == null || renderers == null || targetGameObject == null) {
        string errorString = "";
        errorString += (serializer == null ? "serializer " : "");
        errorString += (renderers == null ? "rendrers " : "");
        errorString += (targetGameObject == null ? "target GameObject " : "");
        errorString += "is null on create channel!";
        Debug.LogError(errorString);
      }

      Dictionary<string, Material> materials = new Dictionary<string, Material>();
      Dictionary<string, Texture> textures = new Dictionary<string, Texture>();
      List<MeshInfo> meshInfos = new List<MeshInfo>();
      List<MaterialInfo> materialInfos = new List<MaterialInfo>();

      List<string> meshNames = new List<string>();
      List<string> materialNames = new List<string>();
      List<string> textureNames = new List<string>();

      for(int i = 0; i < renderers.Count; i++) {
        SkinnedMeshRenderer renderer = renderers[i];
        if(renderer != null) {
          MeshInfo meshInfo = serializer.CreateMeshInfo(renderer);
          if(meshInfo == null) {
            continue;
          }
          meshInfos.Add(meshInfo);
          meshNames.Add("mesh" + i);

          for(int j = 0; j < renderer.sharedMaterials.Length; j++) {
            Material mat = renderer.sharedMaterials[j];
            Material dummy;
            if(materials.TryGetValue(mat.name, out dummy)) {
              continue;
            }
            materials.Add(mat.name, mat);


            MaterialInfo materialInfo = serializer.CreateMaterialInfo(mat);
            if(materialInfo == null) {
              continue;
            }
            materialInfos.Add(materialInfo);
            materialNames.Add("material" + materialNames.Count);

            List<KeyValuePair<string, Texture>> texturePairs =
              serializer.GetTexturesFromMaterial(mat);
            foreach(KeyValuePair<string, Texture> texturePair in texturePairs) {
              Texture dummyTexture;
              if(textures.TryGetValue(texturePair.Key, out dummyTexture)) {
                continue;
              }
              textures.Add(texturePair.Key, texturePair.Value);
              textureNames.Add(texturePair.Key);
            }
          }
        }
      }

      List<int> meshBuffersSize = new List<int>();
      List<int> matBuffersSize = new List<int>();
      List<int> texBuffersSize = new List<int>();
      List<byte> combinedBuffer = new List<byte>();
      
      foreach(KeyValuePair<string, Texture> texturePair in textures) {
        Texture texture = texturePair.Value;
        byte[] buffer = STMHttpSerializer.GetTextureToPNGByteArray(texture, true);
        texBuffersSize.Add(buffer.Length);
        combinedBuffer.AddRange(buffer);
      }

      for (int i = 0; i < materialInfos.Count; i++) {
        MaterialInfo matInfo = materialInfos[i];
        string matString = JsonUtility.ToJson(matInfo);
        byte[] buffer = Encoding.UTF8.GetBytes(matString);
        matBuffersSize.Add(buffer.Length);
        combinedBuffer.AddRange(buffer);
      }

      for(int i = 0; i < meshInfos.Count; i++) {
        MeshInfo meshInfo = meshInfos[i];
        string meshString = JsonUtility.ToJson(meshInfo);
        byte[] buffer = Encoding.UTF8.GetBytes(meshString);
        meshBuffersSize.Add(buffer.Length);
        combinedBuffer.AddRange(buffer);
      }

      byte[] compressedBuffer = StreamingMesh.Lib.ExternalTools.Compress(combinedBuffer.ToArray());

      ChannelInfo channelInfo = serializer.CreateChannelInfo(
        containerSize,
        packageSize,
        frameInterval,
        combinedFrames,
        meshNames,
        materialNames,
        textureNames,
        meshBuffersSize,
        matBuffersSize,
        texBuffersSize
      );

      serializer.Send(channelInfo);
      serializer.Send(compressedBuffer);
    }

    void SaveToJson(string path, string data) {
      StreamWriter sw = new StreamWriter(path);
      sw.Write(data);
      sw.Close();
    }

    void SaveToBinary(string path, byte[] data) {
      FileStream fs = new FileStream(path, FileMode.OpenOrCreate);
      BinaryWriter bw = new BinaryWriter(fs);
      bw.Write(data);
      bw.Close();
      fs.Close();
    }

    void LateUpdate() {
      if(!Application.isPlaying || !EditorApplication.isPlayingOrWillChangePlaymode || !startRecord) {
        return;
      }

      currentTime += Time.deltaTime;
      if (currentTime > frameInterval) {
        currentTime -= frameInterval;
      } else {
        return;
      }

      bool isIframe = frameCnt == 0 ? true : false;
      if (isIframe) {
        tiling.SetInt("packSize", packageSize);
        tiling.SetInt("range", containerSize);
      }

      if (renderers != null) {
        Dictionary<int, TilePacker> dicPacks = new Dictionary<int, TilePacker>();
        List<FragmentVertex[]> frag = new List<FragmentVertex[]>();

        for (int i = 0; i < renderers.Count; i++) {
          SkinnedMeshRenderer smr = renderers[i];
          if (smr != null) {
            //convert to tiled vertices
            Mesh tempMesh = new Mesh();
            smr.BakeMesh(tempMesh);

            //calculate shader threads and dipatch size;
            int alignedNum = 16;
            string kernelName = "cs_main8";
            int verts = tempMesh.vertices.Length;
            int dispatch = 2;
            for (int k = 0; k < alignedVerts.Length; k++) {
                int aligned = alignedVerts[k];
                if (verts - aligned <= 0) {
                    alignedNum = aligned;
                    kernelName = kernelNames[k];
                    dispatch = dispatches[k];
                    break;
                }
            }

            Vector3[] src = new Vector3[alignedNum];
            tempMesh.vertices.CopyTo(src, 0);
            DestroyImmediate(tempMesh);

            Quaternion quat = smr.transform.rotation;
            Vector3 pos = smr.transform.position - targetGameObject.transform.position;
            Vector3 scale = new Vector3(1, 1, 1);

            Matrix4x4 wrd = Matrix4x4.TRS(pos, quat, scale);

            float[] wrdMatrix = {
              wrd.m00, wrd.m01, wrd.m02, wrd.m03,
              wrd.m10, wrd.m11, wrd.m12, wrd.m13,
              wrd.m20, wrd.m21, wrd.m22, wrd.m23,
              wrd.m30, wrd.m31, wrd.m32, wrd.m33
            };

            if (isIframe) {
              tiling.SetFloats("wrdMatrix", wrdMatrix);
              tiling.SetInt("modelGroup", i);

              ComputeBuffer srcBuf = new ComputeBuffer(alignedNum, Marshal.SizeOf(typeof(Vector3)));
              ComputeBuffer destBuf = new ComputeBuffer(alignedNum, Marshal.SizeOf(typeof(TiledVertex)));
              srcBuf.SetData(src);

              int kernelNum = tiling.FindKernel(kernelName);
              tiling.SetBuffer(kernelNum, "srcBuf", srcBuf);
              tiling.SetBuffer(kernelNum, "destBuf", destBuf);
              tiling.Dispatch(kernelNum, dispatch, 1, 1);

              TiledVertex[] data = new TiledVertex[src.Length];
              destBuf.GetData(data);

              srcBuf.Release();
              destBuf.Release();

              for (int j = 0; j < verts; j++) {
                TiledVertex vert = data[j];
                int tx = (vert.tileID & 0xFF);
                int ty = (vert.tileID & 0xFF00) >> 8;
                int tz = (vert.tileID & 0xFF0000) >> 16;
                int tileID = tx + ty * packageSize + tz * (packageSize * packageSize);
                if (tx == 255 && ty == 255 && tz == 255) {
                    continue;
                }

                TilePacker tile;
                if (!dicPacks.TryGetValue(tileID, out tile)) {
                  tile = new TilePacker(tx, ty, tz);
                  dicPacks.Add(tileID, tile);
                }

                ByteCoord coord = new ByteCoord();
                coord.p1 = (byte)((vert.polyIndex & 0xFF));
                coord.p2 = (byte)((vert.polyIndex & 0xFF00) >> 8);
                coord.p3 = (byte)((vert.polyIndex & 0xFF0000) >> 16);
                coord.x = (byte)vert.x;
                coord.y = (byte)vert.y;
                coord.z = (byte)vert.z;

                dicPacks[tileID].AddVertex(coord);
              }
            } else {
              diff.SetFloats("wrdMatrix", wrdMatrix);
              diff.SetFloats("oldMatrix", oldMatrix[i]);

              ComputeBuffer srcBuf = new ComputeBuffer(alignedNum, Marshal.SizeOf(typeof(Vector3)));
              ComputeBuffer oldBuf = new ComputeBuffer(alignedNum, Marshal.SizeOf(typeof(Vector3)));
              ComputeBuffer destBuf = new ComputeBuffer(alignedNum, Marshal.SizeOf(typeof(FragmentVertex)));
              srcBuf.SetData(src);
              oldBuf.SetData(oldVertsBuf[i]);

              int kernelNum = diff.FindKernel(kernelName);
              diff.SetBuffer(kernelNum, "srcBuf", srcBuf);
              diff.SetBuffer(kernelNum, "oldBuf", oldBuf);
              diff.SetBuffer(kernelNum, "destBuf", destBuf);
              diff.Dispatch(kernelNum, dispatch, 1, 1);

              FragmentVertex[] data = new FragmentVertex[src.Length];
              destBuf.GetData(data);

              srcBuf.Release();
              oldBuf.Release();
              destBuf.Release();

              frag.Add(data);
            }
            oldVertsBuf[i] = src;
            oldMatrix[i] = wrdMatrix;
          }
        }

        int packages = 0;
        List<byte> lPacks = new List<byte>();

        if (isIframe) {
          linedIndices.Clear();
          foreach(KeyValuePair<int, TilePacker> p in dicPacks) {
            packages++;
            TilePacker pack = p.Value;
            lPacks.AddRange(pack.PackToByteArray(packageSize));
            linedIndices.AddRange(pack.getIndices());
          }
        } else {
          for(int i = 0; i < linedIndices.Count; i++) {
            int meshIndex = (linedIndices[i] >> 16) & 0xFF;
            int vertIndex = linedIndices[i] & 0xFFFF;
            FragmentVertex f = frag[meshIndex][vertIndex];
            byte[] val = new byte[3];
            val[0] = (byte)(f.x);
            val[1] = (byte)(f.y);
            val[2] = (byte)(f.z);
            lPacks.AddRange(val);
          }
        }

        int currentBinaryLength = combinedBinary.Count;
        if( currentBinaryLength == 0) {
          tempStartTicks = DateTime.Now.Ticks;
        }

        byte[] streamData = 
          AddHeader(lPacks.ToArray(), targetGameObject.transform.position, packages, isIframe, timeStamp);
        byteSize.Add(streamData.Length);
        combinedBinary.AddRange(streamData);

        timeStamp++;
        if (timeStamp % combinedFrames == 0 && timeStamp > 0) {
          long tick = timeStamp / combinedFrames - 1;
          long startTicks = tempStartTicks - recordStartTicks;
          long endTicks = DateTime.Now.Ticks - recordStartTicks;
          StreamInfo streamInfo = serializer.CreateStreamInfo(tick, startTicks, endTicks);
          serializer.Send(streamInfo, tick);
          string fileName = tick.ToString("000000") + ".stmv";
          byte[] buffer = new byte[(byteSize.Count + 1) * sizeof(int) + combinedBinary.Count];
          byteSize.Insert(0, byteSize.Count);
          Buffer.BlockCopy(byteSize.ToArray(), 0, buffer, 0, byteSize.Count * sizeof(int));
          Buffer.BlockCopy(combinedBinary.ToArray(), 0, buffer, byteSize.Count * sizeof(int), combinedBinary.Count);
          //serializer.Send(ExternalTools.Compress(combinedBinary.ToArray()), "stream", fileName);
          serializer.Send(Lib.ExternalTools.Compress(buffer), "stream", fileName);

          byteSize.Clear();
          combinedBinary.Clear();
        }
      }
      frameCnt = (frameCnt + 1) % (subframesPerKeyframe + 1);
    }

    byte[] AddHeader(byte[] rawData, Vector3 position, int packages, bool isIframe, uint stamp) {
      byte[] outData = null;
      outData = new byte[rawData.Length + 21];
      Buffer.BlockCopy(rawData, 0, outData, 21, rawData.Length);
      outData[8] = 0x00;

      if(isIframe) {
        outData[0] = 0xF;
        byte[] stampBuf = BitConverter.GetBytes(stamp);
        outData[1] = stampBuf[0];
        outData[2] = stampBuf[1];
        outData[3] = stampBuf[2];
        outData[4] = stampBuf[3];

        outData[5] = (byte)((packages & 0xFF));
        outData[6] = (byte)((packages & 0xFF00) >> 8);
        outData[7] = (byte)((packages & 0xFF0000) >> 16);
        //sendData[8] ;
      } else {
        outData[0] = 0xE;
      }
      byte[][] vec = new byte[3][];
      vec[0] = BitConverter.GetBytes(position.x);
      vec[1] = BitConverter.GetBytes(position.y);
      vec[2] = BitConverter.GetBytes(position.z);
      for(int j = 0; j < 3; j++) {
        for(int k = 0; k < 4; k++) {
          outData[j * 4 + k + 9] = vec[j][k];
        }
      }

      return outData;
    }
#endif
  }

}