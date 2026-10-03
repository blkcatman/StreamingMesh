using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net;

namespace StreamingMesh {

	[Serializable]
	public class ChannelTokenInfo {
		public string push_token;
	}

	[ExecuteInEditMode]
	public class STMHttpBaseSerializer : MonoBehaviour {

		public string address = "http://127.0.0.1:8000/channels/";
		public string channel = "";

		const string ProvisionTokenEnvironmentVariable = "STREAMINGMESH_PROVISION_TOKEN";
		[NonSerialized]
		string channelPushToken = "";

		public readonly Queue<Action> executeOnUpdate = new Queue<Action>();
		public readonly Queue<KeyValuePair<string, byte[]>> processBuffer = new Queue<KeyValuePair<string, byte[]>>();

		public readonly Queue<KeyValuePair<string, AudioClip>> processAudioBuffer = new Queue<KeyValuePair<string, AudioClip>>();

		bool waitResponse = false;

		bool isPlayApplication = false;

#if UNITY_EDITOR
		Thread thread;

		void OnEnable() {
				channelPushToken = "";
				thread = new Thread(ThreadUpdate);
				try {
						thread.Start();
				} catch (ThreadStartException ex) {
						Debug.LogError(ex.Source);
				}
		}

		void OnDisable() {
				if (thread != null) {
						thread.Abort();
				}
		}

		void Start() {
				isPlayApplication = Application.isPlaying;
		}

		void ThreadUpdate() {
			while(true) {
				Thread.Sleep(100);
				lock(executeOnUpdate) {
					if (executeOnUpdate.Count > 0 && waitResponse == false && !isPlayApplication) {
						executeOnUpdate.Dequeue().Invoke();
					}
				}
			}
		}
#endif

		protected virtual void OnReceivedFragment(int currentBytes, int ContentLength) {
		}

		protected void Request(string filename, bool isBinary, bool isAudio = false) {
				executeOnUpdate.Enqueue(() => {
					if(isAudio)
					{
						StartCoroutine(_Request(filename, isBinary, null, new Action<AudioClip>((outAudio) => {
							processAudioBuffer.Enqueue(new KeyValuePair<string, AudioClip>(filename, outAudio));
						})));
					}
					else
					{
						StartCoroutine(_Request(filename, isBinary, new Action<byte[]>((outBytes) => {
							processBuffer.Enqueue(new KeyValuePair<string, byte[]>(filename, outBytes));
						}), null));
					}
				});
		}

		IEnumerator _Request(string URL, bool isBinary, Action<byte[]> action, Action<AudioClip> audioAction) {
			string addr = URL;
			Debug.Log("REQ: " + addr);
			waitResponse = true;

			if(audioAction != null)
			{
				UnityWebRequest request;
				AudioType type = AudioType.UNKNOWN;
#if !UNITY_EDITOR && UNITY_IOS
        type = AudioType.AUDIOQUEUE;
#else
        type = AudioType.OGGVORBIS;
#endif
        /*
				string ext = Path.GetExtension(addr);
				if(ext == ".m4a") {
					type = AudioType.AUDIOQUEUE;
				} else if (ext == ".mp3") {
					type = AudioType.MPEG;
				} else if (ext == ".ogg") {
					type = AudioType.OGGVORBIS;
				}
        */
				request = UnityWebRequestMultimedia.GetAudioClip(addr, type);

				yield return request.Send();
				if (request.isNetworkError) {
					Debug.LogError(request.error);
				}
				if(request.responseCode == 200) {
					AudioClip audio = ((DownloadHandlerAudioClip)request.downloadHandler).audioClip;
					audioAction(audio);
				}
				waitResponse = false;
			} 
			if(action != null)
			{ 
				/*
				Dictionary<string, string> headers = new Dictionary<string, string>();
				headers.Add("Content-Type",  (isBinary ? "application/octet-stream" : "text/plain"));
				WWW request = new WWW(addr, null, headers );
				yield return request;
				if (request.error == null) {
					action(request.bytes);
				}
				*/
				//CustomDownloadHandler handler = new CustomDownloadHandler();
				//handler.OnReceived = OnReceivedFragment;
				UnityWebRequest request = new UnityWebRequest(addr, "GET");
				//request.downloadHandler = handler;
				request.downloadHandler = new DownloadHandlerBuffer();
				request.SetRequestHeader("Content-Type",  (isBinary ? "application/octet-stream" : "text/plain"));
				yield return request.Send();

				if (request.isNetworkError) {
					Debug.LogError(request.error);
				}
				if(request.responseCode == 200) {
					byte[] data = request.downloadHandler.data;
					//Debug.Log("Content-Length:" + request.GetResponseHeader("Content-Length"));
					//Debug.Log("DownloadSize:" + data.Length);
					if(data != null && data.Length > 0 && action != null) {
						if (isBinary) {
							action(data);
						} else {
							action(data);
						}
					}
				}
				waitResponse = false;
			}
		}

		protected void Send(string query, byte[] data, bool usePushToken) {
#if UNITY_EDITOR
			executeOnUpdate.Enqueue(() => {
				if(isPlayApplication) {
					StartCoroutine(Main_Send(query, data, true, usePushToken));
				} else {
					Thread_Send(query, data, true, usePushToken);
				}
			});
#endif
		}

		protected void Send(string query, string message, bool usePushToken, bool isJson = false) {
#if UNITY_EDITOR
			executeOnUpdate.Enqueue(() => {
				byte[] data = Encoding.UTF8.GetBytes(message);
				if(isPlayApplication) {
					StartCoroutine(Main_Send(query, data, false, usePushToken, isJson));
				} else {
					Thread_Send(query, data, false, usePushToken, isJson);
				}
			});
#endif
        }
#if UNITY_EDITOR

