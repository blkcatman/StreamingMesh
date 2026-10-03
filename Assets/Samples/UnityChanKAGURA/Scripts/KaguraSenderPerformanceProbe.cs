#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using StreamingMesh.Samples;
using TimeWire.Unity;
using Unity.Profiling;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace StreamingMesh.Samples
{
    // Play-mode-only diagnostics. Preallocated samples avoid per-frame CSV work
    // and audio-thread logging, locks, rational arithmetic or object allocation.
    public sealed class KaguraSenderPerformanceProbe : MonoBehaviour
    {
        struct Frame
        {
            public double elapsed, interval, dsp, director, drift, age, timelineDrift;
            public long generation, missed, allocated, gcTime, main, sourceAudio, recorderAudio;
            public long encode, bake, readback, commit, flush, publish;
            public int collections, state;
        }
        struct Block { public long receipt; public double dsp; public int samples, channels; }
        readonly Frame[] frames = new Frame[24000];
        readonly Block[] blocks = new Block[16000];
        readonly List<ProfilerRecorder> recorders = new List<ProfilerRecorder>();
        KaguraDemoControls controls;
        AudioDspClockSource source;
        ProfilerRecorder allocated, gcTime, main, sourceAudio, recorderAudio, encode, bake, readback, commit, flush, publish;
        long started, previous;
        int frameCount, blockCount, droppedFrameSamples;
        double duration;
        volatile bool capturing;
        bool initialized, saved;
        public string OutputDirectory { get; private set; }
        public bool Finished => saved;
        const ProfilerRecorderOptions Options = ProfilerRecorderOptions.StartImmediately |
            ProfilerRecorderOptions.WrapAroundWhenCapacityReached | ProfilerRecorderOptions.SumAllSamplesInFrame;

        ProfilerRecorder Track(ProfilerCategory category, string marker)
        {
            var recorder = ProfilerRecorder.StartNew(category, marker, 1, Options);
            recorders.Add(recorder);
            return recorder;
        }

        public void Begin(string label, double seconds)
        {
            if (initialized) throw new InvalidOperationException("Probe already started.");
            controls = UnityEngine.Object.FindAnyObjectByType<KaguraDemoControls>();
            source = controls.sender.captureClockSource as AudioDspClockSource;
            if (source == null) throw new InvalidOperationException("AudioDspClockSource is required.");
            OutputDirectory = Path.GetFullPath("Logs/KaguraSenderPerformance_" + label + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(OutputDirectory);
            allocated = Track(ProfilerCategory.Memory, "GC Allocated In Frame");
            gcTime = Track(new ProfilerCategory("GC"), "GC.Collect");
            main = Track(ProfilerCategory.Internal, "Main Thread");
            sourceAudio = Track(ProfilerCategory.Audio, "AudioDspClockSource");
            recorderAudio = Track(ProfilerCategory.Audio, "STMAudioRecorder");
            encode = Track(ProfilerCategory.Scripts, "StreamingMesh.Sender.EncodeFrame");
            bake = Track(ProfilerCategory.Scripts, "StreamingMesh.Sender.BakeMesh");
            readback = Track(ProfilerCategory.Scripts, "StreamingMesh.Sender.Readback");
            commit = Track(ProfilerCategory.Scripts, "StreamingMesh.Sender.CommitFrame");
            flush = Track(ProfilerCategory.Scripts, "StreamingMesh.Sender.FlushChunk");
            publish = Track(ProfilerCategory.Scripts, "StreamingMesh.Sender.PublishChunk");
            AudioSettings.GetDSPBufferSize(out int size, out int count);
            File.WriteAllText(Path.Combine(OutputDirectory, "configuration.json"),
                "{\"label\":\"" + label + "\",\"frequency\":" + Stopwatch.Frequency +
                ",\"sampleRate\":" + AudioSettings.outputSampleRate + ",\"bufferFrames\":" + size + ",\"bufferCount\":" + count +
                ",\"recording\":" + (controls.sender.IsStartRecord ? "true" : "false") +
                ",\"frameRate\":" + controls.sender.frameRate.ToString(CultureInfo.InvariantCulture) + "}");
            duration = seconds;
            started = previous = Stopwatch.GetTimestamp();
            initialized = capturing = true;
        }

        void Update()
        {
            if (!capturing) return;
            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - started) / (double)Stopwatch.Frequency;
            if (elapsed >= duration) { Finish(); return; }
            // The diagnostic sample limit must not shorten the actual recording.
            // Keep its preallocated storage bounded and stop only at the deadline.
            if (frameCount >= frames.Length)
            {
                droppedFrameSamples++;
                previous = now;
                return;
            }
            var snapshot = source.GetSnapshot();
            frames[frameCount++] = new Frame {
                elapsed=elapsed, interval=(now-previous)/(double)Stopwatch.Frequency,
                dsp=AudioSettings.dspTime, director=controls.director.time,
                drift=snapshot.LastDriftSeconds, age=snapshot.ObservationAgeSeconds,
                timelineDrift=controls.timelineClock.LastDriftSeconds, generation=snapshot.Generation,
                missed=controls.sender.MissedCaptureSlots, state=(int)snapshot.State,
                collections=GC.CollectionCount(0), allocated=allocated.LastValue, gcTime=gcTime.LastValue,
                main=main.LastValue, sourceAudio=sourceAudio.LastValue, recorderAudio=recorderAudio.LastValue,
                encode=encode.LastValue, bake=bake.LastValue, readback=readback.LastValue,
                commit=commit.LastValue, flush=flush.LastValue, publish=publish.LastValue
            };
            previous=now;
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!capturing || channels <= 0) return;
            int index=Volatile.Read(ref blockCount);
            if (index >= blocks.Length) return;
            blocks[index]=new Block { receipt=Stopwatch.GetTimestamp(), dsp=AudioSettings.dspTime,
                samples=data.Length/channels, channels=channels };
            Volatile.Write(ref blockCount,index+1);
        }

        public void Finish()
        {
            if (!initialized || saved) return;
            capturing=false;
            saved=true;
            if (controls != null && controls.sender.IsStartRecord) controls.sender.Stop();
            var text=new StringBuilder(frameCount*250);
            text.AppendLine("elapsed,interval,dsp,director,observationDrift,observationAge,timelineDrift,generation,missedSlots,clockState,gcCollections,allocatedBytes,gcNs,mainNs,sourceAudioNs,recorderAudioNs,encodeNs,bakeNs,readbackNs,commitNs,flushNs,publishNs");
            for(int i=0;i<frameCount;i++) {
                Frame f=frames[i];
                text.AppendFormat(CultureInfo.InvariantCulture,"{0:F9},{1:F9},{2:F9},{3:F9},{4:F9},{5:F9},{6:F9},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19},{20},{21}\n",
                    f.elapsed,f.interval,f.dsp,f.director,f.drift,f.age,f.timelineDrift,f.generation,f.missed,f.state,f.collections,f.allocated,f.gcTime,f.main,f.sourceAudio,f.recorderAudio,f.encode,f.bake,f.readback,f.commit,f.flush,f.publish);
            }
            File.WriteAllText(Path.Combine(OutputDirectory,"frames.csv"),text.ToString());
            text.Clear();
            text.AppendLine("receipt,dsp,samples,channels");
            int count=Volatile.Read(ref blockCount);
            for(int i=0;i<count;i++) {
                Block b=blocks[i];
                text.AppendFormat(CultureInfo.InvariantCulture,"{0},{1:F9},{2},{3}\n",b.receipt,b.dsp,b.samples,b.channels);
            }
            File.WriteAllText(Path.Combine(OutputDirectory,"audio-blocks.csv"),text.ToString());
            foreach(var recorder in recorders) recorder.Dispose();
            recorders.Clear();
            File.WriteAllText(Path.Combine(OutputDirectory,"complete.txt"),
                "Main-thread and audio callback measurements saved. This does not measure the hardware output.\n" +
                "Frame samples skipped after diagnostic storage filled: " + droppedFrameSamples + "\n");
            Debug.Log("KAGURA Sender performance capture saved: "+OutputDirectory);
        }
        void OnDisable() { if (initialized) Finish(); }
    }
}
#endif
