using System;

namespace StreamingMesh.Core.Rendering
{
  public static class ReceiverPrefetchWindow
  {
    // Includes the file currently being consumed; payload arrays grow lazily.
    public const int DefaultVertexChunks = 3;
    public const int MaximumVertexChunks = 16;
    public const int DefaultWebAudioChunks = 3;
    public const double DefaultWebAudioBackSeconds = 30;
    public const double MinimumSeconds = 0.5;
    public const double MaximumSeconds = 120;

    // Playlist ranges are half-open: a file ending at the playhead is consumed.
    public static bool IsConsumed(long startTicks, long endTicks, double timeSeconds, long timebaseHz)
    {
      if (timebaseHz <= 0 || startTicks < 0 || endTicks <= startTicks)
        throw new ArgumentException("Invalid stream timestamp range.");
      return endTicks / (double)timebaseHz <= timeSeconds;
    }

    public static void Validate(double seconds, bool allowZero = false)
    {
      if (double.IsNaN(seconds) || double.IsInfinity(seconds) ||
          seconds < (allowZero ? 0 : MinimumSeconds) || seconds > MaximumSeconds)
        throw new ArgumentOutOfRangeException(nameof(seconds));
    }

    public static void ValidateChunks(int chunks)
    {
      if (chunks < 1 || chunks > MaximumVertexChunks)
        throw new ArgumentOutOfRangeException(nameof(chunks));
    }

    public static void Layout(int chunks, int combined,
      out int threshold, out int frameCapacity, out int chunkSlots)
    {
      ValidateChunks(chunks);
      if (combined < 1 || combined > 4096) throw new ArgumentOutOfRangeException(nameof(combined));
      // Reserve room for an entire file; the metadata cap can limit large chunks.
      frameCapacity = Math.Min(8192, chunks * combined);
      threshold = frameCapacity - combined + 1;
      chunkSlots = Math.Min(chunks, frameCapacity / combined);
    }
  }
}
