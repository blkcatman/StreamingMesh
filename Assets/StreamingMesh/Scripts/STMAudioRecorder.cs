using UnityEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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

    readonly object queueLock = new object();
    readonly Queue<byte[]> pcmQueue = new Queue<byte[]>();
    readonly AutoResetEvent queueSignal = new AutoResetEvent(false);
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
    int queuedBytes;
    long nextStartSample;
    uint nextSequence;
    bool initEmitted;
    bool queueOverflowed;
    bool overflowReported;
    volatile bool stopRequested;
    volatile bool startRecord;

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

      lock (queueLock)
      {
        pcmQueue.Clear();
        queuedBytes = 0;
      }
      emittedFragments.Clear();
      outputRead = null;
      nextOutputPoll = 0;
      emittedPlaylist = null;
      nextStartSample = 0;
      nextSequence = 0;
      initEmitted = false;
      queueOverflowed = false;
      overflowReported = false;
      encoderError = null;
      stopRequested = false;

      try
      {
        DisposeProcess();
        StartEncoderProcess();
        startRecord = true;
        writerThread = new Thread(WritePcmLoop)
        {
          IsBackground = true,
          Name = "StreamingMesh fMP4 audio writer"
        };
        writerThread.Start();
      }
      catch (Exception exception)
      {
        startRecord = false;
        stopRequested = true;
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
      queueSignal.Set();
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

      byte[] pcm = ConvertToStereoPcm16(data, channels);
      int maxQueuedBytes = inputSampleRate * EncodedChannels * BytesPerSample * maximumQueuedSeconds;
      lock (queueLock)
      {
        if (queuedBytes + pcm.Length > maxQueuedBytes)
        {
          queueOverflowed = true;
          return;
        }
        pcmQueue.Enqueue(pcm);
        queuedBytes += pcm.Length;
      }
      queueSignal.Set();
    }

    static byte[] ConvertToStereoPcm16(float[] data, int channels)
    {
      int frameCount = data.Length / channels;
      byte[] output = new byte[frameCount * EncodedChannels * BytesPerSample];
      for (int frame = 0; frame < frameCount; frame++)
      {
        float left = data[frame * channels];
        float right = channels > 1 ? data[frame * channels + 1] : left;
        WritePcm16(output, frame * 4, left);
        WritePcm16(output, frame * 4 + 2, right);
      }
      return output;
    }

    static void WritePcm16(byte[] output, int offset, float value)
    {
      double clamped = Math.Max(-1.0, Math.Min(1.0, value));
      short sample = (short)Math.Round(clamped * 32767.0);
      output[offset] = (byte)sample;
      output[offset + 1] = (byte)(sample >> 8);
    }

    void WritePcmLoop()
    {
      try
      {
        Stream input = process.StandardInput.BaseStream;
        while (true)
        {
          byte[] pcm = null;
          lock (queueLock)
          {
            if (pcmQueue.Count > 0)
            {
              pcm = pcmQueue.Dequeue();
              queuedBytes -= pcm.Length;
            }
          }

          if (pcm != null)
          {
            input.Write(pcm, 0, pcm.Length);
            continue;
          }
          if (stopRequested)
            break;
          queueSignal.WaitOne(100);
        }
        input.Flush();
        process.StandardInput.Close();
        process.WaitForExit();
      }
      catch (Exception exception)
      {
        encoderError = exception.Message;
      }
    }

    void Update()
    {
      PollEncoderOutput();

      if (queueOverflowed && !overflowReported)
      {
        overflowReported = true;
        UnityEngine.Debug.LogError(
          "StreamingMesh audio PCM queue overflowed. Recording was stopped to avoid A/V drift.");
        Stop();
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
      queueSignal.Dispose();
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
