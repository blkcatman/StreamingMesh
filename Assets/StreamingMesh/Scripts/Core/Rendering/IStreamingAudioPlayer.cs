using System;
using StreamingMesh.Core.Serialization;

namespace StreamingMesh.Core.Rendering
{
  public interface IStreamingAudioPlayer : IDisposable
  {
    bool Initialize(string channelUrl, ChannelInfo channelInfo);
    bool TryGetTime(out double time);
    int State { get; }
    // Initialization and seeking must leave playback paused until the receiver is ready.
    void SetPlaying(bool playing);
    void Seek(double time);
  }

  public static class StreamingAudioPlayerFactory
  {
    public static IStreamingAudioPlayer Create()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
      return new WebFmp4AudioPlayer();
#elif UNITY_ANDROID && !UNITY_EDITOR
      return new AndroidFmp4AudioPlayer();
#elif UNITY_IOS || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
      return new AppleFmp4AudioPlayer();
#else
      return null;
#endif
    }
  }
}