		string GetRequestToken(bool usePushToken) {
			if (usePushToken) return channelPushToken;
			string token = Environment.GetEnvironmentVariable(ProvisionTokenEnvironmentVariable);
			if (!String.IsNullOrEmpty(token)) return token;
			// The local development server token is gitignored. This also works when
			// Unity was launched from Hub without inheriting the shell environment.
			string tokenPath = Path.Combine(Directory.GetCurrentDirectory(), "DevData/provision-token");
			return File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : "";
		}

		void ReadChannelPushToken(string json) {
			ChannelTokenInfo tokenInfo = JsonUtility.FromJson<ChannelTokenInfo>(json);
			if (tokenInfo == null || String.IsNullOrEmpty(tokenInfo.push_token)) {
				Debug.LogError("Channel creation did not return a push token.");
				return;
			}
			channelPushToken = tokenInfo.push_token;
		}

		void Thread_Send(string query, byte[] data, bool isBinary, bool usePushToken, bool isJson = false) {
			string requestToken = GetRequestToken(usePushToken);
			if(usePushToken && String.IsNullOrEmpty(requestToken)) {
				Debug.LogError("Channel push token is unavailable; create the channel first.");
				return;
			}
			string addr = address + channel + "/?" + query;
			Debug.Log("SEND: " + addr);
			try {
				WebRequest req = WebRequest.Create(addr);
				if (!String.IsNullOrEmpty(requestToken)) {
					req.Headers[HttpRequestHeader.Authorization] = "Bearer " + requestToken;
				}
				if (isJson) {
						req.ContentType = "application/json";
				} else {
						req.ContentType = isBinary ? "application/octet-stream" : "text/plain";
				}
				req.Method = "POST";
				req.ContentLength = data.Length;
				waitResponse = true;
				req.Timeout = 10000;

				Stream reqStream = req.GetRequestStream();
				reqStream.Write(data, 0, data.Length);
				reqStream.Close();

				WebResponse res = req.GetResponse();
				Stream resStream = res.GetResponseStream();
				StreamReader sr = new StreamReader(resStream);
				string val = sr.ReadToEnd();
				if(!usePushToken && query.StartsWith("channel=", StringComparison.Ordinal)) {
					ReadChannelPushToken(val);
				}
				sr.Close();
				resStream.Close();
			} catch(WebException we) {
				Debug.LogError(we.Message);
				Debug.LogError(we.Data.ToString());
			}

			waitResponse = false;
		}

        IEnumerator Main_Send(string query, byte[] data, bool isBinary, bool usePushToken, bool isJson = false) {
            string token = GetRequestToken(usePushToken);
            if (usePushToken && String.IsNullOrEmpty(token)) {
                Debug.LogError("Channel push token is unavailable; create the channel first.");
                yield break;
            }
            waitResponse = true;
            try {
                for (int attempt = 0; attempt < 3; attempt++) {
                    using (var request = new UnityWebRequest(address + channel + "/?" + query, "POST")) {
                        request.downloadHandler = new DownloadHandlerBuffer();
                        request.uploadHandler = new UploadHandlerRaw(data);
                        request.timeout = 30;
                        request.SetRequestHeader("Content-Type", isJson ? "application/json" : isBinary ? "application/octet-stream" : "text/plain");
                        if (!String.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);
                        yield return request.SendWebRequest();
                        if (request.result == UnityWebRequest.Result.Success && request.responseCode == 200) {
                            if (!usePushToken && query.StartsWith("channel=", StringComparison.Ordinal))
                                ReadChannelPushToken(request.downloadHandler.text);
                            yield break;
                        }
                        if (attempt == 2) Debug.LogError("StreamingMesh upload failed: " + query + ": " + request.error);
                    }
                    yield return new WaitForSecondsRealtime(0.25f * (attempt + 1));
                }
                GetComponent<STMHttpSender>()?.Stop();
                executeOnUpdate.Clear();
            } finally { waitResponse = false; }
        }

#endif
		protected virtual void ProcessRequestedData(KeyValuePair<string, byte[]> pair) {
		}

		protected virtual void ProcessRequestedData(KeyValuePair<string, AudioClip> pair) {
		}

		void OnValidate() {
			if (!address.EndsWith("/")) {
				address = address + "/";
			}
			if (channel.Length == 0) {
					System.Random random = new System.Random();
					const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
					channel = "channel_" + new string(Enumerable.Repeat(chars, 8)
						.Select(s => s[random.Next(s.Length)]).ToArray());
			}
		}

		void Update() {
#if UNITY_EDITOR
			if(Application.isPlaying) {
			if(executeOnUpdate.Count > 0 && waitResponse == false) {
					executeOnUpdate.Dequeue().Invoke();
				}
			}
#else
			if(executeOnUpdate.Count > 0 && waitResponse == false) {
				executeOnUpdate.Dequeue().Invoke();
			}
#endif
			if (processBuffer.Count > 0) {
				ProcessRequestedData(processBuffer.Dequeue());
			}
			if (processAudioBuffer.Count > 0) {
				ProcessRequestedData(processAudioBuffer.Dequeue());
			}
		}

		public static void CopyTo(Stream input, Stream output) {
			byte[] buffer = new byte[16 * 1024]; // Fairly arbitrary size
			int bytesRead;

			while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
			{
				output.Write(buffer, 0, bytesRead);
			}
		}

    }

}
