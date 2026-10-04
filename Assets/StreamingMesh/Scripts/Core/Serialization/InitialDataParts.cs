using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.IO.Compression;

namespace StreamingMesh.Core.Serialization
{
  [Serializable]
  public sealed class InitialDataSegment
  {
    public int kind, index, offset, resourceOffset, size;
  }

  [Serializable]
  public sealed class InitialDataPart
  {
    public string file, sha256;
    public int size, compressedSize;
    public List<InitialDataSegment> records = new List<InitialDataSegment>();
  }

  public static class InitialDataParts
  {
    public const int Material = 0, Mesh = 1, Texture = 2;
    public const int DefaultPartBytes = 64 * 1024 * 1024;
    public const int MaximumPartBytes = 128 * 1024 * 1024;
    public const int MaximumMetadataBytes = 16 * 1024 * 1024;
    public const int MaximumResourceBytes = 128 * 1024 * 1024;
    public const long MaximumTotalBytes = 1024L * 1024 * 1024;
    public const int MaximumParts = 4096;

    public static string Hash(byte[] data)
    {
      using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }

    public static void Validate(IList<InitialDataPart> parts, IList<int> materials, IList<int> meshes, IList<int> textures)
    {
      if (parts == null || parts.Count == 0 || parts.Count > MaximumParts) throw new InvalidDataException("Missing/invalid initial-data parts.");
      var sizes = new[] { materials, meshes, textures };
      long total = 0;
      foreach (var table in sizes)
      {
        if (table == null) throw new InvalidDataException("Missing resource sizes.");
        foreach (int size in table)
        {
          if (size <= 0 || size > MaximumResourceBytes) throw new InvalidDataException("Initial resource exceeds its budget.");
          total += size;
        }
      }
      if (total > MaximumTotalBytes) throw new InvalidDataException("Initial-data total exceeds its budget.");
      int kind = 0, index = 0, resourceOffset = 0;
      long consumed = 0;
      var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      for (int p = 0; p < parts.Count; p++)
      {
        var part = parts[p];
        if (part == null || !ValidFileName(part.file) || !names.Add(part.file) || part.size <= 0 || part.size > MaximumPartBytes ||
            part.compressedSize <= 0 || part.compressedSize > MaximumPartBytes + 65536 ||
            !ValidHash(part.sha256) || part.records == null || part.records.Count == 0)
          throw new InvalidDataException("Invalid initial-data part " + p);
        int offset = 0;
        foreach (var record in part.records)
        {
          while (kind < sizes.Length && index == sizes[kind].Count) { kind++; index = 0; }
          if (kind == sizes.Length || record == null || record.kind != kind || record.index != index ||
              record.offset != offset || record.resourceOffset != resourceOffset || record.size <= 0 ||
              record.size > part.size - offset || record.size > sizes[kind][index] - resourceOffset)
            throw new InvalidDataException("Initial-data segments have a gap, overlap or invalid reference.");
          offset += record.size; resourceOffset += record.size; consumed += record.size;
          if (resourceOffset == sizes[kind][index]) { index++; resourceOffset = 0; }
        }
        if (offset != part.size) throw new InvalidDataException("Initial-data part length mismatch.");
      }
      if (consumed != total || resourceOffset != 0) throw new InvalidDataException("Incomplete initial-data parts.");
    }

    static bool ValidHash(string hash)
    {
      if (hash == null || hash.Length != 64) return false;
      foreach (char c in hash) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
      return true;
    }

    public static bool ValidFileName(string name)
    {
      if (string.IsNullOrEmpty(name) || name.Length > 96 || !name.EndsWith(".bin", StringComparison.Ordinal) || name[0] == '.') return false;
      foreach (char c in name)
        if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '.' && c != '_' && c != '-') return false;
      string stem = name.Split('.')[0].ToUpperInvariant();
      if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return false;
      if (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '1' && stem[3] <= '9') return false;
      return true;
    }

