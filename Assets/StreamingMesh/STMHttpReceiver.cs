// STMHttpReceiver.cs
//
//Copyright (c) 2017 Tatsuro Matsubara.
//Creative Commons License
//This file is licensed under a Creative Commons Attribution-ShareAlike 4.0 International License.
//https://creativecommons.org/licenses/by-sa/4.0/
//

using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.IO;

namespace StreamingMesh {

	[System.Serializable]
	public class ShaderPair : Serialize.KeyAndValue<string, Shader> {
		public ShaderPair(string key, Shader value) : base(key, value) {
		}
	}

	[System.Serializable]
	public class ShaderTable : Serialize.TableBase<string, Shader, ShaderPair> {
	}
	
	[RequireComponent(typeof(STMHttpSerializer))]
	public class STMHttpReceiver : MonoBehaviour {
		public bool referFromSerializer = true;
		public string streamFile = "stream.json";

		//serializer and material buffer, texture buffer
		STMHttpSerializer serializer;
		List<KeyValuePair<string, Material>> materialList = new List<KeyValuePair<string, Material>>();
		List<KeyValuePair<string, Texture2D>> textureList = new List<KeyValuePair<string, Texture2D>>();
		string streamInfoURL = null;

		int areaRange = 4;
		int packageSize = 128;
		float frameInterval = 0.1f;
		int subframesPerKeyframe = 4;
		int combinedFrames = 100;

		public float streamRefreshInterval = 10.0f;
		float streamCurrentWait = 0.0f;
		List<int> streamBufferList = new List<int>();
		List<int[]> streamBufferByteSize = new List<int[]>();
		List<KeyValuePair<double, byte[]>> bufferedStream = new List<KeyValuePair<double, byte[]>>();
		int currentBufferIndex;
		float vertexUpdateInterval = 0.1f;
		float currentStreamWait = 0.0f;

		public int interpolateFrames = 5;

		//temporary buffers
		List<int[]> indicesBuf = new List<int[]>();
		List<Vector3[]> vertsBuf = new List<Vector3[]>();
		List<Vector3[]> vertsBuf_old = new List<Vector3[]>();
		Vector3 position;
		Vector3 position_old;
		List<int> linedIndices = new List<int>();

		//gameobjects and meshes
		List<GameObject> meshObjects = new List<GameObject>();
		GameObject localRoot = null;
		List<Mesh> meshBuf = new List<Mesh>();

		bool isRequestComplete = false;
		int requestQueue = 0;

		float currentInterporateWait = 0.0f;
		float timeWeight = 0.0f;

		//Shaders
		public Shader defaultShader;
		public ShaderTable customShaders;

		void Reset() {
			if(localRoot != null) {
				DestroyImmediate(localRoot);
			}

			foreach(GameObject obj in meshObjects) {
				DestroyImmediate(obj);
			}

			streamBufferList.Clear();
			streamBufferByteSize.Clear();
			bufferedStream.Clear();

			meshBuf.Clear();
			materialList.Clear();
			textureList.Clear();
			streamInfoURL = null;

			indicesBuf.Clear();
			vertsBuf.Clear();
			vertsBuf_old.Clear();
			linedIndices.Clear();

			//isConnected = false;
			isRequestComplete = false;
		}

		// Use this for initialization
		void Start () {
			Reset();
			InitializeReceiver();
			string url = "";
			if (referFromSerializer) {
				url = serializer.address + serializer.channel + "/" + streamFile;
			} else {
				url = streamFile;
			}
			serializer.Request(url);
		}

		void InitializeReceiver() {
			serializer = gameObject.GetComponent<STMHttpSerializer>();

			serializer.OnChannelInfoReceived = OnInitialDataReceived;
			serializer.OnMeshInfoReceived = OnMeshInfoReceived;
			serializer.OnMaterialInfoReceived = OnMaterialInfoReceived;
			serializer.OnTextureReceived = OnTextureReceived;

			serializer.OnStreamListReceived = OnStreamListReceived;
			serializer.OnStreamReceived = OnStreamDataReceived;
		}

		void OnInitialDataReceived(string name, ChannelInfo info) {
			areaRange = info.area_range;
			packageSize = info.package_size;
			frameInterval = info.frame_interval;
			combinedFrames = info.combined_frames;
			streamInfoURL = info.stream_info;

			foreach(string textureURL in info.textures) {
				serializer.Request(textureURL);
				requestQueue++;
			}

			foreach(string materialURL in info.materials) {
				serializer.Request(materialURL);
				requestQueue++;
			}

			foreach(string meshURL in info.meshes) {
				serializer.Request(meshURL);
				requestQueue++;
			}

			isRequestComplete = true;
		}

