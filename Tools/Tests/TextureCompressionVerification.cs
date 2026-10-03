using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class TextureCompressionVerification
{
    static readonly TextureFormat[] Formats = { TextureFormat.DXT1, TextureFormat.DXT5, TextureFormat.BC7,
        TextureFormat.ETC2_RGB, TextureFormat.ETC2_RGBA8, TextureFormat.ASTC_4x4, TextureFormat.ASTC_6x6 };

    public static void Run()
    {
        try
        {
            // Exercise actual sRGB GPU sampling as well as linear texture sampling.
            PlayerSettings.colorSpace = ColorSpace.Linear;
            string output = Path.GetFullPath(Argument("-probeOutput"));
            string resources = Path.Combine(Application.dataPath, "Resources");
            string payload = Path.Combine(output, "payload");
            Directory.CreateDirectory(resources); Directory.CreateDirectory(payload);
            var entries = new List<TextureProbeEntry>();
            foreach (var format in Formats) foreach (bool linear in new[] { false, true })
            {
                bool alpha = format != TextureFormat.DXT1 && format != TextureFormat.ETC2_RGB;
                var entry = new TextureProbeEntry { name = format + (linear ? "_Linear" : "_sRGB"),
                    format = (int)format, width = 48, height = 48, linear = linear, alpha = alpha };
                var source = TextureCompressionProbe.Reference(entry);
                byte[] data;
                try { data = Encode(source, entry); }
                finally { UnityEngine.Object.DestroyImmediate(source); }
                File.WriteAllBytes(Path.Combine(resources, entry.name + ".bytes"), data);
                entries.Add(entry);
            }

            var body = new TextureProbeEntry { name = "Body_Base_BC7", format = (int)TextureFormat.BC7, width = 8192, height = 8192 };
            byte[] png = File.ReadAllBytes(Argument("-probeBody"));
            File.WriteAllBytes(Path.Combine(payload, "Body_Base.png"), png);
            var original = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!original.LoadImage(png, false) || original.width != body.width || original.height != body.height)
                throw new InvalidOperationException("Expected 8192 x 8192 Body_Base");
            original.filterMode = FilterMode.Bilinear;
            var bodyReference = Readback(original);
            var referenceBytes = new byte[bodyReference.Length * 4];
            for (int i = 0; i < bodyReference.Length; i++)
            {
                referenceBytes[i*4] = bodyReference[i].r; referenceBytes[i*4+1] = bodyReference[i].g;
                referenceBytes[i*4+2] = bodyReference[i].b; referenceBytes[i*4+3] = bodyReference[i].a;
            }
            File.WriteAllBytes(Path.Combine(resources, "BodyReference.bytes"), referenceBytes);
            File.WriteAllBytes(Path.Combine(payload, "Body_Base.bc7"), Encode(original, body));
            UnityEngine.Object.DestroyImmediate(original); png = null; GC.Collect();

            var manifest = new TextureProbeManifest { entries = entries.ToArray(), body = body };
            File.WriteAllText(Path.Combine(resources, "TextureProbeManifest.json"), JsonUtility.ToJson(manifest, true));
            AssetDatabase.Refresh();
            VerifyNative(manifest, resources, payload, output);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("TextureCompressionProbe").AddComponent<TextureCompressionProbe>();
            var camera = new GameObject("Camera").AddComponent<Camera>(); camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.035f, 0.04f);
            EditorSceneManager.SaveScene(scene, "Assets/TextureCompressionProbe.unity");
            PlayerSettings.productName = "Texture compression probe";
            PlayerSettings.companyName = "StreamingMesh";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.WebGPU, GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.WebGL.threadsSupport = true;
            PlayerSettings.WebGL.wasm2023 = true;
            PlayerSettings.WebGL.initialMemorySize = 32;
            PlayerSettings.WebGL.maximumMemorySize = 4096;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.decompressionFallback = true;
            AssetDatabase.SaveAssets();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/TextureCompressionProbe.unity" },
                locationPathName = output, target = BuildTarget.WebGL, options = BuildOptions.None });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Web probe build failed: " + report.summary.result);
            Debug.Log("PASS texture compression export/native/build: " + output);
            EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    static byte[] Encode(Texture2D source, TextureProbeEntry entry)
    {
        var format = (TextureFormat)entry.format;
        EditorUtility.CompressTexture(source, format, TextureCompressionQuality.Normal);
        var data = source.GetRawTextureData();
        int block = format == TextureFormat.ASTC_6x6 ? 6 : 4;
        int blockBytes = format == TextureFormat.DXT1 || format == TextureFormat.ETC2_RGB ? 8 : 16;
        int expected = ((entry.width + block - 1) / block) * ((entry.height + block - 1) / block) * blockBytes;
        if (source.format != format || data.Length != expected || source.mipmapCount != 1)
            throw new InvalidOperationException("Wrong compressed output: " + entry.name);
        entry.bytes = data.Length;
        Debug.Log("STM_TEX_EXPORT " + JsonUtility.ToJson(entry));
        return data;
    }

    static void VerifyNative(TextureProbeManifest manifest, string resources, string payload, string output)
    {
        var summary = new TextureProbeSummary { unity = Application.unityVersion, api = SystemInfo.graphicsDeviceType.ToString(),
            device = SystemInfo.graphicsDeviceName, mode = "native", completed = true };
        foreach (var entry in manifest.entries)
        {
            TextureProbeResult result;
            if (!TextureCompressionProbe.Supported(entry)) result = TextureCompressionProbe.Unsupported(entry);
            else
            {
                // Restore an independently created Texture from the bytes that were written to disk.
                var texture = TextureCompressionProbe.Upload(entry, File.ReadAllBytes(Path.Combine(resources, entry.name + ".bytes")));
                var reference = TextureCompressionProbe.Reference(entry);
                result = TextureCompressionProbe.Compare(entry, texture, Readback(reference), Readback(texture));
                UnityEngine.Object.DestroyImmediate(reference); UnityEngine.Object.DestroyImmediate(texture);
            }
            summary.results.Add(result);
            Debug.Log("STM_TEX_RESULT " + JsonUtility.ToJson(result));
        }
        var body = manifest.body;
        if (TextureCompressionProbe.Supported(body))
        {
            var texture = TextureCompressionProbe.Upload(body, File.ReadAllBytes(Path.Combine(payload, "Body_Base.bc7")));
            var png = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            png.LoadImage(File.ReadAllBytes(Path.Combine(payload, "Body_Base.png")), true); png.filterMode = FilterMode.Bilinear;
            var result = TextureCompressionProbe.Compare(body, texture, Readback(png), Readback(texture));
            summary.results.Add(result); Debug.Log("STM_TEX_RESULT " + JsonUtility.ToJson(result));
            UnityEngine.Object.DestroyImmediate(png); UnityEngine.Object.DestroyImmediate(texture);
        }
        foreach (var result in summary.results) if (result.status == "FAIL") summary.failures++;
        File.WriteAllText(Path.Combine(output, "native-results.json"), JsonUtility.ToJson(summary, true));
        Debug.Log("STM_TEX_SUMMARY " + JsonUtility.ToJson(summary));
        if (summary.failures != 0) throw new InvalidOperationException("Native GPU comparison failed");
    }

    static Color32[] Readback(Texture texture)
    {
        var target = TextureCompressionProbe.Render(texture);
        var previous = RenderTexture.active;
        var image = new Texture2D(48, 48, TextureFormat.RGBA32, false, true);
        try
        {
            RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, 48, 48), 0, 0, false); image.Apply(false, false);
            return image.GetPixels32();
        }
        finally
        {
            RenderTexture.active = previous; target.Release(); UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(image);
        }
    }

    static string Argument(string key)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (int i = 0; i < arguments.Length - 1; i++) if (arguments[i] == key) return arguments[i + 1];
        throw new ArgumentException("Missing argument " + key);
    }
}
