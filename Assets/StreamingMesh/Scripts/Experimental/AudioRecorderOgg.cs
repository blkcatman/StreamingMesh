//AudioRecorderOgg.cs
//
//Copyright (c) 2017 Tatsuro Matsubara.
//Creative Commons License
//This file is licensed under a Creative Commons Attribution-ShareAlike 4.0 International License.
//https://creativecommons.org/licenses/by-sa/4.0/
//

using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;

public class AudioRecorderOgg : MonoBehaviour {
#if UNITY_EDITOR
    int outputRate = 44100;

	const int _headerSize = 44;//wav file header size
	const float _shortFactor = 32767;

	FileStream fs;
	bool recOutput;

    System.Diagnostics.Process process;
    bool isEncoding = false;

	int recInterval = 10;
	int currentIndex = 0;
	int byteOffset = 0;

	// Use this for initialization
	void Start () {
		if (gameObject.GetComponent<AudioListener>() == null) {
			Debug.LogWarning("AudioRecorder: The gameobject has not AudioListener!");
			return;
		}
		CreateFile(currentIndex.ToString("000000") + ".wav");

	}


	void CreateFile(string fileName) {
		Debug.Log("A_REC: " + fileName);
		fs = new FileStream(fileName, FileMode.Create);
		byte[] empty = new byte[44];
		fs.Write(empty, 0, 44);
		recOutput = true;
	}
	
	// Update is called once per frame
	void Update () {
	}

	void OnDisable() {
		if(fs != null) {
			fs.Close();
			fs.Dispose();
			fs = null;
		}
	}

	void OnAudioFilterRead(float[] data, int channels) {
		if(!recOutput || fs == null)
			return;

		byte[] byteData = new byte[data.Length * 2];
		for(int i = 0; i < data.Length; i++) {
			short shortValue = (short)(data[i] * _shortFactor);
			byte[] byteValues = BitConverter.GetBytes(shortValue);
			byteData[i * 2] = byteValues[0];
			byteData[i * 2 + 1] = byteValues[1];
		}

		fs.Write(byteData, 0, byteData.Length);
		byteOffset += byteData.Length;
        if(byteOffset >= 44100 * 2 * 2 * recInterval) {
			WriteHeader();//finish to write wav file
			EncodeToMP3(
				currentIndex.ToString("000000") + ".wav",
				currentIndex.ToString("000000") + ".ogg"
                //currentIndex.ToString("000000") + ".mp3"
			);
			currentIndex++;
			byteOffset = 0;
			CreateFile(currentIndex.ToString("000000") + ".wav");
		}
	}

	void EncodeToMP3(string inputName, string outputName) {
		process = new System.Diagnostics.Process();
		#if UNITY_EDITOR_WIN
		string ffmpegPath = Path.Combine("ffmpeg", "ffmpeg.exe");
		#elif UNITY_EDITOR_OSX
		string ffmpegPath = Path.Combine("ffmpeg", "ffmpeg");
		#endif

		string args = string.Format(
			//"-y -i \"{0}\" -vn -ac 2 -ar 44100 -ab 256k -acodec libmp3lame -f mp3 \"{1}\"",
			"-y -i \"{0}\" -vn -ac 2 -ar 44100 -ab 256k -acodec libvorbis -f ogg \"{1}\"",
			 inputName, outputName);

		process.StartInfo.FileName = ffmpegPath;
		process.StartInfo.Arguments = args;
		//process.EnableRaisingEvents = false;
		process.EnableRaisingEvents = true;
		process.Exited += EncodeFinished;
		isEncoding = true;
		process.Start();
	}

	void EncodeFinished(object sender, System.EventArgs e) {
		process.Dispose();
		process = null;
		isEncoding = false;
	}

	void WriteHeader() {
		fs.Seek(0, SeekOrigin.Begin);
		List<byte> header = new List<byte>();

		header.AddRange(System.Text.Encoding.UTF8.GetBytes("RIFF"));
		header.AddRange(BitConverter.GetBytes((int)fs.Length - 8));
		header.AddRange(System.Text.Encoding.UTF8.GetBytes("WAVE"));
		header.AddRange(System.Text.Encoding.UTF8.GetBytes("fmt "));
		header.AddRange(BitConverter.GetBytes(16));// fmt size
		header.AddRange(BitConverter.GetBytes((short)1)); // Uncompressed PCM
		header.AddRange(BitConverter.GetBytes((short)2)); // 2 channels
		header.AddRange(BitConverter.GetBytes(outputRate));
		header.AddRange(BitConverter.GetBytes(outputRate * 2 * 2));
		header.AddRange(BitConverter.GetBytes((short)(2 * 2))); //16bit(2bytes) * 2 channels;
		header.AddRange(BitConverter.GetBytes((short)16)); //16bit datasize
		header.AddRange(System.Text.Encoding.UTF8.GetBytes("data"));
		header.AddRange(BitConverter.GetBytes((int)fs.Length - _headerSize));

		byte[] outHeader = header.ToArray();
		fs.Write(outHeader, 0, outHeader.Length);
		fs.Close();
		fs.Dispose();
		fs = null;
	}

#endif
}
