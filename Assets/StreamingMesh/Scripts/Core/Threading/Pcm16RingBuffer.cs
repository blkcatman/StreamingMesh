using System;
using System.IO;
using System.Threading;

namespace StreamingMesh.Core.Threading
{
  /// <summary>One audio producer, one stream writer. Publish only completed stereo PCM frames.</summary>
  internal sealed class Pcm16RingBuffer
  {
    const int BytesPerFrame = 4;
    readonly byte[] buffer;
    long written, consumed;
    int producerActive, closed, hasFirstPcm;
    double firstDspTime;

    internal Pcm16RingBuffer(byte[] storage)
    {
      if (storage == null || storage.Length == 0 || storage.Length % BytesPerFrame != 0)
        throw new ArgumentException("PCM storage must contain complete stereo 16-bit frames.", nameof(storage));
      buffer = storage;
    }

    internal byte[] Storage => buffer;
    internal bool IsClosed => Volatile.Read(ref closed) != 0;
    internal bool IsDrained => IsClosed && Volatile.Read(ref producerActive) == 0 &&
      Volatile.Read(ref consumed) == Volatile.Read(ref written);

    internal bool TryGetFirstDspTime(out double time)
    {
      bool available = Volatile.Read(ref hasFirstPcm) != 0;
      time = available ? firstDspTime : 0;
      return available;
    }

    internal void Close() => Volatile.Write(ref closed, 1);

    // Never wait for the consumer. A rejected block must stop capture, not skip its samples.
    internal bool TryWrite(float[] data, int channels, double dspTime)
    {
      if (data == null || channels <= 0 || data.Length == 0 || data.Length % channels != 0 ||
          Interlocked.CompareExchange(ref producerActive, 1, 0) != 0) return false;
      try
      {
        if (IsClosed) return false;
        int frames = data.Length / channels;
        long length = (long)frames * BytesPerFrame;
        long cursor = written;
        if (length > buffer.Length - (cursor - Volatile.Read(ref consumed))) return false;
        int offset = (int)(cursor % buffer.Length);
        for (int frame = 0; frame < frames; frame++)
        {
          float left = data[frame * channels];
          float right = channels > 1 ? data[frame * channels + 1] : left;
          WriteSample(offset, left);
          WriteSample(offset + 2, right);
          offset += BytesPerFrame;
          if (offset == buffer.Length) offset = 0;
        }
        if (hasFirstPcm == 0)
        {
          firstDspTime = dspTime;
          Volatile.Write(ref hasFirstPcm, 1);
        }
        Volatile.Write(ref written, cursor + length);
        return true;
      }
      finally { Volatile.Write(ref producerActive, 0); }
    }

    void WriteSample(int offset, float value)
    {
      double clamped = Math.Max(-1.0, Math.Min(1.0, value));
      short sample = (short)Math.Round(clamped * 32767.0);
      buffer[offset] = (byte)sample;
      buffer[offset + 1] = (byte)(sample >> 8);
    }

    internal int WriteAvailable(Stream destination)
    {
      long cursor = consumed;
      long available = Volatile.Read(ref written) - cursor;
      if (available == 0) return 0;
      int offset = (int)(cursor % buffer.Length);
      int length = (int)Math.Min(available, Math.Min(buffer.Length - offset, 65536));
      destination.Write(buffer, offset, length);
      // The producer cannot reuse this range while a slow/blocked Write still owns it.
      Volatile.Write(ref consumed, cursor + length);
      return length;
    }
  }
}