    // The callback borrows scratch only until it returns. No decoded part or
    // whole texture byte[] is allocated by this path.
    public static void Read(InitialDataPart part, Stream compressed, byte[] scratch,
      Action<InitialDataSegment, int, byte[], int> consume)
    {
      if (compressed == null || !compressed.CanSeek || compressed.Length != part.compressedSize || scratch == null || scratch.Length == 0)
        throw new InvalidDataException("Invalid initial-data input: " + part.file);
      compressed.Position = 0;
      using (var sha = SHA256.Create())
        if (BitConverter.ToString(sha.ComputeHash(compressed)).Replace("-", "").ToLowerInvariant() != part.sha256)
          throw new InvalidDataException("Missing or changed initial-data file: " + part.file);
      compressed.Position = 0;
      if (compressed.Length < 18 || compressed.ReadByte() != 0x1f || compressed.ReadByte() != 0x8b)
        throw new InvalidDataException("Expected a GZip initial-data part.");
      compressed.Position = compressed.Length - 4;
      long footerSize = 0;
      for (int i = 0; i < 4; i++) footerSize |= (long)compressed.ReadByte() << (i * 8);
      if (footerSize != part.size) throw new InvalidDataException("Invalid GZip part length.");
      compressed.Position = 0;
      using (var gzip = new GZipStream(compressed, CompressionMode.Decompress, true))
      {
        int total = 0;
        foreach (var segment in part.records)
        {
          int offset = 0;
          while (offset < segment.size)
          {
            int count = gzip.Read(scratch, 0, Math.Min(scratch.Length, segment.size - offset));
            if (count == 0) throw new InvalidDataException("Truncated initial-data part: " + part.file);
            consume(segment, segment.resourceOffset + offset, scratch, count);
            offset += count; total += count;
          }
        }
        if (total != part.size || gzip.ReadByte() != -1) throw new InvalidDataException("Wrong initial-data size: " + part.file);
      }
    }
  }

  // Export one resource at a time. Never build a combined array for the entire model.
  public sealed class InitialDataPartWriter : IDisposable
  {
    readonly byte[] scratch = new byte[64 * 1024];
    readonly int targetBytes;
    readonly Action<InitialDataPart, byte[]> publish;
    readonly List<InitialDataSegment> records = new List<InitialDataSegment>();
    MemoryStream output;
    GZipStream gzip;
    int used;
    bool disposed;
    public readonly List<InitialDataPart> Parts = new List<InitialDataPart>();

    public InitialDataPartWriter(Action<InitialDataPart, byte[]> publish, int partBytes = InitialDataParts.DefaultPartBytes)
    {
      if (publish == null || partBytes <= 0 || partBytes > InitialDataParts.MaximumPartBytes) throw new ArgumentException("Invalid part writer.");
      this.publish = publish; targetBytes = partBytes;
    }

    public void Write(int kind, int index, byte[] resource)
    {
      if (resource == null) throw new ArgumentNullException(nameof(resource));
      using (var input = new MemoryStream(resource, false)) Write(kind, index, input, resource.Length);
    }

    public void Write(int kind, int index, Stream resource, int length)
    {
      if (disposed) throw new ObjectDisposedException(nameof(InitialDataPartWriter));
      if (resource == null || length <= 0 || length > InitialDataParts.MaximumResourceBytes)
        throw new InvalidDataException("Invalid initial resource.");
      // 64 MiB is a target, not a slicing boundary. Finish each whole resource
      // before rotating. Only the hard 128 MiB ceiling forces a pre-flush.
      if (used > InitialDataParts.MaximumPartBytes - length) Flush();
      if (output == null)
      {
        output = new MemoryStream();
        gzip = new GZipStream(output, CompressionLevel.Fastest, true);
      }
      int read = 0;
      try
      {
        while (read < length)
        {
          int n = resource.Read(scratch, 0, Math.Min(scratch.Length, length - read));
          if (n == 0) throw new InvalidDataException("Truncated initial resource.");
          gzip.Write(scratch, 0, n); read += n;
        }
      }
      catch { Dispose(); throw; } // Never publish a partially written resource.
      records.Add(new InitialDataSegment { kind = kind, index = index, offset = used, resourceOffset = 0, size = length });
      used += length;
      if (used >= targetBytes) Flush();
    }

    public void Flush()
    {
      if (used == 0) return;
      byte[] compressed;
      try
      {
        gzip.Dispose(); gzip = null;
        compressed = output.ToArray();
      }
      finally { gzip?.Dispose(); gzip = null; output.Dispose(); output = null; }
      if (Parts.Count >= InitialDataParts.MaximumParts) throw new InvalidDataException("Too many initial-data parts.");
      var part = new InitialDataPart { file = Guid.NewGuid().ToString("N") + ".bin", size = used, compressedSize = compressed.Length,
        sha256 = InitialDataParts.Hash(compressed), records = new List<InitialDataSegment>(records) };
      Parts.Add(part); publish(part, compressed); records.Clear(); used = 0;
    }

    public void Dispose()
    {
      if (disposed) return;
      disposed = true;
      gzip?.Dispose(); gzip = null;
      output?.Dispose(); output = null;
      records.Clear(); used = 0;
    }
  }

}
