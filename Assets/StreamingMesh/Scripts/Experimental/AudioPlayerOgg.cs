using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.IO;
using System.Text;

public class AudioPlayerOgg : MonoBehaviour {

	public string url = "http://afs.blkcatman.net:3000/channels/test.m3u8";

	Queue<Action> queue = new Queue<Action>();
	bool isDownload = false;

	AudioClip currentClip;
	List<AudioClip> audioClips = new List<AudioClip>();
	List<int> audioIndex = new List<int>();
	AudioSource audioSource;
	int currentAudio;

	public float updateInterval = 10f;
	float currentTime = 0f;

	bool triggeredStart = false;

	// Use this for initialization
	void Start () {
		audioSource = gameObject.AddComponent<AudioSource>();
	}

	public void StartLoading() {
		queue.Enqueue(() => {
			StartCoroutine("LoadList", url);
		});
		triggeredStart = true;
	}

	IEnumerator LoadList(string url) {
		isDownload = true;
		Debug.Log("REQ: " + url);
		WWW www = new WWW(url);
		yield return www;
		string rawList = Encoding.UTF8.GetString(www.bytes);
		Debug.Log(rawList);
		string[] lines = rawList.Split('\n');
		foreach(string line in lines) {
			if(line.StartsWith("http")) {
				string filename = Path.GetFileNameWithoutExtension(line);
				if(!audioIndex.Contains(int.Parse(filename))) {
					queue.Enqueue(() => {
						StartCoroutine("LoadClip", line);
					});
				}
			}
		}
		isDownload = false;
	}

	IEnumerator LoadClip(string url) {
		isDownload = true;
		Debug.Log("REQ: " + url);
		WWW www = new WWW(url);
		yield return www;
		AudioClip clip = www.GetAudioClip(false, false, AudioType.OGGVORBIS);
		string filename = Path.GetFileNameWithoutExtension(url);
		clip.name = filename;
		if(clip != null) {
			audioClips.Add(clip);
			audioIndex.Add(int.Parse(filename));
		}
		isDownload = false;
	}

	public void SeekTo(string milliseconds) {
		int seconds = 0;
		if(int.TryParse(milliseconds, out seconds)) {
			SeekTo(seconds);
		}
	}

	public void SeekTo(int milliseconds) {
		int index = milliseconds / 10000;
		int sub = milliseconds % 10000;
		if(audioIndex.Contains(index)) {
			currentAudio = index + 1;
			currentTime = 0;
            audioSource.Stop();
			AudioClip clip = audioClips[index];
			audioSource.clip = clip;
			audioSource.time = ((float)sub) / 1000f;
			if(audioSource.isActiveAndEnabled) {
				audioSource.Play();
			}
		}
	}

	// Update is called once per frame
	void Update () {
		if(!triggeredStart) {
			return;
		}
		
		if(queue.Count > 0 && !isDownload) {
			queue.Dequeue().Invoke();
		}
		currentTime += Time.deltaTime;
		if(currentTime > updateInterval && isDownload) {
			queue.Enqueue(() => {
				StartCoroutine("LoadList", url);
			});
			currentTime -= updateInterval;
		}
		if(audioSource.isActiveAndEnabled && !audioSource.isPlaying) {
			if(audioClips.Count > currentAudio) {
				audioSource.clip = audioClips[currentAudio];
				audioSource.Play();
				currentAudio++;
            }
		}
	}
}
