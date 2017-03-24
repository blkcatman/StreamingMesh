// SampleHTTPServer.cs
//
//Copyright (c) 2017 Tatsuro Matsubara.
//Creative Commons License
//This file is licensed under a Creative Commons Attribution-ShareAlike 4.0 International License.
//https://creativecommons.org/licenses/by-sa/4.0/
//

using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.IO;
using System.Net;
using System.Text;
#if UNITY_EDITOR
using System.Net.Mime;
using System.Threading;
using UnityEditor.VersionControl;
#endif

[ExecuteInEditMode]
public class SampleHTTPServer : MonoBehaviour {
#if UNITY_EDITOR
    static Dictionary<string, string> errors = new Dictionary<string, string> {
        {"400", "{\"error\":\"Bad request\"}"},
        {"404", "{\"error\":\"The channel is not found\"}"},
        {"exist", "{\"error\":\"The channel is already exist\"}"},
        {"authfail", "{\"error\":\"Authentication failed\"}"}
    };
    public readonly static Queue<Action> excuteOnUpdate = new Queue<Action>();

	public string url = "http://127.0.0.1:8080/";
	public bool lastMinOnly = false;

	Thread thread;
	HttpListener listener;

	bool initStream = false;

    [HideInInspector]
    public List<string> channels = new List<string>();
    [HideInInspector]
    public List<string> auths = new List<string>();

    void Awake() {
		if (!Directory.Exists("channels")) {
			Directory.CreateDirectory("channels");
		}

        StartServer();
    }

    void Start() {
        if (channels.Count != 0) {
            for (int i = 0; i < channels.Count; i++) {
                Debug.Log("channel:" + channels[i] + " auth:" + auths[i]);
            }
        } else {
            Debug.LogWarning("No channel on Play Mode!");
        }
		initStream = false;
    }

    void OnDestroy() {
        CleanupServer();
    }

    public void ResetChannels() {
        if (channels != null) {
			string[] localChannels = Directory.GetDirectories("channels");
			foreach (string channel in localChannels) {
				Debug.Log(channel);
				if (Directory.Exists(channel)) {
					Debug.Log("DELETE: " + channel);
					Directory.Delete(channel, true);
                }
            }
            channels.Clear();
            auths.Clear();
            Debug.Log("Local HTTP Channels clear");
        }
		CleanupServer();
		StartServer();
    }

    public void CleanupServer() {
        if (listener != null) {
            Debug.Log("Local HTTP Listner stop");
            listener.Stop();
        }

        if (thread != null) {
            Debug.Log("Local HTTP Thread stop");
            thread.Abort();
        }
    }

    public void StartServer() {
        thread = new Thread(ThreadUpdate);

        listener = new HttpListener();
        listener.Prefixes.Add(url);

        try {
            thread.Start();
            Debug.Log("Local HTTP Thread start");
        } catch (ThreadStartException ex) {
            Debug.LogError(ex.Source);
        }

        try {
            listener.Start();
            Debug.Log("Local HTTP Listner start");
        } catch (HttpListenerException ex) {
            Debug.LogError(ex.Source);
        }

        listener.BeginGetContext(new AsyncCallback(OnRequested), this.listener);
    }

	public void OnRequested(IAsyncResult result) {
        HttpListener ls = (HttpListener)result.AsyncState;

        HttpListenerContext ctx = ls.EndGetContext(result);
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse res = ctx.Response;
        if (req.HttpMethod == "GET") {
            excuteOnUpdate.Enqueue(() => {
                OnGet(req, res);
            });
        }

        if (req.HttpMethod == "POST") {
			excuteOnUpdate.Enqueue(() => {
                OnPost(req, res);
            });
        }

        listener.BeginGetContext(new AsyncCallback(OnRequested), this.listener);
    }

    void ThreadUpdate() {
        while (true) {
            Thread.Sleep(0);
            lock (excuteOnUpdate) {
				if(excuteOnUpdate.Count > 0) {
					excuteOnUpdate.Dequeue().Invoke();
				}
			}
       }
    }

