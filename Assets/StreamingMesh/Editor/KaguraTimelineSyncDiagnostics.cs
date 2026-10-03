using System;
using System.Collections;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using StreamingMesh.Samples;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Audio;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using TimeWire.Unity;

namespace StreamingMesh.Editor
{
    // Editor-only instrumentation. The full run preserves the playback clock;
    // hitch tests change its mode only inside Play Mode. Nothing is saved into the scene.
    [InitializeOnLoad]
    public static class KaguraTimelineSyncDiagnostics
    {
        const string PendingKey = "StreamingMesh.KaguraTimelineSync.Pending";
        const string StressKey = "StreamingMesh.KaguraTimelineSync.StressMode";

        static KaguraTimelineSyncDiagnostics()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem("Tools/StreamingMesh/KAGURA/Verify Timeline Audio Sync")]
        public static void Verify()
        {
            SessionState.SetInt(StressKey, -1);
            Run();
        }

        [MenuItem("Tools/StreamingMesh/KAGURA/Verify DSP Clock With Hitch")]
        public static void VerifyDspHitch()
        {
            SessionState.SetInt(StressKey, (int)DirectorUpdateMode.DSPClock);
            Run();
        }

        [MenuItem("Tools/StreamingMesh/KAGURA/Verify Game Clock With Hitch")]
        public static void VerifyGameHitch()
        {
            SessionState.SetInt(StressKey, (int)DirectorUpdateMode.GameTime);
            Run();
        }

        [MenuItem("Tools/StreamingMesh/KAGURA/Verify TimeWire Clock With Hitch")]
        public static void VerifyTimeWireHitch()
        {
            SessionState.SetInt(StressKey, 100);
            Run();
        }

        [MenuItem("Tools/StreamingMesh/KAGURA/Verify TimeWire Full Song")]
        public static void VerifyTimeWireFullSong()
        {
            SessionState.SetInt(StressKey, -2);
            Run();
        }

        static void Run()
        {
            if (EditorApplication.isPlaying)
            {
                Begin();
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene("Assets/Samples/UnityChanKAGURA/Scenes/KaguraDemo.unity");
            SessionState.SetBool(PendingKey, true);
            EditorApplication.isPlaying = true;
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(PendingKey, false)) return;
            SessionState.SetBool(PendingKey, false);
            EditorApplication.delayCall += Begin;
        }

