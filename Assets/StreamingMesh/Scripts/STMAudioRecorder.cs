using UnityEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StreamingMesh.Core.Threading;

namespace StreamingMesh
{
  [RequireComponent(typeof(AudioListener))]
  public sealed class STMAudioRecorder : MonoBehaviour
  {
    public const int EncodedSampleRate = 48000;
    const int EncodedChannels = 2;
    const int BytesPerSample = 2;

#if UNITY_EDITOR
    [Header("Fragmented MP4 audio")]
    [SerializeField] string ffmpegPath = "ffmpeg";
    [Range(32, 320)] [SerializeField] int bitrateKbps = 128;
    [UnityEngine.Serialization.FormerlySerializedAs("segmentDuration")]
    [Tooltip("Target duration of each audio segment. Actual durations vary to align with encoded audio frame boundaries. When STMHttpSender starts recording, this is overridden with Combined Frames / Frame Rate (minimum 0.25 seconds).")]
    [Min(0.25f)] [SerializeField] float targetSegmentDurationSeconds = 1.024f;
    [Range(2, 30)] [SerializeField] int maximumQueuedSeconds = 8;

    Pcm16RingBuffer pcmRing;
    readonly HashSet<string> emittedFragments = new HashSet<string>();
    string emittedPlaylist;

    sealed class AudioFragment
    {
      public string name;
      public byte[] data;
      public int samples;
    }
    sealed class EncoderOutput
    {
      public byte[] init;
      public string playlist;
      public readonly List<AudioFragment> fragments = new List<AudioFragment>();
    }
    Task<EncoderOutput> outputRead;
    double nextOutputPoll;

    Process process;
    Thread writerThread;
    string outputDirectory;
    string encoderError;
    int inputSampleRate;
    long nextStartSample;
    uint nextSequence;
    bool initEmitted;
    bool queueOverflowed;
    bool overflowReported;
    bool writerFailed;
    volatile bool stopRequested;
    volatile bool startRecord;
    public bool TryGetFirstPcmDspTime(out double time)
    {
      var ring = Volatile.Read(ref pcmRing);
      if (ring != null) return ring.TryGetFirstDspTime(out time);
      time = 0;
      return false;
    }

    public delegate void Fmp4InitData(string fileName, byte[] data);
    public delegate void Fmp4FragmentData(
      uint sequence,
      long startSample,
      int sampleCount,
      string fileName,
      byte[] data);
    public delegate void Fmp4PlaylistData(string fileName, byte[] data);

    public Fmp4InitData OnFmp4InitData;
    public Fmp4FragmentData OnFmp4FragmentData;
    public Fmp4PlaylistData OnFmp4PlaylistData;

    public bool IsStartRecord { get { return startRecord; } }
    public void Record(float durationSeconds)
    {
      if (startRecord) return;
      targetSegmentDurationSeconds = Mathf.Max(0.25f, durationSeconds);
      Record();
    }
    public string OutputDirectory { get { return outputDirectory; } }

    void OnEnable() => AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
    void OnDisable()
    {
      AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
      StopCapture();
    }
    void OnAudioConfigurationChanged(bool deviceWasChanged)
    {
      if (!startRecord) return;
      StopCapture();
      UnityEngine.Debug.LogError("StreamingMesh audio configuration changed. Restart recording with the new sample rate.");
    }
    void StopCapture()
    {
      Stop();
      var sender = GetComponent<STMHttpSender>();
      if (sender != null && sender.IsStartRecord) sender.Stop();
    }

