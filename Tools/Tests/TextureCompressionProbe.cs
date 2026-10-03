using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Networking;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

[Serializable]
public class TextureProbeEntry
{
    public string name;
    public int format, width, height, bytes;
    public bool linear, alpha;
}

[Serializable]
public class TextureProbeManifest { public TextureProbeEntry[] entries; public TextureProbeEntry body; }

[Serializable]
public class TextureProbeResult
{
    public string name, status, graphicsFormat, reason;
    public int bytes, width, height, mipCount, maxError;
    public bool supported, compressed, readable;
    public double meanError;
}

[Serializable]
public class TextureProbeSummary
{
    public string unity, device, api, mode;
    public bool completed;
    public int failures;
    public List<TextureProbeResult> results = new List<TextureProbeResult>();
}

// An isolated transport/upload experiment, not the production Receiver codec.
public class TextureCompressionProbe : MonoBehaviour
{
    const int Size = 48;
    readonly List<Texture2D> previews = new List<Texture2D>();
    readonly TextureProbeSummary summary = new TextureProbeSummary();
    string status = "Starting";
    int settledFrames;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void STM_ReceiverMemorySnapshot(string stage, out uint capacity, out uint allocated);
    [DllImport("__Internal")] static extern void STM_TextureProbeReport(string json);
#endif

    [Serializable] class MemorySample
    {
        public string stage;
        public long inputBytes, wasmCapacity, wasmAllocated, nativeAllocated, monoHeap, monoUsed;
    }

    public static void Memory(string stage, long inputBytes = 0)
    {
        var sample = new MemorySample { stage = stage, inputBytes = inputBytes,
            nativeAllocated = Profiler.GetTotalAllocatedMemoryLong(), monoHeap = Profiler.GetMonoHeapSizeLong(),
            monoUsed = Profiler.GetMonoUsedSizeLong() };
#if UNITY_WEBGL && !UNITY_EDITOR
        STM_ReceiverMemorySnapshot(stage, out uint capacity, out uint allocated);
        sample.wasmCapacity = capacity; sample.wasmAllocated = allocated;
#endif
        Debug.Log("STM_TEX_MEM " + JsonUtility.ToJson(sample));
    }

