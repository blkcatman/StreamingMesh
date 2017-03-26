// STMHttpReceiver.cs
//
//Copyright (c) 2017 Tatsuro Matsubara.
//Creative Commons License
//This file is licensed under a Creative Commons Attribution-ShareAlike 4.0 International License.
//https://creativecommons.org/licenses/by-sa/4.0/
//

using UnityEngine;
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
        public float streamRefreshInterval = 10.0f;
        public int interpolateFrames = 5;

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

		float currentStreamWait = 0.0f;
        float currentBufferWait = 0.0f;
        float currentInterpolateWait = 0.0f;

        List<int> streamBufferList = new List<int>();
		List<int[]> streamBufferByteSize = new List<int[]>();
		List<KeyValuePair<double, byte[]>> bufferedStream = new List<KeyValuePair<double, byte[]>>();
		int currentBufferIndex;
		float vertexUpdateInterval = 0.1f;

        bool getErrorData = false;
        bool updateNormals = false;

        //temporary buffers
        List<int[]> indicesBuf = new List<int[]>();
        int currentMesh = 0;
        float[][] vertsBuf;
        float[][] vertsBuf_old;
        float pos_x;
        float pos_y;
        float pos_z;
        float old_x;
        float old_y;
        float old_z;
		List<int> linedIndices = new List<int>();

        //gameobjects and meshes
        List<GameObject> meshObjects = new List<GameObject>();
		GameObject localRoot = null;
		List<Mesh> meshBuf = new List<Mesh>();

		bool isRequestComplete = false;
		int requestQueue = 0;

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
            vertsBuf = null;
            vertsBuf_old = null;
			linedIndices.Clear();

			//isConnected = false;
			isRequestComplete = false;

            currentStreamWait = streamRefreshInterval - 1.0f;
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

            vertsBuf = new float[info.meshes.Count][];
            vertsBuf_old = new float[info.meshes.Count][];

            foreach (string textureURL in info.textures) {
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
            mesh.bounds = new Bounds(
                Vector3.zero, new Vector3(areaRange/2, areaRange, areaRange/2));

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
            vertsBuf[currentMesh] = new float[mesh.vertexCount * 3];
            vertsBuf_old[currentMesh] = new float[mesh.vertexCount * 3];
            currentMesh++;

			meshBuf.Add(mesh);
			meshObjects.Add(obj);
			requestQueue--;
		}

		void OnMaterialInfoReceived(string name, MaterialInfo matInfo) {
			Material mat;
			Shader refShader = null;
			bool result = customShaders.GetTable().TryGetValue(name.TrimEnd('\0'), out refShader);
			if(result)
                mat = new Material(refShader);
			else
				mat = new Material(defaultShader);

			if (mat != null) {
				foreach(MaterialPropertyInfo info in matInfo.properties) {
					switch(info.type) {
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
						foreach(KeyValuePair<string, Texture2D> pair in textureList) {
							if (pair.Key == info.value) {
								Texture2D texture = pair.Value;
								mat.SetTexture(info.name, pair.Value);
							}
						}
						break;
					}
				}
				//end of foreach
			} else {
                Debug.LogError("Not found ANY shader!");
            }

			KeyValuePair<string, Material> matPair = new KeyValuePair<string, Material>(matInfo.name, mat);
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
					for(int i = 0; i < streamBufferByteSize[index].Length; i++) {
						int size = streamBufferByteSize[index][i];
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
					Invoke("SeekToZero", 1f);
				}
                onlyOnce = true;
            }
			
			if (isRequestComplete && requestQueue < 1) {
                float delta = Time.deltaTime;

                currentStreamWait += delta;
				if (currentStreamWait > streamRefreshInterval) {
					if (streamInfoURL != null) {
						serializer.Request(streamInfoURL);
					}
					currentStreamWait -= streamRefreshInterval;
				}

                currentBufferWait += delta;
                if (currentBufferWait > vertexUpdateInterval) {
					if (currentBufferIndex < bufferedStream.Count) {
						//Debug.Log("UpdateTime:" + bufferedStream[currentIndex].Key);
						VerticesReceived(bufferedStream[currentBufferIndex].Value);
						currentBufferIndex++;
					}
					currentBufferWait -= vertexUpdateInterval;
				}

				currentInterpolateWait += delta;
                if (currentInterpolateWait > frameInterval / interpolateFrames) {
					currentInterpolateWait -= frameInterval / interpolateFrames;
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
			currentBufferWait = 0;
			currentInterpolateWait = 0;
			UpdateVertsInterpolate();
		}

		unsafe void UpdateVertsInterpolate() {
			if(timeWeight < 1.0f) {
                for (int i = 0; i < vertsBuf.Length; i++) {
                    int size = vertsBuf[i].Length / 3;

					Vector3[] tempBuf = new Vector3[size];

					fixed (Vector3 *tmp = tempBuf)
                    fixed (float *buf = vertsBuf[i])
					fixed (float *old = vertsBuf_old[i]) {
						Vector3* t = tmp;
                        float* b = buf;
                        float* o = old;
                        for (int j = 0; j < size; j++) {
							float ox = *(o++);
							float oy = *(o++);
							float oz = *(o++);
							t->x = ox + (*(b++) - ox) * timeWeight;
							t->y = oy + (*(b++) - oy) * timeWeight;
							t->z = oz + (*(b++) - oz) * timeWeight;
							t++;
                        }
                    }
                    meshBuf[i].vertices = tempBuf;
					tempBuf = null;
                    if (updateNormals == true) {
                        meshBuf[i].RecalculateNormals();
                        updateNormals = false;
                    }
                }

                float w_x = (pos_x - old_x) * timeWeight;
                float w_y = (pos_y - old_y) * timeWeight;
                float w_z = (pos_z - old_z) * timeWeight;
                localRoot.transform.localPosition = new Vector3(old_x + w_x, old_y + w_y, old_z + w_z);

                timeWeight += 1.0f / interpolateFrames;
            }
		}

		unsafe public void VerticesReceived(byte[] data)
		{
            if(!isRequestComplete) return;

            fixed (byte* d = data) {
                byte* b = d;

                old_x = pos_x;
                old_y = pos_y;
                old_z = pos_z;

                for (int i = 0; i < vertsBuf.Length; i++) {
					Buffer.BlockCopy(vertsBuf[i], 0, vertsBuf_old[i], 0, vertsBuf[i].Length * sizeof(float));
                }

                byte frame = *b; // frame type
                b += 5;
                int packages = *(b++);
                packages += (*(b++) << 8);
                packages += (*(b++) << 16);
                //bool isCompressed = *(b+3) == 0x01 ? true : false;
                b++;

                pos_x = *(float*)b;
                b += 4;
                pos_y = *(float*)b;
                b += 4;
                pos_z = *(float*)b;
                b += 4;
                //current offset 21

                if (frame == 0x0F) {
                    updateNormals = true;
                    linedIndices.Clear();

                    int hk = packageSize/2;
                    float qk = areaRange/(float)hk;
                    float sqk = qk/32f;

                    for (int i = 0; i < packages; i++) {
                        float t_x = (*(b++) - hk)*qk;
                        float t_y = (*(b++) - hk)*qk;
                        float t_z = (*(b++) - hk)*qk;

                        int vertCount = *(b++);
                        vertCount += *(b++) << 8;
                        vertCount += *(b++) << 16;

                        for (int j = 0; j < vertCount; j++) {
                            int vIdx = *(b++);
                            vIdx += *(b++) << 8;
                            int mIdx = *(b++);
                            if (mIdx >= vertsBuf.Length) {
                                getErrorData = true;
                                break;
                            }
                            if (vIdx >= vertsBuf[mIdx].Length / 3) {
                                getErrorData = true;
                                break;
                            }

                            int compress = *(b++);
                            compress += *(b++) << 8;

                            //(*(v+mIdx)+vIdx)->x = t_x + (compress & 0x1F) * sqk;
                            //(*(v+mIdx)+vIdx)->y = t_y + ((compress >> 5) & 0x1F) * sqk;
                            //(*(v+mIdx)+vIdx)->z = t_z + ((compress >> 10) & 0x1F) * sqk;
							vertsBuf[mIdx][vIdx*3    ] = t_x + (compress & 0x1F) * sqk;
							vertsBuf[mIdx][vIdx*3 + 1] = t_y + ((compress >> 5) & 0x1F) * sqk;
							vertsBuf[mIdx][vIdx*3 + 2] =  t_z + ((compress >> 10) & 0x1F) * sqk;

                            linedIndices.Add((mIdx << 16) + vIdx);
                            getErrorData = false;
                        }
                        if (getErrorData) {
                            Debug.LogError("data broken in VerticesReceived()");
                        }
                    }
                } else if (frame == 0x0E && !getErrorData) {
                    //const float dd = 0.0078125f; // 1 / 128f;
                    const float dd = 0.00006103515625f; // 1 / 16384f;
                    for (int i = 0; i < linedIndices.Count; i++) {
                        int idx = linedIndices[i];
                        int mIdx = (idx >> 16) & 0xFF;
                        int vIdx = idx & 0xFFFF;

                        //((a - 128)/128)^2 = ((a-128)^2)/16384;
                        int dx = *(b++) - 128;
                        int dy = *(b++) - 128;
                        int dz = *(b++) - 128;
                        //(*(v+mIdx)+vIdx)->x += dx < 0 ? -(dx*dx)*dd : (dx*dx)*dd;
                        //(*(v+mIdx)+vIdx)->y += dy < 0 ? -(dy*dy)*dd : (dy*dy)*dd;
                        //(*(v+mIdx)+vIdx)->z += dz < 0 ? -(dz*dz)*dd : (dz*dz)*dd;
                        vertsBuf[mIdx][vIdx*3    ] += dx < 0 ? -(dx*dx)*dd : (dx*dx)*dd;
                        vertsBuf[mIdx][vIdx*3 + 1] += dy < 0 ? -(dy*dy)*dd : (dy*dy)*dd;
                        vertsBuf[mIdx][vIdx*3 + 2] += dz < 0 ? -(dz*dz)*dd : (dz*dz)*dd;
                    }
                }
            }
			timeWeight = 0.0f;
		}


	}
}