		void OnMeshInfoReceived(string name, MeshInfo info) {
			Mesh mesh = new Mesh();
			mesh.name = info.name + "_stm";

			Vector3[] verts = new Vector3[info.vertexCount];
			mesh.SetVertices(new List<Vector3>(verts));
			mesh.subMeshCount = info.subMeshCount;

			List<int> multiIndices = info.indices;
			int offset = 0;

			List<Material> materials = new List<Material>();
			for(int i = 0; i < info.subMeshCount; i++) {
				int indicesCnt = info.indicesCounts[i];
				List<int> indices = multiIndices.GetRange(offset, indicesCnt);
				offset += indicesCnt;
				mesh.SetIndices(indices.ToArray(), MeshTopology.Triangles, i);

				Material mat = null;
				foreach(KeyValuePair<string, Material> pair in materialList) {
					if (pair.Key == info.materialNames[i]) {
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

			if(localRoot == null) {
				localRoot = new GameObject("ReceivedGameObject");
				localRoot.transform.SetParent(transform, false);
			}

			GameObject obj = new GameObject("Mesh" + meshBuf.Count);
			obj.transform.SetParent(localRoot.transform, false);
			MeshFilter filter = obj.AddComponent<MeshFilter>();
			MeshRenderer renderer = obj.AddComponent<MeshRenderer>();

			filter.mesh = mesh;
			renderer.materials = materials.ToArray();
			vertsBuf.Add(new Vector3[mesh.vertexCount]);
			vertsBuf_old.Add(new Vector3[mesh.vertexCount]);

			meshBuf.Add(mesh);
			meshObjects.Add(obj);
			requestQueue--;
		}

		void OnMaterialInfoReceived(string name, MaterialInfo info) {
			Material mat;
			Shader refShader = null;
			bool result = customShaders.GetTable().TryGetValue(name.TrimEnd('\0'), out refShader);
			if(result) {
				if(refShader != null) {
					mat = new Material(refShader);
				} else {
					mat = new Material(defaultShader);
				}
			} else {
				mat = new Material(defaultShader);
			}
			if (mat != null) {
				foreach(MaterialPropertyInfo tinfo in info.properties) {
					switch(tinfo.type) {
					case 0://ShaderUtil.ShaderPropertyType.Color:
						{
							Color col = JsonUtility.FromJson<Color>(tinfo.value);
							mat.SetColor(tinfo.name, col);
						}
						break;
					case 1://ShaderUtil.ShaderPropertyType.Vector:
						{
							Vector4 vec = JsonUtility.FromJson<Vector4>(tinfo.value);
							mat.SetVector(tinfo.name, vec);
						}
						break;
					case 2://ShaderUtil.ShaderPropertyType.Float:
						{
							float value = JsonUtility.FromJson<float>(tinfo.value);
							mat.SetFloat(tinfo.name, value);
						}
						break;
					case 3://ShaderUtil.ShaderPropertyType.Range:
						{
							float value = JsonUtility.FromJson<float>(tinfo.value);
							mat.SetFloat(tinfo.name, value);
						}
						break;
					case 4://ShaderUtil.ShaderPropertyType.TexEnv:
						{
							foreach(KeyValuePair<string, Texture2D> pair in textureList) {
								if (pair.Key == tinfo.value) {
									Texture2D texture = pair.Value;
									mat.SetTexture(tinfo.name, pair.Value);
								}
							}
						}
						break;
					}
				}
				//end of foreach
			}

			KeyValuePair<string, Material> matPair = new KeyValuePair<string, Material>(info.name, mat);
			materialList.Add(matPair);
			requestQueue--;
		}

		void OnTextureReceived(string name, Texture2D texture) {
			textureList.Add(new KeyValuePair<string, Texture2D>(name, texture));
			requestQueue--;
		}

		void OnStreamListReceived(string name, string list) {
			string[] lines = System.Text.RegularExpressions.Regex.Split(list, "\n");
			foreach(string line in lines) {
				StreamInfo info = JsonUtility.FromJson<StreamInfo>(line);
				if (info != null) {
					Uri uri = new Uri(info.name);
					int index;
					if (int.TryParse(Path.GetFileNameWithoutExtension(uri.AbsolutePath), out index)) {
						if (streamBufferList.Contains(index) == false) {
							streamBufferList.Add(index);
							streamBufferByteSize.Add(info.size.ToArray());
							serializer.Request(info.name);
						}
					}
				}
			}
			// End of OnStreamListReceived()
		}

		void OnStreamDataReceived(string name, byte[] data) {
			int index;
			if (int.TryParse(name, out index)) {
				if (streamBufferList.Contains(index)) {
					List<byte> rawBuffer = new List<byte>(STMHttpBaseSerializer.Decompress(data));
					List<KeyValuePair<double, byte[]>> buffers = new List<KeyValuePair<double, byte[]>>();
					int cnt = 0;
					foreach(int size in streamBufferByteSize[index]) {
						double time = index * 10 + (double)cnt * 0.1;
						byte[] buf = rawBuffer.GetRange(0, size).ToArray();
						buffers.Add(new KeyValuePair<double, byte[]>(time, buf));
						rawBuffer.RemoveRange(0, size);
						cnt++;
						//Debug.Log("INDEX: " + index + ", time: " + time + ", size: " + size);
					}
					bufferedStream.AddRange(buffers);
				}
			}
		}

		bool onlyOnce = false;

		// Update is called once per frame
		void Update() {
			if(bufferedStream.Count > 0 && !onlyOnce) {
				var player = FindObjectOfType<AudioPlayerOgg>();
				if(player != null) {
					player.StartLoading();
					onlyOnce = true;
					Invoke("SeekToZero", 1f);
				}
			}
			
			if (isRequestComplete && requestQueue < 1) {
				streamCurrentWait += Time.deltaTime;
				if (streamCurrentWait > streamRefreshInterval) {
					if (streamInfoURL != null) {
						serializer.Request(streamInfoURL);
					}
					streamCurrentWait -= streamRefreshInterval;
				}

				currentStreamWait += Time.deltaTime;
				if (currentStreamWait > vertexUpdateInterval) {
					if (currentBufferIndex < bufferedStream.Count) {
						byte[] data = bufferedStream[currentBufferIndex].Value;
						//Debug.Log("UpdateTime:" + bufferedStream[currentIndex].Key);
						VerticesReceived(data);
						currentBufferIndex++;
					}
					currentStreamWait -= vertexUpdateInterval;
				}

				currentInterporateWait += Time.deltaTime;
				if(currentInterporateWait > frameInterval / (float)interpolateFrames) {
					currentInterporateWait -= frameInterval / (float)interpolateFrames;
					UpdateVertsInterpolate();
				}
			}

		}

		public void SeekToZero() {
			SeekTo(0);
		}

		public void SeekTo(string bufferedTime) {
			int parseTime;
			if (int.TryParse(bufferedTime, out parseTime)) {
				SeekTo(parseTime);
			}
		}

		public void SeekTo(int bufferedTime) {
			if(bufferedStream.Count == 0) {
				return;
			}
			bufferedTime = bufferedTime < bufferedStream.Count ? bufferedTime : bufferedStream.Count - 1;
			int segment = bufferedTime / (subframesPerKeyframe + 1);
			int iTime = segment * (subframesPerKeyframe + 1);
			currentBufferIndex = iTime;
			byte[] data = bufferedStream[currentBufferIndex].Value;
			VerticesReceived(data);
			VerticesReceived(data);
			currentBufferIndex++;
			currentStreamWait = 0;
			currentInterporateWait = 0;
			UpdateVertsInterpolate();
		}

		void UpdateVertsInterpolate() {
			if(timeWeight < 1.0f) {
				for(int i = 0; i < vertsBuf.Count; i++) {
					Vector3[] tempBuf = new Vector3[vertsBuf[i].Length];
					Vector3 tempPos = new Vector3();
					for(int j = 0; j < vertsBuf[i].Length; j++) {
						Vector3 old = vertsBuf_old[i][j];
						Vector3 dst = vertsBuf[i][j];
						tempBuf[j] = old * (1.0f - timeWeight) + dst * timeWeight;
						tempPos = position_old * (1.0f - timeWeight) + position * timeWeight;
					}
					meshBuf[i].SetVertices(new List<Vector3>(tempBuf));
					meshBuf[i].RecalculateNormals();
					meshBuf[i].RecalculateBounds();
					localRoot.transform.localPosition = tempPos;
				}
			}
			timeWeight += 1.0f / interpolateFrames;
		}

		bool getErrorData = false;

		public void VerticesReceived(byte[] data)
		{
			if(isRequestComplete) {
				for(int i = 0; i < vertsBuf.Count; i++) {
					vertsBuf[i].CopyTo(vertsBuf_old[i], 0);
				}
				position_old = position;

				int packages = data[7] * 65536 + data[6] * 256 + data[5];
				//bool isCompressed = data[8] == 0x01 ? true : false;

				byte[][] byteVec = new byte[3][];
				for(int i = 0; i < 3; i++) {
					byteVec[i] = new byte[4];
					for(int j = 0; j < 4; j++) {
						byteVec[i][j] = data[i * 4 + j + 9];
					}
				}
				position = new Vector3(
					BitConverter.ToSingle(byteVec[0], 0),
					BitConverter.ToSingle(byteVec[1], 0),
					BitConverter.ToSingle(byteVec[2], 0)
				);

				int offset = 21;

				byte[] buf = data;

				if(data[0] == 0x0F) {
					linedIndices.Clear();

					for(int i = 0; i < packages; i++) {
						VertexPack vp = new VertexPack();
						vp.tx = buf[offset];
						vp.ty = buf[offset + 1];
						vp.tz = buf[offset + 2];
						vp.poly1 = buf[offset + 3];
						vp.poly2 = buf[offset + 4];
						vp.poly3 = buf[offset + 5];

						offset += 6;

						int hk = packageSize / 2;
						int qk = hk / areaRange;

						int vertCount = vp.poly3 * 65536 + vp.poly2 * 256 + vp.poly1;
						for(int j = 0; j < vertCount; j++) {
							ByteCoord v = new ByteCoord();
							v.p1 = buf[offset + j * 5];
							v.p2 = buf[offset + j * 5 + 1];
							v.p3 = buf[offset + j * 5 + 2];
							int compress = 0;
							compress += buf[offset + j * 5 + 3];
							compress += (ushort)(buf[offset + j * 5 + 4] << 8);

							v.x = (byte)(compress & 0x1F);
							v.y = (byte)((compress >> 5) & 0x1F);
							v.z = (byte)((compress >> 10) & 0x1F);

							float x = ((int)vp.tx - hk) / (float)qk;
							float y = ((int)vp.ty - hk) / (float)qk;
							float z = ((int)vp.tz - hk) / (float)qk;
							x += (float)v.x / (32 * (float)qk);
							y += (float)v.y / (32 * (float)qk);
							z += (float)v.z / (32 * (float)qk);

							int vertIdx = v.p2 * 256 + v.p1;
							int meshIdx = v.p3;

							Vector3 vert = new Vector3(x, y, z);
							try {
								vertsBuf[meshIdx][vertIdx] = vert;
							} catch(IndexOutOfRangeException ie) {
								//Debug.Log("RECEIVER: some vertex data is broken, " + ie.Message);
								getErrorData = true;
								break;
							}
							linedIndices.Add(meshIdx * 0x10000 + vertIdx);
							getErrorData = false;
						}
						offset += (vertCount * 5);
					}
				} else if(data[0] == 0x0E && !getErrorData) {
					for(int i = 0; i < linedIndices.Count; i++) {
						int meshIdx = (linedIndices[i] >> 16) & 0xFF;
						int vertIdx = linedIndices[i] & 0xFFFF;
						int ix = buf[i * 3 + offset];
						int iy = buf[i * 3 + offset + 1];
						int iz = buf[i * 3 + offset + 2];
						float dx = ((float)ix - 128f) / 128f;
						float dy = ((float)iy - 128f) / 128f;
						float dz = ((float)iz - 128f) / 128f;
						float x = Mathf.Sign(dx) * Mathf.Pow(Mathf.Abs(dx), 2f);
						float y = Mathf.Sign(dy) * Mathf.Pow(Mathf.Abs(dy), 2f);
						float z = Mathf.Sign(dz) * Mathf.Pow(Mathf.Abs(dz), 2f);

						Vector3 vec = vertsBuf[meshIdx][vertIdx];
						vec = vec + new Vector3(x, y, z);
						vertsBuf[meshIdx][vertIdx] = vec;
					}
				}
			}
			timeWeight = 0.0f;
		}
	}
}