    public static Color32[] Fixture(bool alpha)
    {
        var pixels = new Color32[Size * Size];
        for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++)
            pixels[y * Size + x] = new Color32((byte)(x * 5), (byte)(y * 5), (byte)((x + y) * 2), alpha ? (byte)(x * 5) : (byte)255);
        return pixels;
    }

    public static Texture2D Reference(TextureProbeEntry entry)
    {
        var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false, entry.linear);
        texture.SetPixels32(Fixture(entry.alpha)); texture.Apply(false, false);
        texture.filterMode = FilterMode.Point;
        return texture;
    }

    public static bool Supported(TextureProbeEntry entry)
    {
        var format = (TextureFormat)entry.format;
        var graphics = GraphicsFormatUtility.GetGraphicsFormat(format, !entry.linear);
        return SystemInfo.SupportsTextureFormat(format) && SystemInfo.IsFormatSupported(graphics, GraphicsFormatUsage.Sample);
    }

    public static Texture2D Upload(TextureProbeEntry entry, byte[] data)
    {
        if (data.Length != entry.bytes) throw new InvalidOperationException("Raw payload length mismatch: " + entry.name);
        Memory(entry.name + ":before-create", data.Length);
        var texture = new Texture2D(entry.width, entry.height, (TextureFormat)entry.format, false, entry.linear);
        try
        {
            Memory(entry.name + ":before-raw-upload", data.Length);
            texture.LoadRawTextureData(data);
            Memory(entry.name + ":after-raw-upload", data.Length);
            texture.Apply(false, true);
            Memory(entry.name + ":after-apply", data.Length);
            texture.filterMode = entry.width == Size ? FilterMode.Point : FilterMode.Bilinear;
            return texture;
        }
        catch { UnityEngine.Object.Destroy(texture); throw; }
    }

    public static Texture2D UploadNative(TextureProbeEntry entry, NativeArray<byte>.ReadOnly data)
    {
        if (data.Length != entry.bytes) throw new InvalidOperationException("Native payload length mismatch");
        Memory(entry.name + ":before-create", data.Length);
        var texture = new Texture2D(entry.width, entry.height, (TextureFormat)entry.format, false, entry.linear);
        try
        {
            Memory(entry.name + ":before-native-copy", data.Length);
            // Borrow the request's buffer while the DownloadHandler is alive. Do not dispose this view.
            // The destination is the Texture's existing CPU buffer; no whole-image managed byte[] is created.
            data.CopyTo(texture.GetRawTextureData<byte>());
            Memory(entry.name + ":after-native-copy", data.Length);
            texture.Apply(false, true);
            Memory(entry.name + ":after-apply", data.Length);
            texture.filterMode = FilterMode.Bilinear;
            return texture;
        }
        catch { UnityEngine.Object.Destroy(texture); throw; }
    }

    public static RenderTexture Render(Texture texture)
    {
        var output = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        output.Create();
        var material = new Material(Resources.Load<Shader>("TextureCompressionProbe"));
        Graphics.Blit(texture, output, material);
        if (Application.isPlaying) UnityEngine.Object.Destroy(material);
        else UnityEngine.Object.DestroyImmediate(material);
        return output;
    }

    public static TextureProbeResult Compare(TextureProbeEntry entry, Texture2D texture, Color32[] reference, Color32[] actual)
    {
        long sum = 0; int max = 0;
        if (reference.Length != actual.Length || actual.Length != Size * Size) throw new InvalidOperationException("Readback size mismatch");
        for (int i = 0; i < actual.Length; i++)
        {
            var a = actual[i]; var b = reference[i];
            foreach (int error in new[] { Math.Abs(a.r - b.r), Math.Abs(a.g - b.g), Math.Abs(a.b - b.b), Math.Abs(a.a - b.a) })
            { sum += error; max = Math.Max(max, error); }
        }
        double mean = (double)sum / (actual.Length * 4);
        bool compressed = GraphicsFormatUtility.IsCompressedFormat(texture.graphicsFormat);
        // Lossy compression is expected. Also reject CPU fallback, retained CPU pixels and unexpected mips.
        var expectedFormat = GraphicsFormatUtility.GetGraphicsFormat((TextureFormat)entry.format, !entry.linear);
        bool passed = mean <= 8 && max <= 64 && compressed && texture.graphicsFormat == expectedFormat &&
            texture.format == (TextureFormat)entry.format && texture.width == entry.width && texture.height == entry.height &&
            !texture.isReadable && texture.mipmapCount == 1;
        return new TextureProbeResult { name = entry.name, status = passed ? "PASS" : "FAIL", supported = true,
            bytes = entry.bytes, width = texture.width, height = texture.height, graphicsFormat = texture.graphicsFormat.ToString(), compressed = compressed,
            readable = texture.isReadable, mipCount = texture.mipmapCount, meanError = mean, maxError = max };
    }

    public static TextureProbeResult Unsupported(TextureProbeEntry entry) => new TextureProbeResult {
        name = entry.name, status = "UNSUPPORTED", bytes = entry.bytes,
        reason = "Device cannot sample this compressed format; no decompression fallback counted as direct upload." };

    IEnumerator Start()
    {
        summary.unity = Application.unityVersion; summary.device = SystemInfo.graphicsDeviceName;
        summary.api = SystemInfo.graphicsDeviceType.ToString(); summary.mode = "matrix";
        if (Application.absoluteURL.Contains("mode=bc7")) summary.mode = "bc7";
        if (Application.absoluteURL.Contains("mode=bc7-native")) summary.mode = "bc7-native";
        if (Application.absoluteURL.Contains("mode=png")) summary.mode = "png";
        var manifest = JsonUtility.FromJson<TextureProbeManifest>(Resources.Load<TextAsset>("TextureProbeManifest").text);
        Memory("start");
        if (summary.mode == "matrix")
        {
            foreach (var entry in manifest.entries)
            {
                status = "Testing " + entry.name;
                if (!Supported(entry)) { Record(Unsupported(entry)); continue; }
                var payload = Resources.Load<TextAsset>(entry.name);
                Texture2D texture = null;
                try { texture = Upload(entry, payload.bytes); }
                catch (Exception error) { Failure(entry.name, error.Message); }
                if (texture == null) continue;
                var reference = Reference(entry);
                yield return CompareGpu(entry, texture, reference, null);
                Destroy(reference); previews.Add(texture);
                yield return null;
            }
        }
        else
        {
            var entry = manifest.body;
            if (summary.mode != "png" && !Supported(entry)) Record(Unsupported(entry));
            else
            {
                status = "Loading 8192 x 8192 Body_Base (" + summary.mode + ")";
                Memory("body:before-download");
                var url = new Uri(new Uri(Application.absoluteURL), "payload/Body_Base." + (summary.mode == "png" ? "png" : "bc7"));
                using (var request = UnityWebRequest.Get(url))
                {
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success) Failure(entry.name, request.error);
                    else
                    {
                        Memory("body:after-download");
                        byte[] data = null;
                        if (summary.mode != "bc7-native")
                        {
                            data = request.downloadHandler.data;
                            Memory("body:after-managed-copy", data.Length);
                            if (summary.mode == "png") { entry.name = "Body_Base_PNG"; entry.bytes = data.Length; }
                        }
                        Texture2D texture = null;
                        try
                        {
                            if (summary.mode == "bc7-native") texture = UploadNative(entry, request.downloadHandler.nativeData);
                            else if (summary.mode == "bc7") texture = Upload(entry, data);
                            else
                            {
                                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                                Memory("body:before-load-image", data.Length);
                                if (!texture.LoadImage(data, true)) throw new InvalidOperationException("PNG decode failed");
                                Memory("body:after-load-image", data.Length);
                            }
                        }
                        catch (Exception error) { Failure(entry.name, error.Message); }
                        data = null;
                        if (texture != null)
                        {
                            texture.filterMode = FilterMode.Bilinear;
                            var rawReference = Resources.Load<TextAsset>("BodyReference").bytes;
                            var pixels = new Color32[Size * Size];
                            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(rawReference[i*4], rawReference[i*4+1], rawReference[i*4+2], rawReference[i*4+3]);
                            yield return CompareGpu(entry, texture, null, pixels);
                            previews.Add(texture);
                        }
                    }
                }
                // Unwind the upload's native/managed stack before the conservative Web GC.
                yield return null;
                GC.Collect();
                yield return null;
                Memory("body:after-release");
            }
        }
        summary.completed = true; status = "Complete";
        Debug.Log("STM_TEX_SUMMARY " + JsonUtility.ToJson(summary));