    void OnGet(HttpListenerRequest req, HttpListenerResponse res) {
        string[] path = ParseURLToPath(req.RawUrl);
		if ("channels" != path[0]) {
            OnError(req, res, "400");
            return;
        }
		string filePath = "";
#if UNITY_EDITOR_WIN
		filePath = req.RawUrl.Substring(1).Replace("/", "\\");
#else
		filePath = req.RawUrl.Substring(1);
		/*
		Uri uri = new Uri(req.RawUrl);
		Debug.Log(uri.AbsolutePath);
		filePath = uri.LocalPath.Substring(1) + Uri.UnescapeDataString(uri.Fragment);
		*/
#endif
		if(File.Exists(filePath)) {
			string ext = Path.GetExtension(filePath);
			if (ext == ".png" || ext == ".stmv") {
				byte[] binary = File.ReadAllBytes(filePath);

				res.StatusCode = (int)HttpStatusCode.OK;
				res.ContentType = MediaTypeNames.Application.Octet;
				SendBinary(binary, res);
			} else {
				StreamReader sr = new StreamReader(filePath);
				string outputText = sr.ReadToEnd();
				sr.Close();

				res.StatusCode = (int)HttpStatusCode.OK;
				res.ContentType = MediaTypeNames.Text.Plain;
				SendText(outputText, res);
			}
		} else {
			OnError(req, res, "400");
			return;
		}
    }

    void OnGetData(HttpListenerRequest req, HttpListenerResponse res) {
        //StreamReader sr = new StreamReader(req.InputStream);
        //string received = sr.ReadToEnd();
        //sr.Close();

        string outString = "ok";
        res.StatusCode = (int)HttpStatusCode.OK;
        res.ContentType = MediaTypeNames.Text.Plain;

        SendText(outString, res);
    }

    void OnPost(HttpListenerRequest req, HttpListenerResponse res) {
        string[] path = ParseURLToPath(req.RawUrl);
        if (path[0] != "channels" || path[1] == null) {
            OnError(req, res, "400");
            return;
        }

        NameValueCollection queries = req.QueryString;
        if(queries.Count == 0) {
            OnError(req, res, "400");
            return;
        }
        if(queries["auth"] != null) {
            if (channels.Contains(path[1])) {
				int index = 0;
				string channel = path[1];
                string authCode = auths[channels.IndexOf(path[1])];

                if (queries["auth"] == authCode && queries["streaminfo"] != null) {
					//OnPostStreamInfo(req, res, channel, queries["streaminfo"]);
					OnPostData<string>(req, res, path[1], new Func<string, bool>((data) => {
						if (Directory.Exists(Path.Combine("channels", channel))) {
							StreamingMesh.StreamInfo streamInfo = JsonUtility.FromJson<StreamingMesh.StreamInfo>(data);
							streamInfo.name = url + "channels/" + channel + "/" + streamInfo.name;

							string stmjPath = Path.Combine(Path.Combine("channels", channel), "streaminfo.stmj");
							if(initStream == false) {
								if(File.Exists(stmjPath)){
									File.Delete(stmjPath);
								}
								initStream = true;
								FileStream fs = File.Create(stmjPath);
								fs.Close();
							}
							File.AppendAllText(stmjPath, JsonUtility.ToJson(streamInfo).Replace(Environment.NewLine, "") + "\n");
							if(lastMinOnly) {
								List<string> lines = new List<string>(File.ReadAllLines(stmjPath));
								while(lines.Count > 6) {
									lines.RemoveAt(0);
								}
								File.WriteAllLines(stmjPath, lines.ToArray());
							}
							return true;
						}
						return false;
					}));
                    return;
                }

                if (queries["auth"] == authCode && queries["stream"] != null) {
					//OnPostStream(req, res, channel, queries["stream"]);
					OnPostData<byte[]>(req, res, channel, new Func<byte[], bool>((data) => {
						if (Directory.Exists(Path.Combine("channels", path[1]))) {
							FileWrite<byte[]>("channels", path[1], queries["stream"], data);
							return true;
						}
						return false;
					}));
                    return;
                }

                if (queries["auth"] == authCode && int.TryParse(queries["mesh"], out index)) {
                    OnPostMeshInfo(req, res, path[1], index);
                    return;
                }

                if (queries["auth"] == authCode && int.TryParse(queries["material"], out index)) {
                    OnPostMaterialInfo(req, res, path[1], index);
                    return;
                }

                if (queries["auth"] == authCode && queries["texture"] != null) {
                    OnPostTexture(req, res, path[1], queries["texture"]);
                    return;
                }

            }
			OnError(req, res, "authfail");
            return;
        }

        if(queries["channel"] != null) {
            OnPostChannelInfo(req, res, path[1]);
            return;
        }

        OnError(req, res, "400");
    }
		
