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
      InitialResourceExporter.Export(channelInfo, oldInfo.materials.Select(id => materials[id]).ToList(), meshInfos,
        oldInfo.textures.Select(id => textures[id]).ToList(), GpuTextureFormat.BC7,
        (part, bytes) => File.WriteAllBytes(Path.Combine(channel, part.file), bytes));
      foreach (var file in Directory.GetFiles(fixture))
        if (!new[] { "stream.bin", "stream.json" }.Contains(Path.GetFileName(file))) File.Copy(file, Path.Combine(channel, Path.GetFileName(file)), true);
      File.WriteAllText(Path.Combine(channel, "stream.json"), JsonUtility.ToJson(channelInfo));
      long bytesTotal = channelInfo.textureSizes.Sum(size => (long)size);
      long compressedTotal = channelInfo.initial_data.Sum(part => (long)part.compressedSize);
      Debug.Log($"STM_INDEX_EXPORT parts={channelInfo.initial_data.Count} textures={textures.Count} rawTextureBytes={bytesTotal} compressedTotalBytes={compressedTotal}");
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