    public void Record()
    {
      if (startRecord)
        return;
      if (writerThread != null && writerThread.IsAlive)
      {
        UnityEngine.Debug.LogError("StreamingMesh audio encoder is still stopping.");
        return;
      }

      inputSampleRate = AudioSettings.outputSampleRate;
      outputDirectory = Path.Combine(
        Application.temporaryCachePath,
        "StreamingMeshAudio",
        Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(outputDirectory);

      emittedFragments.Clear();
      outputRead = null;
      nextOutputPoll = 0;
      emittedPlaylist = null;
      nextStartSample = 0;
      nextSequence = 0;
      initEmitted = false;
      queueOverflowed = false;
      overflowReported = false;
      Volatile.Write(ref writerFailed, false);
      encoderError = null;
      stopRequested = false;

      try
      {
        DisposeProcess();
        if (inputSampleRate <= 0) throw new InvalidOperationException("Audio output sample rate is unavailable.");
        int capacity = checked(inputSampleRate * EncodedChannels * BytesPerSample * Math.Max(1, maximumQueuedSeconds));
        var previousRing = Volatile.Read(ref pcmRing);
        byte[] storage = previousRing != null && previousRing.IsDrained && previousRing.Storage.Length == capacity
          ? previousRing.Storage : new byte[capacity];
        // A fresh wrapper keeps an old callback closed even when its storage is reused.
        Volatile.Write(ref pcmRing, new Pcm16RingBuffer(storage));
        StartEncoderProcess();
        var sessionRing = pcmRing;
        var encoder = process;
        writerThread = new Thread(() => WritePcmLoop(sessionRing, encoder))
        {
          IsBackground = true,
          Name = "StreamingMesh fMP4 audio writer"
        };
        startRecord = true;
        writerThread.Start();
      }
      catch (Exception exception)
      {
        startRecord = false;
        stopRequested = true;
        Volatile.Read(ref pcmRing)?.Close();
        DisposeProcess();
        UnityEngine.Debug.LogError("StreamingMesh could not start FFmpeg: " + exception.Message);
      }
    }

    public void Stop()
    {
      if (!startRecord && stopRequested)
        return;
      startRecord = false;
      stopRequested = true;
      Volatile.Read(ref pcmRing)?.Close();
    }

    void StartEncoderProcess()
    {
      string duration = targetSegmentDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
      string arguments = string.Format(
        CultureInfo.InvariantCulture,
        "-hide_banner -loglevel warning -f s16le -ar {0} -ac {1} -i pipe:0 " +
        "-vn -c:a aac -b:a {2}k -ar {3} -ac {1} " +
        "-f hls -hls_time {4} -hls_list_size 0 -hls_segment_type fmp4 " +
        "-hls_fmp4_init_filename audio-init.mp4 " +
        "-hls_segment_filename audio-%06d.m4s " +
        "-hls_flags append_list+omit_endlist+temp_file audio.m3u8",
        inputSampleRate,
        EncodedChannels,
        bitrateKbps,
        EncodedSampleRate,
        duration);

      ProcessStartInfo startInfo = new ProcessStartInfo
      {
        FileName = ffmpegPath,
        Arguments = arguments,
        WorkingDirectory = outputDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardError = true
      };

      process = new Process { StartInfo = startInfo };
      process.ErrorDataReceived += (sender, args) =>
      {
        if (!string.IsNullOrEmpty(args.Data))
          encoderError = args.Data;
      };
      if (!process.Start())
        throw new InvalidOperationException("FFmpeg did not start.");
      process.BeginErrorReadLine();
    }

    void OnAudioFilterRead(float[] data, int channels)
    {
      if (!startRecord || data == null || data.Length == 0 || channels <= 0)
        return;

      double blockDsp = AudioSettings.dspTime;
      var ring = Volatile.Read(ref pcmRing);
      if (ring != null && !ring.TryWrite(data, channels, blockDsp) && !ring.IsClosed)
      {
        // Close immediately so later callbacks cannot resume after a missing block.
        Volatile.Write(ref queueOverflowed, true);
        startRecord = false;
        ring.Close();
      }
    }

    void WritePcmLoop(Pcm16RingBuffer ring, Process encoder)
    {
      try
      {
        Stream input = encoder.StandardInput.BaseStream;
        while (true)
        {
          if (ring.WriteAvailable(input) != 0) continue;
          if (ring.IsDrained) break;
          // Only the writer waits; the audio thread performs no lock/event/syscall.
          Thread.Sleep(1);
        }
        input.Flush();
        encoder.StandardInput.Close();
        encoder.WaitForExit();
      }
      catch (Exception exception)
      {
        encoderError = exception.Message;
        startRecord = false;
        ring.Close();
        Volatile.Write(ref writerFailed, true);
      }
    }

    void Update()
    {
      PollEncoderOutput();

      if (Volatile.Read(ref queueOverflowed) && !overflowReported)
      {
        overflowReported = true;
        UnityEngine.Debug.LogError(
          "StreamingMesh audio PCM queue overflowed. Recording was stopped to avoid A/V drift.");
        StopCapture();
      }

      if (Volatile.Read(ref writerFailed))
      {
        Volatile.Write(ref writerFailed, false);
        StopCapture();
      }

      if (!string.IsNullOrEmpty(encoderError))
      {
        string error = encoderError;
        encoderError = null;
        UnityEngine.Debug.LogWarning("FFmpeg audio encoder: " + error);
      }
    }

    void PollEncoderOutput()
    {
      if (string.IsNullOrEmpty(outputDirectory)) return;
      if (outputRead != null)
      {
        if (!outputRead.IsCompleted) return;
        var completed = outputRead;
        outputRead = null;
        if (completed.IsFaulted)
          UnityEngine.Debug.LogWarning("StreamingMesh audio output read failed: " + completed.Exception.GetBaseException().Message);
        else if (!completed.IsCanceled)
        {
          var result = completed.Result;
          if (result.init != null)
          {
            initEmitted = true;
            OnFmp4InitData?.Invoke("audio-init.mp4", result.init);
          }
          foreach (var fragment in result.fragments)
          {
            emittedFragments.Add(fragment.name);
            OnFmp4FragmentData?.Invoke(nextSequence++, nextStartSample, fragment.samples, fragment.name, fragment.data);
            nextStartSample += fragment.samples;
          }
          if (result.playlist != null)
          {
            emittedPlaylist = result.playlist;
            OnFmp4PlaylistData?.Invoke("audio.m3u8", System.Text.Encoding.UTF8.GetBytes(result.playlist));
          }
        }
      }
      if (Time.realtimeSinceStartupAsDouble < nextOutputPoll) return;
      nextOutputPoll = Time.realtimeSinceStartupAsDouble + 0.1;
      string directory = outputDirectory, previous = emittedPlaylist;
      bool needsInit = !initEmitted;
      var sent = new HashSet<string>(emittedFragments);
      outputRead = Task.Run(() => ReadEncoderOutput(directory, previous, needsInit, sent));
    }

    static EncoderOutput ReadEncoderOutput(string directory, string previous, bool needsInit, HashSet<string> sent)
    {
      var result = new EncoderOutput();
      string path = Path.Combine(directory, "audio.m3u8");
      string[] lines;
      try { if (!File.Exists(path)) return result; lines = File.ReadAllLines(path); }
      catch (IOException) { return result; }
      // FFmpeg publishes this playlist by rename after finishing the init/fragment.
      // Do not expose a still-being-created init file before the first playlist.
      if (needsInit)
      {
        result.init = TryReadCompletedFile(Path.Combine(directory, "audio-init.mp4"));
        if (result.init == null) return result;
      }
      string playlist = string.Join("\n", lines) + "\n";
      if (playlist == previous) return result;
      double duration = 0;
      foreach (string raw in lines)
      {
        string line = raw.Trim();
        if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
        {
          double.TryParse(line.Substring(8).TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
          continue;
        }
        if (line.Length == 0 || line[0] == '#') continue;
        string name = Path.GetFileName(line);
        if (sent.Contains(name)) continue;
        byte[] data = TryReadCompletedFile(Path.Combine(directory, name));
        // Do not advertise an unavailable fragment, or skip its sample interval.
        if (data == null) return result;
        result.fragments.Add(new AudioFragment {name=name, data=data,
          samples=Math.Max(1, (int)Math.Round(duration * EncodedSampleRate))});
      }
      result.playlist = playlist;
      return result;
    }

    static byte[] TryReadCompletedFile(string path)
    {
      try
      {
        FileInfo info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
          return null;
        return File.ReadAllBytes(path);
      }
      catch (IOException)
      {
        return null;
      }
    }

    void OnDestroy()
    {
      Stop();
      if (writerThread != null && writerThread.IsAlive)
        writerThread.Join(500);
      DisposeProcess();
    }

    void DisposeProcess()
    {
      if (process == null)
        return;
      try
      {
        if (!process.HasExited)
          process.Kill();
      }
      catch
      {
      }
      process.Dispose();
      process = null;
    }
#else
    public bool IsStartRecord { get { return false; } }
    public void Record() { }
    public void Stop() { }
#endif
  }
}