	void OnPostData<T>(HttpListenerRequest req, HttpListenerResponse res, string channel, Func<T, bool> action) {
		if (!channels.Contains(channel)) {
			OnError(req, res, "404");
			return;
		}

		List<byte> binary = new List<byte>();
		byte[] buf = new byte[1024 * 1024 * 5];
		int bytesRead = 0;
		try {
			while ((bytesRead = req.InputStream.Read(buf, 0, buf.Length)) > 0) {
				byte[] frag = new byte[bytesRead];
				System.Array.Copy(buf, frag, bytesRead);
				binary.AddRange(frag);
			}
		} catch (Exception ex) {
			if (ex.InnerException != null) {
				Debug.LogWarning(ex.InnerException.ToString());
			}
			OnError(req, res, "400");
			return;
		}

		Type type = typeof(T);
		T data = default(T);
		if (typeof(string) == type) {
			data = (T)Convert.ChangeType(Encoding.UTF8.GetString(binary.ToArray()), typeof(T));
		} else if (typeof(byte).IsAssignableFrom(type.GetElementType()) && type.IsArray) {
			data = (T)Convert.ChangeType(binary.ToArray(), typeof(T));
		}
			
		if (!action(data)) {
			OnError(req, res, "400");
			return;
		}

		string outString = "{\"stat\":\"ok\"}";
		res.StatusCode = (int)HttpStatusCode.OK;
		res.ContentType = MediaTypeNames.Text.Plain;

		SendText(outString, res);
	}

	void OnPostChannelInfo(HttpListenerRequest req, HttpListenerResponse res, string channel) {
		if(channels.Contains(channel)) {
			OnError(req, res, "exist");
			return;
		}

		string inString = GetText(req);
		if(inString == null) {
			OnError(req, res, "400");
			return;
		}

		StreamingMesh.ChannelInfo channelInfo = JsonUtility.FromJson<StreamingMesh.ChannelInfo>(inString);
		List<string> meshUrls = new List<string>(channelInfo.meshes);
		List<string> materialUrls = new List<string>(channelInfo.materials);
		List<string> textureUrls = new List<string>(channelInfo.textures);
		for(int i = 0; i < meshUrls.Count; i++) {
			meshUrls[i] = url + "channels/" + channel + "/" + meshUrls[i];
        }
		for(int i = 0; i < materialUrls.Count; i++) {
			materialUrls[i] = url + "channels/" + channel + "/" + materialUrls[i];
        }
		for(int i = 0; i < textureUrls.Count; i++) {
			textureUrls[i] = url + "channels/" + channel + "/" + textureUrls[i];
        }
		channelInfo.meshes = meshUrls;
		channelInfo.materials = materialUrls;
		channelInfo.textures = textureUrls;
		channelInfo.stream_info = url + "channels/" + channel + "/streaminfo.stmj";
		Debug.Log(JsonUtility.ToJson(channelInfo));
		FileWrite<string>("channels", channel, "stream.json", JsonUtility.ToJson(channelInfo));

        System.Random random = new System.Random();
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        string auth = new string(Enumerable.Repeat(chars, 8)
          .Select(s => s[random.Next(s.Length)]).ToArray());
        channels.Add(channel);
        auths.Add(auth);

        string outString = "{\"auth\":\"" + auth + "\"}";
        res.StatusCode = (int)HttpStatusCode.OK;
        res.ContentType = MediaTypeNames.Text.Plain;

        SendText(outString, res);
    }

