using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using StreamingMesh;
using StreamingMesh.Core.Serialization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class IndexedResourceVerification
{
  static string Argument(string key) { var args = Environment.GetCommandLineArgs(); return args[Array.IndexOf(args, key) + 1]; }
  static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

  public static void VerifyEncodersCommandLine()
  {
    try
    {
      PlayerSettings.colorSpace = ColorSpace.Linear;
      VerifyEncoders();
      VerifyReconnect();
      Debug.Log("PASS production encoders and mips"); EditorApplication.Exit(0);
    }
    catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
  }

  public static void Run()
  {
    try
    {
      PlayerSettings.colorSpace = ColorSpace.Linear;
      string output = Path.GetFullPath(Argument("-indexedOutput"));
      VerifyReconnect();
      if (!Environment.GetCommandLineArgs().Contains("-indexedBuildOnly"))
      {
      VerifyEncoders();
      string channel = Path.GetFullPath(Argument("-indexedChannel"));
      string fixture = Path.GetFullPath(Argument("-indexedFixture"));
      Directory.CreateDirectory(output); Directory.CreateDirectory(channel);
      var oldInfo = JsonUtility.FromJson<ChannelInfo>(File.ReadAllText(Path.Combine(fixture, "stream.json")));
      var source = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Samples/UnityChanKAGURA/Prefabs/UnityChanKAGURA.prefab"));
      var serializerObject = new GameObject("verification-sender");
      var serializer = serializerObject.AddComponent<STMHttpSerializer>();
      serializer.BeginResourceSnapshot();
      var meshInfos = new List<MeshInfo>(); var materials = new Dictionary<string, MaterialInfo>(); var textures = new Dictionary<string, Texture>();
      foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
      {
        var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh == null) continue;
        meshInfos.Add(serializer.CreateMeshInfo(renderer, mesh));
        foreach (var material in renderer.sharedMaterials)
        {
          if (material == null) continue;
          var info = serializer.CreateMaterialInfo(material); materials[info.id] = info;
          foreach (var pair in serializer.GetTexturesFromMaterial(material)) textures[pair.Key] = pair.Value;
        }
      }
      Check(meshInfos.Count == oldInfo.meshes.Count && meshInfos.Sum(mesh => mesh.vertexCount) == 23489, "Recorded mesh layout changed");
      // Offline fixture audit only: production Receiver does not accept v4.
      var recorded = StreamingMesh.Lib.ExternalTools.DecompressExact(File.ReadAllBytes(Path.Combine(fixture, "stream.bin")), 128 * 1024 * 1024);
      int recordedOffset = oldInfo.textureSizes.Sum() + oldInfo.materialSizes.Sum();
      for (int i = 0; i < meshInfos.Count; i++)
      {
        var previous = InfoConverter.Deserialize<MeshInfo>(recorded, recordedOffset, oldInfo.meshSizes[i]);
        Check(previous.name == meshInfos[i].name && previous.vertexCount == meshInfos[i].vertexCount && previous.indices.SequenceEqual(meshInfos[i].indices), "Recorded mesh order/topology changed");
        recordedOffset += oldInfo.meshSizes[i];
      }
      recorded = null;
      Check(materials.Count == oldInfo.materials.Count && oldInfo.materials.All(materials.ContainsKey), "Material IDs changed");
      Check(textures.Count == oldInfo.textures.Count && oldInfo.textures.All(textures.ContainsKey), "Texture IDs changed");
      var channelInfo = serializer.CreateChannelInfo(oldInfo.container_size, oldInfo.package_size, oldInfo.frame_interval, oldInfo.combined_frames,
        oldInfo.meshes, oldInfo.materials, oldInfo.textures, new List<int>(), new List<int>(), new List<int>(), oldInfo.textureNames);
      InitialResourceExporter.ExportVariants(channelInfo, oldInfo.materials.Select(id => materials[id]).ToList(), meshInfos,
        oldInfo.textures.Select(id => textures[id]).ToList(), new[] { GpuTextureFormat.BC7, GpuTextureFormat.ASTC_4x4,
          GpuTextureFormat.ASTC_6x6, GpuTextureFormat.DXT5, GpuTextureFormat.ETC2_RGBA8 },
        (part, bytes) => File.WriteAllBytes(Path.Combine(channel, part.file), bytes));
      foreach (var file in Directory.GetFiles(fixture))
        if (!new[] { "stream.bin", "stream.json" }.Contains(Path.GetFileName(file))) File.Copy(file, Path.Combine(channel, Path.GetFileName(file)), true);
      File.WriteAllText(Path.Combine(channel, "stream.json"), JsonUtility.ToJson(channelInfo));
      long bytesTotal = channelInfo.textureSizes.Sum(size => (long)size);
      long compressedTotal = channelInfo.initial_data.Sum(part => (long)part.compressedSize);
      Debug.Log($"STM_INDEX_EXPORT parts={channelInfo.initial_data.Count} textures={textures.Count} rawTextureBytes={bytesTotal} compressedTotalBytes={compressedTotal}");
      foreach (var variant in channelInfo.texture_variants)
      {
        Check(variant.initial_data.SelectMany(part => part.records).All(record => record.resourceOffset == 0), "A resource was split between files");
        var copy = JsonUtility.FromJson<ChannelInfo>(JsonUtility.ToJson(channelInfo));
        string selected = TextureVariants.Select(copy, supported: payload => payload.format == variant.format);
        Check(selected == variant.format && copy.texturePayloads.All(payload => payload.format == selected) && copy.texture_variants == null,
          "Platform selection did not isolate the selected variant");
        Debug.Log($"STM_INDEX_VARIANT format={selected} parts={copy.initial_data.Count} bytes={copy.textureSizes.Sum(size => (long)size)} compressed={copy.initial_data.Sum(part => (long)part.compressedSize)} atomic=true selection=true");
      }
      bool unsupportedRejected = false;
      try { TextureVariants.Select(JsonUtility.FromJson<ChannelInfo>(JsonUtility.ToJson(channelInfo)), supported: payload => false); }
      catch (NotSupportedException) { unsupportedRejected = true; }
      Check(unsupportedRejected, "Unsupported variants did not stop loading");
      VerifyNative(channelInfo, channel);
      UnityEngine.Object.DestroyImmediate(serializerObject); UnityEngine.Object.DestroyImmediate(source);
      }
      var scene = EditorSceneManager.OpenScene("Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity");
      var receiver = UnityEngine.Object.FindFirstObjectByType<Receiver>(FindObjectsInactive.Include);
      var settings = new SerializedObject(receiver);
      settings.FindProperty("m_LogMemoryDiagnostics").boolValue = true;
      settings.ApplyModifiedPropertiesWithoutUndo(); EditorSceneManager.SaveScene(scene);
      UnityEngine.Object.DestroyImmediate(receiver);
      // Reopen the saved scene; scene cleanup above releases native test objects.
      EditorSceneManager.OpenScene(scene.path);
      var pipeline = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>("Assets/Settings/StreamingMeshURP.asset");
      UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = pipeline; QualitySettings.renderPipeline = pipeline;
      // Isolated 17.5 test project can receive settings copied from source 17.6.
      // Create defaults for the installed test package rather than downgrading
      // or changing the user's source settings asset.
      var globalType = typeof(UnityEngine.Rendering.Universal.UniversalRenderPipeline).Assembly.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineGlobalSettings");
      var global = UnityEngine.Rendering.RenderPipelineGlobalSettingsUtils.Create(globalType, "Assets/IndexedVerificationGlobalSettings.asset");
      UnityEditor.Rendering.EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset<UnityEngine.Rendering.Universal.UniversalRenderPipeline>(global);
      StreamingMesh.Editor.StreamingMeshWebBuild.ConfigureWebGpuReceiver();
      PlayerSettings.WebGL.initialMemorySize = 32;
      var report = BuildPipeline.BuildPlayer(new UnityEditor.BuildPlayerOptions {
        scenes = new[] { "Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity" }, locationPathName = output,
        target = BuildTarget.WebGL, options = BuildOptions.None });
      Check(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded, "Web build failed");
      Debug.Log("PASS indexed resources export/native/build"); EditorApplication.Exit(0);
    }
    catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
  }

  public static void VerifyReconnectCommandLine()
  {
    try { VerifyReconnect(); Debug.Log("PASS reconnect resources and stale worker rejection"); EditorApplication.Exit(0); }
    catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
  }

  static void VerifyNative(ChannelInfo info, string channel)
  {
    var textures = new Dictionary<string, Texture2D>();
    try
    {
      using (var loader = new InitialResourceLoader(info, material => Shader.Find(material.shaderName), (id, texture) => textures.Add(id, texture)))
      {
        var scratch = new byte[64 * 1024];
        foreach (var part in info.initial_data)
          using (var input = File.OpenRead(Path.Combine(channel, part.file))) InitialDataParts.Read(part, input, scratch, loader.Consume);
        Check(textures.Count == 20 && textures.Values.All(texture => texture.format == TextureFormat.BC7 && !texture.isReadable), "Native compressed textures changed format or retained CPU memory");
        Check(loader.Materials.All(material => material != null) && loader.Meshes.All(mesh => mesh != null), "Native metadata incomplete");
        typeof(ReceiverMaterialVerification).GetMethod("VerifyRenderedAppearance", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { textures });
      }
      Debug.Log("STM_INDEX_NATIVE PASS textures=20 format=BC7 readable=false materialAppearance=true");
    }
    finally { foreach (var texture in textures.Values) UnityEngine.Object.DestroyImmediate(texture); }
  }

  static void VerifyReconnect()
  {
    const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
    var gameObject = new GameObject("reconnect-verification"); gameObject.SetActive(false);
    var receiver = gameObject.AddComponent<Receiver>();
    var renderer = new StreamingMesh.Core.Rendering.StreamingMeshRenderer {
      DecodeBackend = StreamingMesh.Core.Rendering.ReceiverDecodeBackend.GPU, CombinedFrames = 2, FrameInterval = 1 };
    var methods = typeof(ReceiverTangentVerification);
    var mesh = (Mesh)methods.GetMethod("Fixture", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
    var vertices = mesh.vertices;
    var keyframe = (byte[])methods.GetMethod("Keyframe", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { new[] { vertices }, (uint)0 });
    var keyframe1 = (byte[])methods.GetMethod("Keyframe", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { new[] { vertices }, (uint)1 });
    var chunk = (byte[])methods.GetMethod("Chunk", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { new[] { keyframe, keyframe1 } });
    var texture = new Texture2D(4, 4); var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
    renderer.AddTexture("texture", texture); renderer.AddMaterial("material", material); renderer.AddMesh("mesh", mesh);
    renderer.CreateVertexBuffer(); renderer.CreateVertexContainer(128, 4);
    Check(renderer.IsGpuResident, "Reconnect fixture did not use GPU");
    try
    {
      renderer.AddVertexData("0", chunk, 0); renderer.UpdateWithTime(0);
      var rendererType = renderer.GetType();
      var pipeline = rendererType.GetField("m_GpuPipeline", flags).GetValue(renderer);
      var pool = rendererType.GetField("m_ChunkPool", flags).GetValue(renderer);
      var layout = rendererType.GetField("m_VertexLayout", flags).GetValue(renderer);
      renderer.ResetPlayback();
      Check(renderer.EncodedFrameCount == 0 && renderer.BufferedFrameCount == 0 && double.IsNaN(renderer.PresentedTime), "Old playback state survived reconnect");
      Check(ReferenceEquals(pipeline, rendererType.GetField("m_GpuPipeline", flags).GetValue(renderer)) &&
        ReferenceEquals(pool, rendererType.GetField("m_ChunkPool", flags).GetValue(renderer)) &&
        ReferenceEquals(layout, rendererType.GetField("m_VertexLayout", flags).GetValue(renderer)), "Reconnect reallocated buffers");
      // Hold the worker gate to deterministically reconnect while an old
      // import is in flight. Its continuation only returns the stale lease.
      var gate = rendererType.GetField("m_ImportGate", flags).GetValue(renderer);
      System.Threading.Tasks.Task<bool> stale = null;
      System.Threading.Monitor.Enter(gate);
      var context = System.Threading.SynchronizationContext.Current;
      try
      {
        System.Threading.SynchronizationContext.SetSynchronizationContext(null);
        stale = renderer.AddVertexDataAsync("1", chunk, 0);
        renderer.ResetPlayback();
      }
      finally
      {
        System.Threading.SynchronizationContext.SetSynchronizationContext(context);
        System.Threading.Monitor.Exit(gate);
      }
      Check(!stale.GetAwaiter().GetResult() && renderer.EncodedFrameCount == 0, "Stale worker committed into the new playback");
      renderer.AddVertexData("0", chunk, 0);
      Check(renderer.EncodedFrameCount == 2, "Chunk zero could not be imported after reconnect");
      var info = new ChannelInfo { protocol_version = 5, textures = new List<string> { "same-id" },
        initial_data = new List<InitialDataPart> { new InitialDataPart { file = "random.bin", sha256 = new string('a',64), size = 64 } } };
      var fingerprintMethod = typeof(Receiver).GetMethod("ResourceFingerprint", flags);
      string original = (string)fingerprintMethod.Invoke(receiver, new object[] { info });
      Check(original == (string)fingerprintMethod.Invoke(receiver, new object[] { info }), "Stable manifest changed fingerprint");
      info.initial_data[0].sha256 = new string('b',64);
      Check(original != (string)fingerprintMethod.Invoke(receiver, new object[] { info }), "Same path ID hid changed texture bytes");
      info.initial_data[0].sha256 = new string('a',64);
      typeof(Receiver).GetField("m_TangentMode", flags).SetValue(receiver, StreamingMesh.Core.Rendering.ReceiverTangentMode.Recalculate);
      Check(original != (string)fingerprintMethod.Invoke(receiver, new object[] { info }), "Changed vertex layout reused cache");
      var root = new GameObject("cached-model"); root.transform.SetParent(gameObject.transform);
      typeof(Receiver).GetField("m_MeshRenderer", flags).SetValue(receiver, renderer);
      typeof(Receiver).GetField("m_StreamRoot", flags).SetValue(receiver, root);
      typeof(Receiver).GetField("m_ActiveResourceFingerprint", flags).SetValue(receiver, original);
      typeof(Receiver).GetMethod("ResetPlaybackData", flags).Invoke(receiver, new object[] { true });
      Check(ReferenceEquals(renderer, typeof(Receiver).GetField("m_CachedMeshRenderer", flags).GetValue(receiver)) && !root.activeSelf && renderer.HasValidResources, "Receiver destroyed cached model");
      Check(renderer.TextureDictionary["texture"] == texture && renderer.MaterialDictionary["material"] == material, "Cached Unity resources changed identity");
      Debug.Log("STM_INDEX_RECONNECT PASS modelIdentity=true buffersReused=true chunkZero=true staleWorkerRejected=true manifestAndSettingsInvalidation=true");
    }
    finally
    {
      renderer.Dispose(); UnityEngine.Object.DestroyImmediate(gameObject);
      UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(texture);
    }
  }

  static void VerifyEncoders()
  {
    foreach (GpuTextureFormat format in Enum.GetValues(typeof(GpuTextureFormat))) foreach (bool linear in new[] { false, true })
    {
      var entry = new TextureProbeEntry { name = format + "_" + linear, format = (int)new TexturePayloadInfo { format = format.ToString() }.UnityFormat(),
        width = 48, height = 48, linear = linear, alpha = format != GpuTextureFormat.DXT1 && format != GpuTextureFormat.ETC2_RGB };
      var source = TextureCompressionProbe.Reference(entry); Texture2D encoded = null, restored = null;
      try
      {
        encoded = TextureConverter.ExportGpu(source, format, out var payload);
        Check(payload.linear == linear && payload.mipCount == 1, "Export changed texture settings");
        entry.bytes = payload.ByteCount();
        if (TextureCompressionProbe.Supported(entry))
        {
          restored = payload.Create(); encoded.GetRawTextureData<byte>().CopyTo(restored.GetRawTextureData<byte>()); restored.Apply(false, true);
          var result = TextureCompressionProbe.Compare(entry, restored, Readback(source), Readback(restored));
          Debug.Log("STM_INDEX_MATRIX " + JsonUtility.ToJson(result)); Check(result.status == "PASS", "Production encoder GPU mismatch");
        }
        else Debug.Log("STM_INDEX_MATRIX exported=" + format + " linear=" + linear + " GPU=unsupported");
      }
      finally
      {
        UnityEngine.Object.DestroyImmediate(source);
        if (encoded != null) UnityEngine.Object.DestroyImmediate(encoded);
        if (restored != null) UnityEngine.Object.DestroyImmediate(restored);
      }
    }
    var mipSource = new Texture2D(32, 16, TextureFormat.RGBA32, true, true);
    Texture2D mipEncoded = null, mipRestored = null;
    try
    {
      mipSource.SetPixels32(Enumerable.Repeat(new Color32(80, 140, 210, 100), 512).ToArray()); mipSource.Apply(true, false);
      mipEncoded = TextureConverter.ExportGpu(mipSource, GpuTextureFormat.BC7, out var payload);
      Check(payload.mipCount == 6 && payload.ByteCount() == 720, "Compressed mip layout changed");
      mipRestored = payload.Create(); mipEncoded.GetRawTextureData<byte>().CopyTo(mipRestored.GetRawTextureData<byte>()); mipRestored.Apply(false, true);
      Check(mipRestored.mipmapCount == 6 && !mipRestored.isReadable, "Mip upload retained CPU data or changed mip count");
      Debug.Log("STM_INDEX_MIPS PASS levels=6 bytes=720 readable=false");
    }
    finally
    {
      UnityEngine.Object.DestroyImmediate(mipSource);
      if (mipEncoded != null) UnityEngine.Object.DestroyImmediate(mipEncoded);
      if (mipRestored != null) UnityEngine.Object.DestroyImmediate(mipRestored);
    }
  }

  static Color32[] Readback(Texture texture)
  {
    var target = TextureCompressionProbe.Render(texture); var previous = RenderTexture.active;
    var image = new Texture2D(48, 48, TextureFormat.RGBA32, false, true);
    try { RenderTexture.active = target; image.ReadPixels(new Rect(0,0,48,48),0,0,false); image.Apply(false,false); return image.GetPixels32(); }
    finally { RenderTexture.active = previous; target.Release(); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(image); }
  }
}