#if UNITY_WEBGL && !UNITY_EDITOR
        STM_TextureProbeReport(JsonUtility.ToJson(summary));
#endif
    }

    IEnumerator CompareGpu(TextureProbeEntry entry, Texture2D texture, Texture2D reference, Color32[] referencePixels)
    {
        var actualRt = Render(texture);
        RenderTexture referenceRt = reference == null ? null : Render(reference);
        var actual = AsyncGPUReadback.Request(actualRt, 0, TextureFormat.RGBA32);
        var expected = referenceRt == null ? default : AsyncGPUReadback.Request(referenceRt, 0, TextureFormat.RGBA32);
        float deadline = Time.realtimeSinceStartup + 30;
        while ((!actual.done || (referenceRt != null && !expected.done)) && Time.realtimeSinceStartup < deadline) yield return null;
        if (!actual.done || actual.hasError || (referenceRt != null && (!expected.done || expected.hasError))) Failure(entry.name, "GPU readback failed or timed out");
        else
        {
            var result = Compare(entry, texture, referencePixels ?? expected.GetData<Color32>().ToArray(), actual.GetData<Color32>().ToArray());
            if (summary.mode == "png")
            {
                // PNG is a comparison control and is intentionally uncompressed on the GPU.
                result.status = result.meanError <= 1 && result.maxError <= 4 && !result.compressed && !texture.isReadable &&
                    texture.width == entry.width && texture.height == entry.height && texture.mipmapCount == 1 ? "CONTROL_PASS" : "FAIL";
            }
            Record(result);
        }
        actualRt.Release(); Destroy(actualRt);
        if (referenceRt != null) { referenceRt.Release(); Destroy(referenceRt); }
    }

    void Record(TextureProbeResult result)
    {
        summary.results.Add(result); if (result.status == "FAIL") summary.failures++;
        Debug.Log("STM_TEX_RESULT " + JsonUtility.ToJson(result));
    }
    void Failure(string name, string reason) => Record(new TextureProbeResult { name = name, status = "FAIL", reason = reason });

    void Update()
    {
        if (!summary.completed || summary.mode == "matrix" || ++settledFrames != 5) return;
        // Measure again after Start's iterator and its temporary upload references have unwound.
        GC.Collect();
        Memory("body:settled-after-gc");
    }

    void OnGUI()
    {
        GUI.skin.label.fontSize = 17;
        GUI.Label(new Rect(20, 15, 900, 30), "Compressed texture upload / GPU readback: " + status);
        GUI.Label(new Rect(20, 45, 900, 30), summary.api + " / " + summary.device + " / " + summary.mode);
        for (int i = 0; i < summary.results.Count; i++)
        {
            var result = summary.results[i];
            GUI.Label(new Rect(20, 82 + i * 27, 910, 27), result.name + ": " + result.status + (result.supported ? "  error=" + result.meanError.ToString("F2") + "  " + result.graphicsFormat : ""));
        }
        for (int i = 0; i < previews.Count; i++) GUI.DrawTexture(new Rect(20 + i * 120, 490, 110, 110), previews[i], ScaleMode.ScaleToFit, true);
    }
}