	void OnPostMeshInfo(HttpListenerRequest req, HttpListenerResponse res, string channel, int index) {
        if (!channels.Contains(channel)) {
            OnError(req, res, "404");
            return;
        }

		string inString = GetText(req);
		if(inString == null) {
			OnError(req, res, "400");
			return;
		}

		if(Directory.Exists(Path.Combine("channels", channel))) {
			FileWrite<string>("channels", channel, "mesh" + index + ".json", inString);
        } else {
            OnError(req, res, "400");
            return;
        }

		string outString = "{\"stat\":\"ok\"}";
		res.StatusCode = (int)HttpStatusCode.OK;
		res.ContentType = MediaTypeNames.Text.Plain;

		SendText(outString, res);
	}

    void OnPostMaterialInfo(HttpListenerRequest req, HttpListenerResponse res, string channel, int index) {
        if (!channels.Contains(channel)) {
            OnError(req, res, "404");
            return;
        }

        string inString = GetText(req);
        if (inString == null) {
            OnError(req, res, "400");
            return;
        }

		if (Directory.Exists(Path.Combine("channels", channel))) {
			FileWrite<string>("channels", channel, "material" + index + ".json", inString);
        } else {
            OnError(req, res, "400");
            return;
        }

        string outString = "{\"stat\":\"ok\"}";
        res.StatusCode = (int)HttpStatusCode.OK;
        res.ContentType = MediaTypeNames.Text.Plain;

        SendText(outString, res);
    }

    void OnPostTexture(HttpListenerRequest req, HttpListenerResponse res, string channel, string textureName) {
        if (!channels.Contains(channel)) {
            OnError(req, res, "404");
            return;
        }

        byte[] binary = GetBinary(req);
        if (binary == null) {
            OnError(req, res, "400");
            return;
        }

		if (Directory.Exists(Path.Combine("channels", channel))) {
			FileWrite<byte[]>("channels", channel, textureName, binary);
        } else {
            OnError(req, res, "400");
            return;
        }

        string outString = "{\"stat\":\"ok\"}";
        res.StatusCode = (int)HttpStatusCode.OK;
        res.ContentType = MediaTypeNames.Text.Plain;

        SendText(outString, res);
    }

    void OnPostStreamInfo(HttpListenerRequest req, HttpListenerResponse res, string channel, string streamName) {
        if (!channels.Contains(channel)) {
            OnError(req, res, "404");
            return;
        }

		string inString = GetText(req);
		if (inString == null) {
            OnError(req, res, "400");
            return;
        }
		StreamingMesh.StreamInfo streamInfo = JsonUtility.FromJson<StreamingMesh.StreamInfo>(inString);
		streamInfo.name = url + "channels/" + channel + "/" + streamInfo.name;

		Debug.Log(streamInfo.name);

		if (Directory.Exists(Path.Combine("channels", channel))) {
			string stmjPath = Path.Combine(Path.Combine("channels", channel), "streaminfo.stmj");
			File.AppendAllText(stmjPath, JsonUtility.ToJson(streamInfo) + Environment.NewLine);
			if(lastMinOnly) {
				List<string> lines = new List<string>(File.ReadAllLines(stmjPath));
				while(lines.Count > 6) {
					lines.RemoveAt(0);
				}
				File.WriteAllLines(stmjPath, lines.ToArray());
            }
        } else {
            OnError(req, res, "400");
            return;
        }

        string outString = "{\"stat\":\"ok\"}";
        res.StatusCode = (int)HttpStatusCode.OK;
        res.ContentType = MediaTypeNames.Text.Plain;

        SendText(outString, res);
    }

    void OnPostStream(HttpListenerRequest req, HttpListenerResponse res, string channel, string streamName) {
        if (!channels.Contains(channel)) {
            OnError(req, res, "404");
            return;
        }

        byte[] binary = GetBinary(req);
        if (binary == null) {
            OnError(req, res, "400");
            return;
        }

		if (Directory.Exists(Path.Combine("channels", channel))) {
			FileWrite<byte[]>("channels", channel, streamName, binary);
        } else {
            OnError(req, res, "400");
            return;
        }

        string outString = "{\"stat\":\"ok\"}";
        res.StatusCode = (int)HttpStatusCode.OK;
        res.ContentType = MediaTypeNames.Text.Plain;

        SendText(outString, res);
    }

