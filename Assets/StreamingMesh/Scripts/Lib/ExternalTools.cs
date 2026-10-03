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

        // Initial data is read once; use the validated GZip ISIZE directly so
        // growing MemoryStream backing arrays and ToArray never coexist.
        public static byte[] DecompressExact(byte[] data, int maximumBytes) {
            if (data == null || data.Length < 18 || data[0] != 0x1f || data[1] != 0x8b)
                throw new InvalidDataException("Missing initial-data GZip header.");
            uint length = BitConverter.ToUInt32(data, data.Length - 4);
            if (length == 0 || length > maximumBytes) throw new InvalidDataException("Initial-data size exceeds its budget.");
            var result = new byte[(int)length];
            using (var input = new MemoryStream(data, false))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress)) {
                int offset = 0;
                while (offset < result.Length) {
                    int count = gzip.Read(result, offset, result.Length - offset);
                    if (count == 0) throw new InvalidDataException("Truncated initial data.");
                    offset += count;
                }
                if (gzip.ReadByte() != -1) throw new InvalidDataException("Initial-data length mismatch.");
            }
            return result;
        }


	}

}
