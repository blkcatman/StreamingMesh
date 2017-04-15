using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace StreamingMesh {
	public class CustomDownloadHandler : DownloadHandlerScript {
		public delegate void Received(int currentBytes, int contentLength);
		public Received OnReceived;

		int m_contentLength = 0;
		int m_currentBytes = 0;
		List<byte> m_downloadBuffer;

		public byte[] DownloadedData {
			get {return m_downloadBuffer.ToArray();}
		}

		public CustomDownloadHandler() : base() {
			m_downloadBuffer = new List<byte>();
		}
		public CustomDownloadHandler(byte[] buffer) : base(buffer){
			m_downloadBuffer = new List<byte>();
		}

		protected override byte[] GetData() { return null; }

		protected override bool ReceiveData(byte[] data, int dataLength) {
			if(data == null || data.Length < 1) {
				return false;
			}
			m_downloadBuffer.AddRange(data);
			m_currentBytes += data.Length;
			if(OnReceived != null) {
				OnReceived(m_currentBytes, m_contentLength);
			}
			return true;
		}

		protected override void CompleteContent() {
		}

		protected override void ReceiveContentLength(int contentLength) {
			m_contentLength = contentLength;
    }

	}
}