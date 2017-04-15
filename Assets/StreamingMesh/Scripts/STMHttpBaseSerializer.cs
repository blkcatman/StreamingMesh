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
	public class AuthInfo {
		public string auth;
	}

	[ExecuteInEditMode]
	public class STMHttpBaseSerializer : MonoBehaviour {

		public string address = "http://127.0.0.1:8000/channels/";
		public string channel = "";
		[HideInInspector]
		public string authCode = "";

		public readonly Queue<Action> executeOnUpdate = new Queue<Action>();
		public readonly Queue<KeyValuePair<string, byte[]>> processBuffer = new Queue<KeyValuePair<string, byte[]>>();

		public readonly Queue<KeyValuePair<string, AudioClip>> processAudioBuffer = new Queue<KeyValuePair<string, AudioClip>>();

		bool waitResponse = false;

		bool isPlayApplication = false;

#if UNITY_EDITOR
		Thread thread;

		void OnEnable() {
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

		protected void Send(string query, byte[] data, bool isAuth) {
#if UNITY_EDITOR
			executeOnUpdate.Enqueue(() => {
				//_Send(query, data, true, isAuth);
				if(isPlayApplication) {
					StartCoroutine(Main_Send(query, data, true, isAuth));
				} else {
					Thread_Send(query, data, true, isAuth);
				}
			});
#endif
		}

		protected void Send(string query, string message, bool isAuth, bool isJson = false) {
#if UNITY_EDITOR
			/*
			executeOnUpdate.Enqueue(() => {
				byte[] data = Encoding.UTF8.GetBytes(message);
				_Send(query, data, false, isAuth, isJson);
			});
			*/

			executeOnUpdate.Enqueue(() => {
				byte[] data = Encoding.UTF8.GetBytes(message);
				if(isPlayApplication) {
					StartCoroutine(Main_Send(query, data, false, isAuth, isJson));
				} else {
					Thread_Send(query, data, false, isAuth, isJson);
				}
			});
#endif
        }
#if UNITY_EDITOR

    void Thread_Send(string query, byte[] data, bool isBinary, bool isAuth, bool isJson = false) {
			if(isAuth && authCode == "") {
				Debug.LogError("Authentication failed in initial sending!");
				return;
			}
			string addr = address + channel + "/?" +
				(isAuth ? "auth=" + this.authCode + "&" : "") + query;
			Debug.Log("SEND: " + addr);
			try {
				WebRequest req = WebRequest.Create(addr);
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
				if(!isAuth) {
					AuthInfo ai = JsonUtility.FromJson<AuthInfo>(val);
					this.authCode = ai.auth;
				}
				//Debug.Log(val);
				sr.Close();
				resStream.Close();
			} catch(WebException we) {
				Debug.LogError(we.Message);
				Debug.LogError(we.Data.ToString());
			}

			waitResponse = false;
		}

		IEnumerator Main_Send(string query, byte[] data, bool isBinary, bool isAuth, bool isJson = false) {
			if(isAuth && authCode == "") {
				Debug.LogError("Authentication failed in initial sending!");
				yield break;
			}
			string addr = address + channel + "/?" + 
				(isAuth ? "auth=" + this.authCode + "&" : "") + query;
			Debug.Log("SEND: " + addr);
			waitResponse = true;

			UnityWebRequest request = new UnityWebRequest(addr, "POST");
			request.downloadHandler = (DownloadHandler) new DownloadHandlerBuffer();
			request.uploadHandler = (UploadHandler) new UploadHandlerRaw(data);
			request.SetRequestHeader("Content-Type",  (isBinary ? "application/octet-stream" : "text/plain"));
			yield return request.Send();

			if (request.isNetworkError) {
				Debug.Log(request.error);
			}

			if (!isAuth && query.Contains("channel")) {
				AuthInfo ai = JsonUtility.FromJson<AuthInfo>(request.downloadHandler.text);
				this.authCode = ai.auth;
			}

			waitResponse = false;
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