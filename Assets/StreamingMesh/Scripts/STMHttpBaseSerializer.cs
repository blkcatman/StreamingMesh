// STMHttpBaseSerializer.cs
//
//Copyright (c) 2017 Tatsuro Matsubara.
//Creative Commons License
//This file is licensed under a Creative Commons Attribution-ShareAlike 4.0 International License.
//https://creativecommons.org/licenses/by-sa/4.0/
//

using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;

namespace StreamingMesh {

	[Serializable]
	public class AuthInfo {
		public string auth;
	}

	[ExecuteInEditMode]
    public class STMHttpBaseSerializer : MonoBehaviour {

        public string address = "http://127.0.0.1:8080/channels/";
        public string channel = "";
        [HideInInspector]
        public string authCode = "";

        public readonly Queue<Action> executeOnUpdate = new Queue<Action>();
		public readonly Queue<KeyValuePair<string, byte[]>> requestBuffer = new Queue<KeyValuePair<string, byte[]>>();
		bool waitResponse = false;

		protected void Request(string filename, bool isBinary) {
			StartCoroutine(_Request(filename, isBinary, new Action<byte[]>((outBytes) =>
				executeOnUpdate.Enqueue(() => {
					requestBuffer.Enqueue(new KeyValuePair<string, byte[]>(filename, outBytes));
				})
			)));
		}

		IEnumerator _Request(string URL, bool isBinary, Action<byte[]> action) {
			string addr = URL;
			Debug.Log("REQ: " + addr);

			waitResponse = true;
			Dictionary<string, string> headers = new Dictionary<string, string>();
			headers.Add("Content-Type",  (isBinary ? "application/octet-stream" : "text/plain"));
			WWW request = new WWW(addr, null, headers );
			yield return request;
			if (request.error == null) {
				action(request.bytes);
			}

			/*
			UnityWebRequest request = new UnityWebRequest(addr, "GET");
			request.downloadHandler = (DownloadHandler) new DownloadHandlerBuffer();
			request.SetRequestHeader("Content-Type",  (isBinary ? "application/octet-stream" : "text/plain"));
			yield return request.Send();

			if (request.isError) {
				Debug.Log(request.error);
			}
			if(request.responseCode == 200) {
				if(request.downloadHandler.data != null && action != null) {
					if (isBinary) {
						action(request.downloadHandler.data);
					} else {
						Debug.Log(request.downloadHandler.text);
						byte[] buf = Encoding.UTF8.GetBytes(request.downloadHandler.text);
						action(buf);
					}
				}
			}
			*/
			waitResponse = false;
		}

		protected void Send(string query, byte[] data, bool isAuth) {
			executeOnUpdate.Enqueue(() => {
				StartCoroutine(_Send(query, data, true, isAuth));
			});
		}

		protected void Send(string query, string message, bool isAuth) {
			executeOnUpdate.Enqueue(() => {
				byte[] data = Encoding.UTF8.GetBytes(message);
				StartCoroutine(_Send(query, data, false, isAuth));
			});
		}

		IEnumerator _Send(string query, byte[] data, bool isBinary, bool isAuth) {
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

			if (request.isError) {
				Debug.Log(request.error);
			}

			if (!isAuth && query.Contains("channel")) {
				AuthInfo ai = JsonUtility.FromJson<AuthInfo>(request.downloadHandler.text);
				this.authCode = ai.auth;
				Debug.Log("AUTH: " + this.authCode);
			}

			waitResponse = false;
		}

		protected virtual void ProcessRequestedData(KeyValuePair<string, byte[]> pair) {
		}

		void OnValidate() {
            if (channel.Length == 0) {
                System.Random random = new System.Random();
                const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
                channel = "channel_" + new string(Enumerable.Repeat(chars, 8)
                  .Select(s => s[random.Next(s.Length)]).ToArray());
            }
        }

		float process_speed = 0f;

		void Update() {
			process_speed += Time.deltaTime;

			if(executeOnUpdate.Count > 0 && waitResponse == false) {
				executeOnUpdate.Dequeue().Invoke();
			}
			if (requestBuffer.Count > 0) {
				//Debug.Log("CALL time: " + process_speed);
				//process_speed = 0f;
				ProcessRequestedData(requestBuffer.Dequeue());
			}
		}

		public static byte[] Compress(byte[] data) {
			using (MemoryStream rStream = new MemoryStream()) {
				using (GZipStream gStream = new GZipStream(rStream, CompressionMode.Compress, true)) {
					new BinaryFormatter().Serialize(gStream, data);
				}
				return rStream.ToArray();
			} 
		}

		public static byte[] Decompress(byte[] data) {
			using (MemoryStream rStream = new MemoryStream(data)) {
				using (GZipStream gStream = new GZipStream(rStream, CompressionMode.Decompress, true)) {
					return (byte[])new BinaryFormatter().Deserialize(gStream);
				}
			}
		}
    }

}