        static void Begin()
        {
            var controls = UnityEngine.Object.FindAnyObjectByType<KaguraDemoControls>();
            if (controls == null || controls.director == null || (controls.sender != null && controls.sender.IsStartRecord))
                throw new InvalidOperationException("A non-recording KaguraDemo scene is required.");
            if (UnityEngine.Object.FindAnyObjectByType<KaguraTimelineSyncProbe>() != null)
                throw new InvalidOperationException("A Timeline sync measurement is already running.");
            var listener = UnityEngine.Object.FindAnyObjectByType<AudioListener>();
            if (listener == null) throw new InvalidOperationException("AudioListener is required.");
            var probe = listener.gameObject.AddComponent<KaguraTimelineSyncProbe>();
            probe.hideFlags = HideFlags.DontSave;
            int stressMode = SessionState.GetInt(StressKey, -1);
            SessionState.SetInt(StressKey, -1);
            // Isolate this measurement from clocks saved in the sample scene.
            if (controls.timelineClock != null) controls.timelineClock.enabled = false;
            if (controls.playbackClock != null) controls.playbackClock.Pause();
            probe.Begin(controls.director, stressMode,
                stressMode == -2 && controls.sender != null ? controls.sender.captureClockSource : null);
        }
    }

    public sealed class KaguraTimelineSyncProbe : MonoBehaviour
    {
        struct AudioBlock
        {
            public double dsp, monotonic;
            public long sample;
            public int window, offset, count, channels;
        }

        sealed class CaptureWindow
        {
            public double from, to;
            public float[] pcm;
            public int count;
        }

        [Serializable]
        sealed class Configuration
        {
            public string unity, directorUpdateMode, output;
            public double audioStart, duration, audioDuration, startDsp, startRealtime, startMonotonic;
            public int outputFrequency, referenceFrequency, referenceChannels, dspBufferFrames, dspBufferCount;
            public float timeScale, maximumDeltaTime;
            public int targetFrameRate, vSyncCount;
            public bool senderRecording;
            public bool controlledHitch;
            public bool timeWireEnabled;
            public string timeWireSource;
            public double hitchAtSeconds, hitchSleepSeconds, timelineDuration;
            public double[] referenceTimes;
        }

        readonly AudioBlock[] blocks = new AudioBlock[20000];
        readonly StringBuilder clocks = new StringBuilder(4 * 1024 * 1024);
        readonly StringBuilder phases = new StringBuilder(1024 * 1024);
        double[] referenceTimes = { 4, 10, 120, 128, 246, 254 };
        CaptureWindow[] windows = {
            new CaptureWindow { from = 0, to = 20 },
            new CaptureWindow { from = 120, to = 142 },
            new CaptureWindow { from = 246, to = 268 }
        };
        PlayableDirector director;
        AudioClip song;
        AudioClipPlayable audioPlayable;
        AnimationClipPlayable bodyPlayable;
        Configuration configuration;
        int blockCount, frequency;
        long audioSamples;
        double startDsp, audioStart, nextLog, nextSample, sumDelta, sumUnscaled;
        float sumDeltaFloat;
        string output;
        bool initialized, finished, phasePending, saved, hitchDone;
        volatile bool capturing;

        static double Monotonic => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

        public void Begin(PlayableDirector target, int stressMode, ClockSourceBehaviour referenceClock = null)
        {
            director = target;
            foreach (var track in ((TimelineAsset)director.playableAsset).GetOutputTracks())
            {
                if (!(track is AudioTrack)) continue;
                foreach (var clip in track.GetClips())
                {
                    song = ((AudioPlayableAsset)clip.asset).clip;
                    audioStart = clip.start;
                    break;
                }
            }
            if (song == null) throw new InvalidOperationException("Timeline song was not found.");
            output = Path.GetFullPath("Logs/KaguraClockComparison_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(output);
            frequency = AudioSettings.outputSampleRate;
            AudioSettings.GetDSPBufferSize(out int bufferLength, out int bufferCount);
            bool stress = stressMode >= 0;
            bool timeWire = stressMode == 100 || stressMode == -2;
            if (timeWire) director.timeUpdateMode = DirectorUpdateMode.DSPClock;
            if (stress)
            {
                referenceTimes = new double[] { 4, 10, 24, 30 };
                windows = new[] { new CaptureWindow { from = 0, to = 42 } };
                director.timeUpdateMode = timeWire ? DirectorUpdateMode.DSPClock : (DirectorUpdateMode)stressMode;
            }
            foreach (var window in windows)
                window.pcm = new float[frequency * (int)(window.to - window.from + 2) * 2];
            foreach (double time in referenceTimes)
            {
                var reference = new float[(int)(song.frequency * 0.3) * song.channels];
                if (!song.GetData(reference, (int)(song.frequency * time)))
                    throw new InvalidOperationException("Song PCM is not readable.");
                WriteFloats(Path.Combine(output, "reference_" + time + ".f32"), reference, reference.Length);
            }
            clocks.AppendLine("frame,dspBefore,dspAfter,monotonic,realtime,time,unscaledTime,delta,unscaledDelta,sumDelta,sumDeltaFloat,sumUnscaled,timeScale,director,audioPlayable,bodyPlayable,state");
            phases.AppendLine("frame,phase,dsp,monotonic,realtime,director");
            director.Stop();
            director.time = 0;
            director.Play();
            if (timeWire)
            {
                // Measure the playback-correction path with AudioTrack still active.
                // Manual pose evaluation alone would not verify audio playback.
                var host = new GameObject("TimeWire measurement clocks");
                host.hideFlags = HideFlags.DontSave;
                host.SetActive(false);
                var source = referenceClock != null ? referenceClock : host.AddComponent<HybridClockSource>();
                var transport = host.AddComponent<TransportClockSource>();
                var serialized = new SerializedObject(transport);
                serialized.FindProperty("source").objectReferenceValue = source;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var controller = director.gameObject.AddComponent<TimelineClockController>();
                controller.hideFlags = HideFlags.DontSave;
                controller.source = transport;
                controller.director = director;
                controller.mode = TimelineClockMode.CorrectPlayback;
                // AudioTrack must retain its native sample rate. The local source
                // has rate=1; disabling speed correction avoids pitch/time changes.
                controller.maximumSpeedAdjustment = 0;
                controller.preserveNativeRate = true;
                // Let the DSP Director process a stopped main-thread frame before
                // treating its transient lag as a seek. A seek reschedules AudioTrack.
                controller.hardThresholdSeconds = 1;
                controller.loop = false;
                host.SetActive(true);
                controller.SynchronizeNow();
            }
            startDsp = AudioSettings.dspTime;
            configuration = new Configuration {
                unity = Application.unityVersion, directorUpdateMode = director.timeUpdateMode.ToString(), output = output,
                audioStart = audioStart, duration = stress ? 40 : director.duration, timelineDuration = director.duration,
                audioDuration = (double)song.samples / song.frequency,
                outputFrequency = frequency, referenceFrequency = song.frequency, referenceChannels = song.channels,
                dspBufferFrames = bufferLength, dspBufferCount = bufferCount, timeScale = Time.timeScale,
                maximumDeltaTime = Time.maximumDeltaTime, targetFrameRate = Application.targetFrameRate,
                vSyncCount = QualitySettings.vSyncCount, startDsp = startDsp,
                startRealtime = Time.realtimeSinceStartupAsDouble, startMonotonic = Monotonic,
                senderRecording = false, referenceTimes = referenceTimes, controlledHitch = stress,
                timeWireEnabled = timeWire,
                timeWireSource = timeWire ? (referenceClock != null ? referenceClock.GetType().Name : nameof(HybridClockSource)) : "None",
                hitchAtSeconds = stress ? 20 : 0, hitchSleepSeconds = stress ? 0.6 : 0
            };
            File.WriteAllText(Path.Combine(output, "configuration.json"), JsonUtility.ToJson(configuration, true));
            nextLog = startDsp;
            capturing = initialized = true;
            StartCoroutine(SampleEndOfFrame());
            Debug.Log("KAGURA clock comparison started: mode=" + director.timeUpdateMode + ", output=" + output);
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!capturing) return;
            double dsp = AudioSettings.dspTime;
            double monotonic = Monotonic;
            int windowIndex = -1, offset = -1;
            for (int i = 0; i < windows.Length; i++)
            {
                var window = windows[i];
                if (dsp - startDsp < window.from || dsp - startDsp >= window.to) continue;
                if (window.count + data.Length > window.pcm.Length) break;
                windowIndex = i;
                offset = window.count;
                Array.Copy(data, 0, window.pcm, offset, data.Length);
                window.count += data.Length;
                break;
            }
            int index = blockCount;
            if (index < blocks.Length)
            {
                blocks[index] = new AudioBlock { dsp = dsp, monotonic = monotonic, sample = audioSamples,
                    window = windowIndex, offset = offset, count = data.Length, channels = channels };
                System.Threading.Volatile.Write(ref blockCount, index + 1);
            }
            audioSamples += data.Length / channels;
        }

        void LateUpdate()
        {
            if (!initialized || finished) return;
            if (configuration.controlledHitch && !hitchDone && Monotonic - configuration.startMonotonic >= 20)
            {
                hitchDone = true;
                Debug.Log("KAGURA controlled main-thread hitch: 600 ms; audio thread continues.");
                System.Threading.Thread.Sleep(600);
            }
            sumDelta += Time.deltaTime;
            sumDeltaFloat += Time.deltaTime;
            sumUnscaled += Time.unscaledDeltaTime;
            double dsp = AudioSettings.dspTime;
            if (!audioPlayable.IsValid() || !bodyPlayable.IsValid()) FindPlayables();
            double realtime = Time.realtimeSinceStartupAsDouble;
            // Sample at <=120 Hz, but accumulate deltaTime on every actual Editor frame.
            if (realtime >= nextSample)
            {
                nextSample = realtime + 1.0 / 120;
                double monotonic = Monotonic;
                double time = Time.timeAsDouble;
                double unscaled = Time.unscaledTimeAsDouble;
                double timeline = director.time;
                double audio = audioPlayable.IsValid() ? audioPlayable.GetTime() : double.NaN;
                double body = bodyPlayable.IsValid() ? bodyPlayable.GetTime() : double.NaN;
                double dspAfter = AudioSettings.dspTime;
                clocks.Append(Time.frameCount).Append(',').Append(F(dsp)).Append(',').Append(F(dspAfter)).Append(',')
                    .Append(F(monotonic)).Append(',').Append(F(realtime)).Append(',').Append(F(time)).Append(',')
                    .Append(F(unscaled)).Append(',').Append(F(Time.deltaTime)).Append(',').Append(F(Time.unscaledDeltaTime)).Append(',')
                    .Append(F(sumDelta)).Append(',').Append(F(sumDeltaFloat)).Append(',').Append(F(sumUnscaled)).Append(',')
                    .Append(F(Time.timeScale)).Append(',').Append(F(timeline)).Append(',').Append(F(audio)).Append(',')
                    .Append(F(body)).Append(',').Append(director.state).AppendLine();
                AppendPhase("LateUpdate", dsp, monotonic, realtime, timeline);
                phasePending = true;
            }
            if (dsp >= nextLog)
            {
                nextLog = dsp + 30;
                Debug.Log("KAGURA clock comparison: timeline=" + F(director.time) + ", dspElapsed=" + F(dsp - startDsp) +
                    ", accumulatedDelta=" + F(sumDelta) + ", audioBlocks=" + blockCount);
            }
            if (dsp - startDsp > configuration.duration + 2)
            {
                finished = true;
                capturing = false;
                SaveResults();
                File.WriteAllText(Path.Combine(output, "complete.txt"),
                    (configuration.controlledHitch ? "Completed 40-second controlled hitch test." : "Completed full Timeline playback.") +
                    " Sender encoding was not enabled.\n");
                Debug.Log("KAGURA clock comparison complete: " + output);
                EditorApplication.isPlaying = false;
            }
        }

        IEnumerator SampleEndOfFrame()
        {
            var wait = new WaitForEndOfFrame();
            while (!finished)
            {
                yield return wait;
                if (!phasePending) continue;
                phasePending = false;
                AppendPhase("EndOfFrame", AudioSettings.dspTime, Monotonic, Time.realtimeSinceStartupAsDouble, director.time);
            }
        }

        void AppendPhase(string phase, double dsp, double monotonic, double realtime, double timeline)
        {
            phases.Append(Time.frameCount).Append(',').Append(phase).Append(',').Append(F(dsp)).Append(',')
                .Append(F(monotonic)).Append(',').Append(F(realtime)).Append(',').Append(F(timeline)).AppendLine();
        }

        void FindPlayables()
        {
            var graph = director.playableGraph;
            if (!graph.IsValid()) return;
            var visited = new HashSet<Playable>();
            for (int i = 0; i < graph.GetRootPlayableCount(); i++) Visit(graph.GetRootPlayable(i), visited);
            for (int i = 0; i < graph.GetOutputCount(); i++) Visit(graph.GetOutput(i).GetSourcePlayable(), visited);
        }

        void Visit(Playable node, HashSet<Playable> visited)
        {
            if (!node.IsValid() || !visited.Add(node)) return;
            if (node.IsPlayableOfType<AudioClipPlayable>())
            {
                var audio = (AudioClipPlayable)node;
                if (audio.GetClip() == song) audioPlayable = audio;
            }
            if (node.IsPlayableOfType<AnimationClipPlayable>())
            {
                var animation = (AnimationClipPlayable)node;
                if (animation.GetAnimationClip().name.StartsWith("UC01", StringComparison.Ordinal)) bodyPlayable = animation;
            }
            for (int i = 0; i < node.GetInputCount(); i++) Visit(node.GetInput(i), visited);
        }

        void SaveResults()
        {
            if (saved) return;
            saved = true;
            // No filesystem I/O or dynamic collections run on the audio thread.
            // Unity removes this filter when Play Mode exits; this snapshot includes only fully published blocks.
            int count = System.Threading.Volatile.Read(ref blockCount);
            var csv = new StringBuilder("dsp,monotonic,sample,window,offset,count,channels\n");
            for (int i = 0; i < count; i++)
            {
                var block = blocks[i];
                csv.Append(F(block.dsp)).Append(',').Append(F(block.monotonic)).Append(',').Append(block.sample).Append(',')
                    .Append(block.window).Append(',').Append(block.offset).Append(',').Append(block.count).Append(',').Append(block.channels).AppendLine();
            }
            File.WriteAllText(Path.Combine(output, "audio-blocks.csv"), csv.ToString());
            for (int i = 0; i < windows.Length; i++)
                WriteFloats(Path.Combine(output, "listener_" + i + ".f32"), windows[i].pcm, windows[i].count);
            File.WriteAllText(Path.Combine(output, "clock.csv"), clocks.ToString());
            File.WriteAllText(Path.Combine(output, "phases.csv"), phases.ToString());
        }

        void OnDisable()
        {
            capturing = false;
            if (initialized) SaveResults();
        }

        static string F(double value) => value.ToString("F9", CultureInfo.InvariantCulture);

        static void WriteFloats(string path, float[] data, int count)
        {
            var bytes = new byte[count * sizeof(float)];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(path, bytes);
        }
    }
}
