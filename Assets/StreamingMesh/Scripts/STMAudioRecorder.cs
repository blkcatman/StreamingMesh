using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine.Networking;

namespace StreamingMesh {
	[RequireComponent(typeof(AudioListener))]
	public class STMAudioRecorder : MonoBehaviour {
#if UNITY_EDITOR
		int sampleRate = 44100;

		const float _shortFactor = 32767;

		List<float> audioBuffer = new List<float>();
		List<byte> audioByteBuffer = new List<byte>();

		System.Diagnostics.Process process;

		public int RecordTimeSpan { get; set; }
		int currentIndex = 0;

		bool isProcessing = false;

		byte[] wavHeader;

		bool startRecord = false;
		public bool IsStartRecord {
				get {
						return startRecord;
				}
		}

		string encodingFilename;
		public readonly Queue<Action> executeOnUpdate = new Queue<Action>();

    public delegate void GetEncodedAudioData(long ticks, string fileName, byte[] data);

    public GetEncodedAudioData OnGetEncodedAudioData;

		public void Record() {
			if (gameObject.GetComponent<AudioListener>() == null) {
				return;
			}
			if (startRecord == false) {
				currentIndex = 0;
			}
			audioBuffer.Clear();

			int oneSecByteSize = sampleRate * 2 * 2;
			int storedByteBufferSize = oneSecByteSize * (RecordTimeSpan + 2);
			wavHeader = GetWavHeader(storedByteBufferSize + 44);

			startRecord = true;
		}

		public void Stop() {
			startRecord = false;
		}

			// Use this for initialization
		void Start () {
			if (gameObject.GetComponent<AudioListener>() == null) {
				Debug.LogWarning("AudioRecorder: The gameobject has not AudioListener!");
				return;
			}
			Debug.Log("AudioSampleRate: " + AudioSettings.outputSampleRate);
			sampleRate = (int)AudioSettings.outputSampleRate;
			audioBuffer.Clear();
		}

		// Update is called once per frame
		void Update () {
			if(Application.isPlaying && startRecord) {
				int currentAudioSize = audioBuffer.Count;
				if(currentAudioSize > 0) {
					for(int i = 0; i < audioBuffer.Count; i++) {
						short shortValue = (short)(audioBuffer[i] * _shortFactor);
						byte[] byteValues = BitConverter.GetBytes(shortValue);
						audioByteBuffer.Add(byteValues[0]);
						audioByteBuffer.Add(byteValues[1]);
					}
					audioBuffer.RemoveRange(0, currentAudioSize);
				}

				int oneSecByteSize = sampleRate * 2 * 2;
				int storedByteBufferSize = (int)(oneSecByteSize * (RecordTimeSpan + 0.5f));
				//int storedByteBufferSize = oneSecByteSize * RecordTimeSpan;
				if(audioByteBuffer.Count >= storedByteBufferSize && !isProcessing) {
					Invoke("SetAudioData", 0f);
					isProcessing = true;
				}
			}
		}

		void OnAudioFilterRead(float[] data, int channels) {
			if(!startRecord)
				return;

			audioBuffer.AddRange(data);
		}

		void SetAudioData() {
			int oneSecByteSize = sampleRate * 2 * 2;
			int storedByteBufferSize = (int)(oneSecByteSize * (RecordTimeSpan + 0.5f));
			//int storedByteBufferSize = oneSecByteSize * RecordTimeSpan;
			byte[] wavData = new byte[storedByteBufferSize + 44];
			Buffer.BlockCopy(wavHeader, 0, wavData, 0, 44); //write wav header
			Buffer.BlockCopy(audioByteBuffer.ToArray(), 0, wavData, 44, storedByteBufferSize);
			OnGetEncodedAudioData(currentIndex, currentIndex.ToString("000000") + ".wav", wavData);
			audioByteBuffer.RemoveRange(0, oneSecByteSize * RecordTimeSpan);
			currentIndex++;
			isProcessing = false;
		}

		byte[] GetWavHeader(int dataLength) {
			List<byte> header = new List<byte>();
      
      const int headerSize = 44;
			header.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
			header.AddRange(BitConverter.GetBytes(dataLength - 8));
			header.AddRange(System.Text.Encoding.ASCII.GetBytes("WAVE"));
			header.AddRange(System.Text.Encoding.ASCII.GetBytes("fmt "));
			header.AddRange(BitConverter.GetBytes(16));// fmt size
			header.AddRange(BitConverter.GetBytes((short)1)); // Uncompressed PCM
			header.AddRange(BitConverter.GetBytes((short)2)); // 2 channels
			header.AddRange(BitConverter.GetBytes(sampleRate));
			header.AddRange(BitConverter.GetBytes(sampleRate * 2 * 2));
			header.AddRange(BitConverter.GetBytes((short)(2 * 2))); //16bit(2bytes) * 2 channels;
			header.AddRange(BitConverter.GetBytes((short)16)); //16bit datasize
			header.AddRange(System.Text.Encoding.ASCII.GetBytes("data"));
			header.AddRange(BitConverter.GetBytes(dataLength - headerSize));
			return header.ToArray();
		}
#endif
	}

}