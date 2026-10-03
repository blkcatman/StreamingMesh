using System;
using System.IO;
using Unity.Collections;

namespace StreamingMesh.Core.Serialization
{
  // Borrowed native memory. The owner must outlive this stream (DownloadHandler
  // or readable Texture); Dispose never frees the owner's memory.
  public sealed class NativeBufferStream : Stream
  {
    readonly NativeArray<byte>.ReadOnly data;
    int position;
    public NativeBufferStream(NativeArray<byte>.ReadOnly data) { this.data = data; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => position; set {
      if (value < 0 || value > data.Length) throw new ArgumentOutOfRangeException(nameof(value));
      position = (int)value;
    } }
    public override int Read(byte[] buffer, int offset, int count)
    {
      if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentException("Invalid buffer.");
      int size = Math.Min(count, data.Length - position);
      for (int i = 0; i < size; i++) buffer[offset + i] = data[position + i];
      position += size; return size;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
      Position = offset + (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? position : data.Length);
      return Position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
