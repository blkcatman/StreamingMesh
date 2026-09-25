using System;
using System.IO;
using System.IO.Compression;

namespace StreamingMesh.Lib {

	public class ExternalTools {

		public static byte[] Compress(byte[] data) {
			if (data == null)
				throw new ArgumentNullException(nameof(data));

			using (MemoryStream output = new MemoryStream()) {
				using (GZipStream gzip = new GZipStream(output, CompressionLevel.Fastest, true)) {
					gzip.Write(data, 0, data.Length);
				}
				return output.ToArray();
			}
		}

		public static byte[] Decompress(byte[] data) {
			if (data == null)
				throw new ArgumentNullException(nameof(data));

			using (MemoryStream input = new MemoryStream(data, false))
			using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
			using (MemoryStream output = new MemoryStream()) {
				gzip.CopyTo(output);
				return output.ToArray();
			}
		}


	}

}
