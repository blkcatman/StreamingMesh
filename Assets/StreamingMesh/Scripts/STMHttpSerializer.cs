using System;
using System.IO;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using StreamingMesh.Core.Serialization;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace StreamingMesh {
	public class STMHttpSerializer : STMHttpBaseSerializer {

		public bool useLocalFiles = true;

        ResourceIdentityRegistry resourceIdentities = new ResourceIdentityRegistry();
        public void BeginResourceSnapshot() { resourceIdentities = new ResourceIdentityRegistry(); }

		public ChannelInfo CreateChannelInfo(
			int containerSize,
			int packageSize,
			float frameInterval,
			int comvinedFrames,
			List<string> meshNames,
			List<string> materialNames,
			List<string> textureNames,
			List<int> meshSizes,
			List<int> materialSizes,
			List<int> textureSizes,
            List<string> textureDisplayNames
) {
#if UNITY_EDITOR
			ChannelInfo channelInfo = new ChannelInfo {
				protocolVersion = ChannelInfo.CurrentVersion,
				timebaseHz = TimeSpan.TicksPerSecond,
				containerSize = containerSize,
				packageSize = packageSize,
				frameInterval = frameInterval,
				combinedFrames = comvinedFrames,
				meshes = meshNames,
				materials = materialNames,
				textures = textureNames,
                textureNames = textureDisplayNames,
				meshSizes = meshSizes,
				materialSizes = materialSizes,
				textureSizes = textureSizes,
                initialData = new List<InitialDataPart>(),
				streamInfo = "stream.stmj",
				audioInfo = "stream.stma",
				audioFormat = "fmp4",
				audioMimeType = "audio/mp4",
				audioCodec = "mp4a.40.2",
				audioTimescale = STMAudioRecorder.EncodedSampleRate,
				audioSampleRate = STMAudioRecorder.EncodedSampleRate,
				audioChannels = 2,
				audioInit = "audio-init.mp4",
				audioPlaylist = "audio.m3u8"
			};
			return channelInfo;
#else
			return null;
#endif
		}

		public MeshInfo CreateMeshInfo(Renderer renderer, Mesh source) {
#if UNITY_EDITOR
			if (source == null) return null;
			Mesh snapshot = null;
			try {
				// Imported meshes can release their CPU data in Play mode. BakeMesh
				// supplies readable topology and UVs without changing import settings.
				Mesh mesh = source;
				if (!source.isReadable && renderer is SkinnedMeshRenderer skinned) {
					snapshot = new Mesh();
					skinned.BakeMesh(snapshot);
					mesh = snapshot;
				}
				if (!mesh.isReadable)
					throw new InvalidOperationException("StreamingMesh requires Read/Write Enabled on a MeshFilter mesh: " + source.name);
				MeshInfo meshInfo = new MeshInfo() {
					name = source.name,
					vertexCount = mesh.vertexCount,
					subMeshCount = mesh.subMeshCount,
					uv = mesh.uv,
					uv2 = mesh.uv2,
					uv3 = mesh.uv3,
					uv4 = mesh.uv4,
					materialIds = (from m in renderer.sharedMaterials select resourceIdentities.GetId(m)).ToList(),
					indicesCounts = new List<int>(),
					indices = new List<int>()
				};
				for (int i = 0; i < mesh.subMeshCount; i++) {
					if (mesh.GetTopology(i) != MeshTopology.Triangles)
						throw new InvalidOperationException("StreamingMesh requires triangle topology: " + source.name);
					int[] indices = mesh.GetIndices(i);
					meshInfo.indicesCounts.Add(indices.Length);
					meshInfo.indices.AddRange(indices);
				}
				if (meshInfo.vertexCount != source.vertexCount || meshInfo.indices.Count == 0)
					throw new InvalidOperationException("StreamingMesh could not read mesh topology: " + source.name);
				return meshInfo;
			}
			finally {
				if (snapshot != null) {
					if (Application.isPlaying) UnityEngine.Object.Destroy(snapshot);
					else UnityEngine.Object.DestroyImmediate(snapshot);
				}
			}
#else
			return null;
#endif
		}

		public List<KeyValuePair<string, Texture>> GetTexturesFromMaterial(Material material) {
#if UNITY_EDITOR
			List<KeyValuePair<string, Texture>> textures = new List<KeyValuePair<string, Texture>>();
			Shader shader = material.shader;
			if(shader == null) {
				return null;
			}
			int propertyCount = ShaderUtil.GetPropertyCount(shader);
			for(int i = 0; i < propertyCount; i++) {
				string propertyName = ShaderUtil.GetPropertyName(shader, i);
				ShaderUtil.ShaderPropertyType propertyType = ShaderUtil.GetPropertyType(shader, i);
				if(propertyType == ShaderUtil.ShaderPropertyType.TexEnv) {
					Texture tex = material.GetTexture(propertyName);
					if(tex != null) {
						textures.Add(new KeyValuePair<string, Texture>(resourceIdentities.GetId(tex), tex));
					}
                }
			}
			return textures;
#else
			return null;
#endif
		}

        public MaterialInfo CreateMaterialInfo(Material material) {
#if UNITY_EDITOR
            return MaterialConverter.Serialize(material, resourceIdentities);
#else
            return null;
#endif
        }
        // Kept for source compatibility; exporting never changes TextureImporter settings.
        public static byte[] GetTextureToPNGByteArray(Texture texture, bool isReimportTexture) {
            return TextureConverter.SerializeToPNG(texture);
        }
		public StreamInfo CreateStreamInfo(
			long tickCnt,
			long startTicks,
			long endTicks,
			uint firstSequence = 0,
			uint lastSequence = 0) {
#if UNITY_EDITOR
			StreamInfo streamInfo = new StreamInfo() {
				video = tickCnt.ToString("000000") + ".stmv",
				startTicks = startTicks,
				endTicks = endTicks,
				firstSequence = firstSequence,
				lastSequence = lastSequence,
			};
			return streamInfo;
#else
			return null;
#endif
		}

		public AudioInfo CreateAudioInfo(string fileName, long startSample, int sampleCount) {
#if UNITY_EDITOR
			long startTicks = startSample * TimeSpan.TicksPerSecond / STMAudioRecorder.EncodedSampleRate;
			long endTicks = (startSample + sampleCount) * TimeSpan.TicksPerSecond / STMAudioRecorder.EncodedSampleRate;
			AudioInfo audioInfo = new AudioInfo() {
				audio = fileName,
				startTicks = startTicks,
				endTicks = endTicks,
				startSample = startSample,
				sampleCount = sampleCount,
				discontinuity = false
			};
			return audioInfo;
#else
			return null;
#endif
		}

		public delegate void ChannelInfoReceived(string name, ChannelInfo info);
		//public delegate void MeshInfoReceived(string name, MeshInfo info);
		//public delegate void MaterialInfoReceived(string name, MaterialInfo info);
		//public delegate void TextureReceived(string name, Texture2D texture);
		public delegate void CombinedDataReceived(string name, byte[] data);
		public delegate void StreamListReceived(string name, string list);
		public delegate void StreamReceived(string name, byte[] data);
		public delegate void AudioListReceived(string name, string list);
		public delegate void AudioReceived(string name, AudioClip audio);
		public delegate void ReceivedFragmentData(int currentBytes, int ContentLength);

		public ChannelInfoReceived OnChannelInfoReceived;
		//public MeshInfoReceived OnMeshInfoReceived;
		//public MaterialInfoReceived OnMaterialInfoReceived;
		//public TextureReceived OnTextureReceived;
		public CombinedDataReceived OnCombinedDataReceived;
		public StreamListReceived OnStreamListReceived;
		public StreamReceived OnStreamReceived;

		public AudioListReceived OnAudioListReceived;
		public AudioReceived OnAudioReceived;

		public ReceivedFragmentData OnReceivedFragmentData;

		protected override void OnReceivedFragment(int currentBytes, int contentLength) {
			if(OnReceivedFragmentData != null) {
				OnReceivedFragmentData(currentBytes, contentLength);
			}
		}

		protected override void ProcessRequestedData(KeyValuePair<string, byte[]> pair) {
			string fileName = pair.Key;
			byte[] data = pair.Value;
			string name = Path.GetFileNameWithoutExtension(fileName);
			string ext = Path.GetExtension(fileName);
			if (ext.Contains("stmv")) {
				if (OnStreamReceived != null) {
					OnStreamReceived(name, data);
				}
			} else if (ext.Contains("stma")) {
				if(OnAudioListReceived != null) {
					string listData = Encoding.UTF8.GetString(data);
					OnAudioListReceived(name, listData);
				}
			} else if (ext.Contains("stmj")) {
				string listData = Encoding.UTF8.GetString(data);
				if (OnStreamListReceived != null) {
					OnStreamListReceived(name, listData);
				}
			} else if(ext.Contains("json")) {
				string json = Encoding.UTF8.GetString(data);
				if (name.Contains("stream")) {
					if (OnChannelInfoReceived != null) {
						OnChannelInfoReceived(name, JsonUtility.FromJson<ChannelInfo>(json));
					}
				/* 
				} else if (name.Contains("mesh")) {
					if (OnMeshInfoReceived != null) {
						OnMeshInfoReceived(name, JsonUtility.FromJson<MeshInfo>(json));
					}
				} else if (name.Contains("material")) {
					if(OnMaterialInfoReceived != null) {
						OnMaterialInfoReceived(name, JsonUtility.FromJson<MaterialInfo>(json));
					}
				*/
				}
			/*
			} else if (ext.Contains("png")) {
				if (OnTextureReceived != null) {
					Texture2D texture = new Texture2D(2, 2);
					texture.LoadImage(data);
					OnTextureReceived(name, texture);
				}
			*/
			} else if (ext.Contains("bin")) {
				if (OnCombinedDataReceived != null) {
					OnCombinedDataReceived(name, data);
				}
			}
		}

		protected override void ProcessRequestedData(KeyValuePair<string, AudioClip> pair) {
			string fileName = pair.Key;
			AudioClip audio = pair.Value;
			string name = Path.GetFileNameWithoutExtension(fileName);
			if(OnAudioReceived != null) {
				OnAudioReceived(name, audio);
			}
		}

		public void Request(string fileName) {
			string ext = Path.GetExtension(fileName);
			if (ext.Contains("stmv")) {
				base.Request(base.address + base.channel + "/" + fileName, true);
			} else if (ext.Contains("stmj")) {
				base.Request(base.address + base.channel + "/" + fileName, false);
			} else if (ext.Contains("stma")) {
				base.Request(base.address + base.channel + "/" + fileName, false);
			} else if(ext.Contains("json")) {
				base.Request(base.address + base.channel + "/" + fileName, false);
			/*
			} else if (ext.Contains("png")) {
				base.Request(fileName, true);
			}*/
			} else if (ext.Contains("bin")) {
				base.Request(base.address + base.channel + "/" + fileName, true);
			} else { // audio files
#if !UNITY_EDITOR && UNITY_IOS
        ext = ".m4a";
#else
        ext = ".ogg";
#endif
				base.Request(base.address + base.channel + "/" + fileName + ext, true, true);
			}
		}

		public void Send(ChannelInfo channelInfo) {
			string json = JsonUtility.ToJson(channelInfo);
			base.Send("channel=" + base.channel, json, false);
		}

        public void PublishInitialData(ChannelInfo channelInfo) {
            base.Send("initialinfo=stream.json", JsonUtility.ToJson(channelInfo), true, true);
        }

    /*
		public void Send(MeshInfo meshInfo, int index) {
			string json = JsonUtility.ToJson(meshInfo);
			base.Send("mesh=" + index.ToString(), json, true);
		}

		public void Send(MaterialInfo materialInfo, int index) {
			string json = JsonUtility.ToJson(materialInfo);
			base.Send("material=" + index.ToString(), json, true);
		}

		public void Send(Texture texture, bool isReimportTexture) {
			byte[] binary = GetTextureToPNGByteArray(texture, isReimportTexture);
			base.Send("texture=" + texture.name + ".png", binary, true);
		}
    */

		public void Send(StreamInfo streamInfo, long ticks) {
			string json = JsonUtility.ToJson(streamInfo);
			base.Send("streaminfo=" + ticks.ToString("000000"), json, true, true);
		}

		public void Send(AudioInfo audioInfo, long ticks) {
			string json = JsonUtility.ToJson(audioInfo);
			base.Send("audioinfo=" + ticks.ToString("000000"), json, true, true);
		}

		public void SendAudioInit(byte[] data, string fileName) {
			base.Send("audioinit=" + fileName, data, true);
		}

		public void Send(byte[] stream, string query, string fileName) {
			base.Send(query + "=" + fileName, stream, true);
		}

		public void Send(byte[] combinedBuffer) {
			base.Send("combined=" + "stream.bin", combinedBuffer, true);
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
	}

}
