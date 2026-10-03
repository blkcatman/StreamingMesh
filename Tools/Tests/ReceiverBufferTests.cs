using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using StreamingMesh.Core.Rendering;

static class ReceiverBufferTests
{
  static int checks;
  static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
  static byte[] Chunk(bool invalid = false)
  {
    using (var raw = new MemoryStream())
    {
      using (var writer = new BinaryWriter(raw, System.Text.Encoding.UTF8, true))
      {
        writer.Write(2); writer.Write(29); writer.Write(invalid ? -1 : 29);
        for (int i = 0; i < 2; i++)
        {
          var frame = new byte[29]; frame[0] = 15; frame[1] = (byte)i; frame[8] = 2;
          Buffer.BlockCopy(BitConverter.GetBytes((long)i * 10000000), 0, frame, 21, 8);
          writer.Write(frame);
        }
      }
      using (var compressed = new MemoryStream())
      {
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, true)) raw.WriteTo(gzip);
        return compressed.ToArray();
      }
    }
  }
  static void Reject(Action action)
  {
    try { action(); } catch (InvalidDataException) { checks++; return; }
    throw new Exception("Malformed chunk accepted");
  }
  static void Main()
  {
    var ring = new FrameRing<int>(17);
    var reference = new List<int>(); var random = new Random(73);
    for (int i = 0; i < 20000; i++)
    {
      if (reference.Count == 0 || (reference.Count < 17 && random.Next(2) == 0))
      {
        int at = random.Next(reference.Count + 1); ring.Insert(at, i); reference.Insert(at, i);
      }
      else
      {
        int at = random.Next(reference.Count); Check(ring.RemoveAt(at) == reference[at], "Wrong removed frame"); reference.RemoveAt(at);
      }
      Check(ring.Count == reference.Count, "Ring count changed");
      for (int j = 0; j < reference.Count; j++) Check(ring[j] == reference[j], "Wrapped insertion reordered frames");
    }
    ring.Clear(); ring.Add(1); ring.RemoveAt(0);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 100000; i++) { ring.Add(i); ring.Add(i + 1); ring.RemoveAt(0); ring.RemoveAt(0); }
    long allocations = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(allocations == 0, "Ring operations allocate");

    byte[] good = Chunk(), bad = Chunk(true);
    using (var pool = new EncodedChunkPool(2, 2, 1024, 65536, 131072))
    {
      var a = pool.Parse(0, good, 0, 2, .1f); var b = pool.Parse(1, good, 0, 2, .1f);
      byte[] storage = a.bytes;
      pool.GetUsage(out var capacity, out var payload, out var peakPayload, out var leases, out var peakLeases, out var slots);
      Check(capacity == 131072 && payload == 140 && peakPayload == 140 && leases == 2 && peakLeases == 2 && slots == 2, "Active pool usage incorrect");
      Check(!pool.HasFreeChunk && !ReferenceEquals(a.bytes, b.bytes), "Active chunk lease reused");
      Check(a.frames[1].offset == 41 && a.frames[1].length == 29 && a.frames[1].presentationTime == 1, "Slice metadata wrong");
      pool.Return(a); a = pool.Parse(2, good, 0, 2, .1f);
      Check(ReferenceEquals(storage, a.bytes), "Chunk bytes not reused");
      Check(b.frames[0].Data[0] == 15, "Recycled chunk overwrote active bytes");
      pool.Return(a); pool.Return(b);
      pool.Return(b); // Duplicate return must not subtract the lease twice.
      pool.GetUsage(out capacity, out payload, out peakPayload, out leases, out peakLeases, out slots);
      Check(capacity == 131072 && payload == 0 && leases == 0 && peakPayload == 140 && peakLeases == 2, "Returned pool usage incorrect");
      Reject(() => pool.Parse(0, bad, 0, 2, .1f));
      Check(pool.HasFreeChunk, "Rejected batch leaked its lease");
      pool.GetUsage(out capacity, out payload, out peakPayload, out leases, out peakLeases, out slots);
      Check(payload == 0 && leases == 0, "Rejected batch leaked usage counters");
      var truncated = (byte[])good.Clone(); Array.Resize(ref truncated, truncated.Length - 3);
      Reject(() => pool.Parse(0, truncated, 0, 2, .1f));
      var falseSize = (byte[])good.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(69u), 0, falseSize, falseSize.Length - 4, 4);
      Reject(() => pool.Parse(0, falseSize, 0, 2, .1f));
      for (int i = 0; i < 1000; i++) { a = pool.Parse(i, good, 0, 2, .1f); pool.Return(a); }
      Check(pool.AllocatedBytes == 131072, "Pool storage grew during replay");
      a = pool.Parse(0, good, 0, 2, .1f); pool.Dispose();
      Check(a.bytes != null && a.frames[0].Data[0] == 15, "Dispose cleared worker-owned bytes");
      pool.Return(a); Check(pool.AllocatedBytes == 0, "Late lease return did not release storage");
      pool.GetUsage(out capacity, out payload, out peakPayload, out leases, out peakLeases, out slots);
      Check(capacity == 0 && payload == 0 && leases == 0, "Disposed pool usage incorrect");
    }
    using (var pool = new EncodedChunkPool(1, 1, 1024)) Reject(() => pool.Parse(0, good, 0, 1, .1f));
    Console.WriteLine("PASS receiver buffers: " + checks + " checks; ring allocation=" + allocations + " bytes");
  }
}
