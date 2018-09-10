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

namespace StreamingMesh.Lib {

	public class ExternalTools {

		public static byte[] Compress(byte[] data) {
			/*
						using (MemoryStream rStream = new MemoryStream(data))
						using (MemoryStream wStream = new MemoryStream()) {
								using (GZipStream gStream = new GZipStream(wStream, CompressionMode.Compress)) {
										//new BinaryFormatter().Serialize(gStream, data);
										CopyTo(rStream, gStream);
								}
								return wStream.ToArray();
						} 
			*/
			byte[] compress = new byte[data.Length + 18];
			int gzres = lzip.gzip(data, compress, 6);
			byte[] output = new byte[gzres];
			Buffer.BlockCopy(compress, 0, output, 0, gzres);
			return output;
		}

		public static byte[] Decompress(byte[] data) {
			/*
						using (MemoryStream rStream = new MemoryStream(data))
						using (MemoryStream wStream = new MemoryStream()) { 
								using (GZipStream gStream = new GZipStream(rStream, CompressionMode.Decompress)) {
										//return (byte[])new BinaryFormatter().Deserialize(gStream);
										CopyTo(gStream, wStream);
								}
								return wStream.ToArray();
						}
			*/
			byte[] output = new byte[lzip.gzipUncompressedSize(data)];
			lzip.unGzip(data, output);
			return output;
		}


	}

}