using System;
using System.Collections.Generic;
using System.IO;
using StreamingMesh.Core.Serialization;

static class InitialDataPartsTests
{
  static int checks;
  static void Check(bool value) { checks++; if (!value) throw new Exception("Check " + checks + " failed"); }
  static void Reject(Action action) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected); }
  static void Main()
  {
    var files = new List<byte[]>();
    var writer = new InitialDataPartWriter((p, data) => files.Add(data), 23);
    var resources = new[] { new byte[19], new byte[58], new byte[71] };
    var random = new Random(17); foreach (var bytes in resources) random.NextBytes(bytes);
    for (int i = 0; i < 3; i++) writer.Write(i, 0, resources[i]);
    writer.Flush();
    var names = new HashSet<string>();
    foreach (var part in writer.Parts) Check(part.file.Length == 36 && InitialDataParts.ValidFileName(part.file) && names.Add(part.file));
    var sizes = new[] { new[] { 19 }, new[] { 58 }, new[] { 71 } };
    InitialDataParts.Validate(writer.Parts, sizes[0], sizes[1], sizes[2]);
    int consumed = 0;
    var scratch = new byte[7];
    for (int i = 0; i < files.Count; i++)
      using (var input = new MemoryStream(files[i]))
        InitialDataParts.Read(writer.Parts[i], input, scratch, (segment, offset, bytes, count) => {
          for (int n = 0; n < count; n++) Check(resources[segment.kind][offset + n] == bytes[n]);
          consumed += count;
        });
    Check(consumed == 148);
    writer.Parts[0].file = "model-metadata.bin";
    InitialDataParts.Validate(writer.Parts, sizes[0], sizes[1], sizes[2]);
    Check(true);
    writer.Parts[1].file = "MODEL-METADATA.bin";
    Reject(() => InitialDataParts.Validate(writer.Parts, sizes[0], sizes[1], sizes[2]));
    writer.Parts[1].file = "stream1.bin";
    foreach (string invalid in new[] { "../stream0.bin", "CON.bin", "lpt1.bin", "stream?.bin", "日本語.bin" })
      Check(!InitialDataParts.ValidFileName(invalid));
    var record = writer.Parts[1].records[0]; record.resourceOffset++;
    Reject(() => InitialDataParts.Validate(writer.Parts, sizes[0], sizes[1], sizes[2])); record.resourceOffset--;
    record.offset++;
    Reject(() => InitialDataParts.Validate(writer.Parts, sizes[0], sizes[1], sizes[2])); record.offset--;
    var tail = writer.Parts[writer.Parts.Count - 1]; writer.Parts.RemoveAt(writer.Parts.Count - 1);
    Reject(() => InitialDataParts.Validate(writer.Parts, sizes[0], sizes[1], sizes[2])); writer.Parts.Add(tail);
    files[0][12] ^= 1;
    Reject(() => { using (var input = new MemoryStream(files[0])) InitialDataParts.Read(writer.Parts[0], input, scratch, (a,b,c,d) => { }); });
    files[0][12] ^= 1;
    writer.Parts[0].size++;
    Reject(() => { using (var input = new MemoryStream(files[0])) InitialDataParts.Read(writer.Parts[0], input, scratch, (a,b,c,d) => { }); });
    writer.Parts[0].size--;
    // A model larger than the old 128 MiB aggregate limit is valid; each
    // physical file and resource still has an independent bound.
    var big = new InitialDataPartWriter((p, data) => Check(p.size <= InitialDataParts.MaximumPartBytes));
    var resource = new byte[17 * 1024 * 1024]; var textureSizes = new List<int>();
    for (int i = 0; i < 8; i++) { big.Write(2, i, resource); textureSizes.Add(resource.Length); }
    big.Flush(); InitialDataParts.Validate(big.Parts, new int[0], new int[0], textureSizes);
    Check(big.Parts.Count == 2 && big.Parts[0].size == 68 * 1024 * 1024);
    Check(big.Parts[0].records.Count == 4); // Several textures in one file.
    foreach (var part in big.Parts) foreach (var segment in part.records)
      Check(segment.resourceOffset == 0 && segment.size == resource.Length); // No texture is split.
    var tooLarge = new InitialDataPart { file = "budget.bin", sha256 = new string('a', 64),
      size = InitialDataParts.MaximumPartBytes + 1, compressedSize = 10,
      records = new List<InitialDataSegment> { new InitialDataSegment { kind = 2, size = 1 } } };
    Reject(() => InitialDataParts.Validate(new[] { tooLarge }, new int[0], new int[0], new[] { 1 }));
    Reject(() => InitialDataParts.Validate(big.Parts, new int[0], new int[0], new[] { InitialDataParts.MaximumResourceBytes + 1 }));
    using (var hard = new InitialDataPartWriter((p, data) => Check(p.size <= InitialDataParts.MaximumPartBytes), InitialDataParts.MaximumPartBytes))
    {
      for (int i = 0; i < 8; i++) hard.Write(2, i, resource);
      hard.Flush(); InitialDataParts.Validate(hard.Parts, new int[0], new int[0], textureSizes);
      Check(hard.Parts.Count == 2 && hard.Parts[0].size == 119 * 1024 * 1024);
    }
    int published = 0;
    using (var incomplete = new InitialDataPartWriter((p, data) => published++))
    {
      Reject(() => incomplete.Write(2, 0, new MemoryStream(new byte[3]), 4));
      incomplete.Flush(); Check(published == 0);
    }
    big.Dispose(); writer.Dispose();
    Console.WriteLine("PASS initial data parts: " + checks + " checks");
  }
}