    void OnError(HttpListenerRequest req, HttpListenerResponse res, string code) {
        res.StatusCode = (int)HttpStatusCode.BadRequest;
        res.ContentType = MediaTypeNames.Text.Plain;
        byte[] sendData = Encoding.Unicode.GetBytes(errors[code]);
        res.OutputStream.Write(sendData, 0, sendData.Length);
        res.Close();
    }

    void OnNotFound(HttpListenerRequest req, HttpListenerResponse res, string code) {
        res.StatusCode = (int)HttpStatusCode.NotFound;
        res.ContentType = MediaTypeNames.Text.Plain;
        byte[] sendData = Encoding.Unicode.GetBytes(errors[code]);
        res.OutputStream.Write(sendData, 0, sendData.Length);
        res.Close();
    }

    string[] ParseURLToPath(string url) {
        string[] path = url.Split('/');
        if (path.Length != 4) {
            return new string[] {
                null,
                null
            };
        } else {
            return new string[]{
                path[1],
                path[2]
            };
        }
    }

    void SendText(string text, HttpListenerResponse res) {
     try {
            StreamWriter sw = new StreamWriter(res.OutputStream);
            sw.Write(text);
            sw.Flush();
            sw.Close();
            res.Close();
        } catch (Exception ex) {
            if (ex.InnerException != null) {
                Debug.LogWarning(ex.InnerException.ToString());
            }
        }
    }

    string GetText(HttpListenerRequest req) {
        string text = null;
        try {
            StreamReader sr = new StreamReader(req.InputStream);
            text = sr.ReadToEnd();
            sr.Close();
        } catch (Exception ex) {
            if (ex.InnerException != null) {
                Debug.LogWarning(ex.InnerException.ToString());
            }
        }
        return text;
    }

	void SendBinary(byte[] binary, HttpListenerResponse res) {
		try {
			BinaryWriter bw = new BinaryWriter(res.OutputStream);
			bw.Write(binary);
			bw.Flush();
			bw.Close();
			res.Close();
		} catch (Exception ex) {
			if (ex.InnerException != null) {
				Debug.LogWarning(ex.InnerException.ToString());
			}
		}
	}

    byte[] GetBinary(HttpListenerRequest req) {
        List<byte> binary = new List<byte>();
        byte[] buf = new byte[1024 * 1024 * 5];
        int bytesRead = 0;
        try {
            while ((bytesRead = req.InputStream.Read(buf, 0, buf.Length)) > 0) {
                byte[] frag = new byte[bytesRead];
                System.Array.Copy(buf, frag, bytesRead);
                binary.AddRange(frag);
            }
            return binary.ToArray();
        } catch (Exception ex) {
            if (ex.InnerException != null) {
                Debug.LogWarning(ex.InnerException.ToString());
            }
        }
        return null;
    }

	void FileWrite<T>(string parentDirectory, string subDirectory, string fileName, T data) {
		string targetDirectory = "";
		if (subDirectory != "") {
			targetDirectory = Path.Combine(parentDirectory, subDirectory);
		} else {
			targetDirectory = parentDirectory;
		}
		if(!Directory.Exists(targetDirectory)) {
			Directory.CreateDirectory(targetDirectory);
		}
		string fileDirectory = Path.Combine(targetDirectory, fileName);

		Type type = data.GetType();
		if (typeof(string) == type) {
			StreamWriter sw = File.CreateText(fileDirectory);
			string stringData = data as string;
			sw.Write(stringData);
			sw.Close();

		} else if (typeof(byte).IsAssignableFrom(type.GetElementType()) && type.IsArray) {
			FileStream fs = File.Create(fileDirectory);
			byte[] binaryData = data as byte[];
			fs.Write(binaryData, 0, binaryData.Length);
			fs.Close();
		}
	}
#endif
}
