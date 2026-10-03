using System;
using System.IO;
using System.IO.Compression;

namespace StreamingMesh.Core.Rendering
{
  // Fixed storage: advancing the head does not allocate collection nodes or
  // move payload bytes. Indexed insertion also handles out-of-order chunks.
  internal sealed class FrameRing<T>
  {
    readonly T[] items;
    int head;
    public int Count { get; private set; }
    public int Capacity => items.Length;
    public FrameRing(int capacity)
    {
      if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
      items = new T[capacity];
    }
    public T this[int index]
    {
      get { Check(index); return items[(head + index) % items.Length]; }
      set { Check(index); items[(head + index) % items.Length] = value; }
    }
    void Check(int index) { if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index)); }
    public void Add(T item) { Insert(Count, item); }
    public void Insert(int index, T item)
    {
      if (Count == items.Length) throw new InvalidOperationException("Receiver frame ring is full.");
      if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
      for (int i = Count; i > index; i--) items[(head + i) % items.Length] = items[(head + i - 1) % items.Length];
      items[(head + index) % items.Length] = item;
      Count++;
    }
    public T RemoveAt(int index)
    {
      T item = this[index];
      if (index == 0)
      {
        items[head] = default(T);
        head = (head + 1) % items.Length;
      }
      else
      {
        for (int i = index; i < Count - 1; i++) items[(head + i) % items.Length] = items[(head + i + 1) % items.Length];
        items[(head + Count - 1) % items.Length] = default(T);
      }
      Count--;
      return item;
    }
    public void Clear() { while (Count > 0) RemoveAt(0); }
  }

  internal struct EncodedFrameSlice
  {
    public uint sequence;
    public double presentationTime;
    public bool isKeyframe;
    public int offset, length;
    public EncodedChunkPool.Chunk chunk;
    public ArraySegment<byte> Data => new ArraySegment<byte>(chunk.bytes, offset, length);
  }

  // A lease belongs to the parser until the whole chunk is validated. After
  // commit, each queued slice retains it until decode has copied/read the data.
  // Rent/Return can cross the parsing worker and Unity threads; queued metadata
  // itself is only modified on the Unity thread.
  internal sealed class EncodedChunkPool : IDisposable
  {
    internal sealed class Chunk
    {
      public byte[] bytes;
      public readonly EncodedFrameSlice[] frames;
      public int count, retained, index;
      internal bool leased;
      internal Chunk(int framesPerChunk) { frames = new EncodedFrameSlice[framesPerChunk]; }
    }
    readonly object gate = new object();
    readonly Chunk[] chunks;
    readonly int maxChunkBytes, maxFrameBytes;
    readonly long budget;
    long allocated;
    bool disposed;
    public long AllocatedBytes { get { lock (gate) return allocated; } }
    public bool HasFreeChunk
    {
      get { lock (gate) { if (disposed) return false; foreach (var chunk in chunks) if (!chunk.leased) return true; return false; } }
    }
    public EncodedChunkPool(int slots, int framesPerChunk, int maximumFrameBytes,
      int maximumChunkBytes = 128 * 1024 * 1024, long byteBudget = 256L * 1024 * 1024)
    {
      if (slots < 1 || framesPerChunk < 1 || maximumFrameBytes < 29 || maximumChunkBytes < 4 || byteBudget < 4)
        throw new ArgumentOutOfRangeException();
      chunks = new Chunk[slots];
      for (int i = 0; i < slots; i++) chunks[i] = new Chunk(framesPerChunk);
      maxChunkBytes = maximumChunkBytes; maxFrameBytes = maximumFrameBytes; budget = byteBudget;
    }
    Chunk Rent(int size)
    {
      lock (gate)
      {
        if (disposed) throw new ObjectDisposedException(nameof(EncodedChunkPool));
        // Prefer an already large enough lease, rather than growing every slot.
        Chunk candidate = null;
        foreach (var chunk in chunks)
        {
          if (chunk.leased) continue;
          if (chunk.bytes != null && chunk.bytes.Length >= size) { candidate = chunk; break; }
          if (candidate == null) candidate = chunk;
        }
        if (candidate == null) throw new InvalidOperationException("Receiver chunk pool is full.");
        int oldSize = candidate.bytes == null ? 0 : candidate.bytes.Length;
        if (oldSize < size)
        {
          // Round modestly to avoid resizing on small capture-size variations.
          int capacity = checked((int)(((long)size + 65535) / 65536 * 65536));
          if (allocated - oldSize + capacity > budget) throw new InvalidDataException("Receiver chunk memory budget exceeded.");
          candidate.bytes = new byte[capacity];
          allocated += capacity - oldSize;
        }
        candidate.leased = true; candidate.count = 0; candidate.retained = 0;
        return candidate;
      }
    }
    public void Return(Chunk chunk)
    {
      lock (gate)
      {
        Array.Clear(chunk.frames, 0, chunk.count);
        chunk.count = 0; chunk.retained = 0; chunk.leased = false;
        if (disposed && chunk.bytes != null) { allocated -= chunk.bytes.Length; chunk.bytes = null; }
      }
    }
    public Chunk Parse(int chunkIndex, byte[] compressed, long ticks, int combined, float interval)
    {
      if (compressed == null || compressed.Length < 18 || compressed[0] != 0x1f || compressed[1] != 0x8b)
        throw new InvalidDataException("Missing GZip chunk header or trailer.");
      // ISIZE is only an allocation hint. Both bounds and actual decompressed
      // length are verified before a single frame can be committed.
      uint size = BitConverter.ToUInt32(compressed, compressed.Length - 4);
      if (size < 4 || size > maxChunkBytes) throw new InvalidDataException("Invalid decompressed chunk size.");
      Chunk chunk = Rent((int)size);
      chunk.index = chunkIndex;
      try
      {
        using (var input = new MemoryStream(compressed, false))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        {
          int read = 0;
          while (read < size)
          {
            int n = gzip.Read(chunk.bytes, read, (int)size - read);
            if (n == 0) throw new InvalidDataException("Truncated stream chunk.");
            read += n;
          }
          if (gzip.ReadByte() != -1) throw new InvalidDataException("Unexpected decompressed chunk data.");
        }
        int count = BitConverter.ToInt32(chunk.bytes, 0);
        if (count < 0 || count > chunk.frames.Length || 4L * (count + 1) > size)
          throw new InvalidDataException("Invalid frame size table.");
        int offset = (count + 1) * 4;
        double start = ticks / (double)TimeSpan.TicksPerSecond;
        for (int i = 0; i < count; i++)
        {
          int length = BitConverter.ToInt32(chunk.bytes, (i + 1) * 4);
          if (length < 21 || length > maxFrameBytes || length > size - offset)
            throw new InvalidDataException("Invalid frame size.");
          byte version = chunk.bytes[offset + 8];
          if (version >= 2 && length < 29) throw new InvalidDataException("Missing PTS header.");
          chunk.frames[i] = new EncodedFrameSlice {
            chunk = chunk, offset = offset, length = length,
            sequence = version >= 1 ? BitConverter.ToUInt32(chunk.bytes, offset + 1) : checked((uint)((long)chunkIndex * combined + i)),
            presentationTime = version >= 2 ? BitConverter.ToInt64(chunk.bytes, offset + 21) / (double)TimeSpan.TicksPerSecond : start + i * interval,
            isKeyframe = chunk.bytes[offset] == 0x0f
          };
          chunk.count = i + 1; offset += length;
        }
        if (offset != size) throw new InvalidDataException("Unexpected trailing chunk data.");
        return chunk;
      }
      catch { Return(chunk); throw; }
    }
    public void Dispose()
    {
      lock (gate)
      {
        disposed = true;
        // In-flight workers keep their leases until their finally/Return.
        foreach (var chunk in chunks)
          if (!chunk.leased && chunk.bytes != null) { allocated -= chunk.bytes.Length; chunk.bytes = null; }
      }
    }
  }
}
