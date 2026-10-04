using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace StreamingMesh.Editor
{
  public static class StreamingMeshWebBuild
  {
    const string KaguraReceiverScene = "Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity";
    const string DefaultOutput = "Builds/WebReceiver";

    [MenuItem("Tools/StreamingMesh/Configure WebGPU Receiver")]
    public static void ConfigureWebGpuReceiver()
    {
      PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
      PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[]
      {
        GraphicsDeviceType.WebGPU,
        GraphicsDeviceType.OpenGLES3
      });
      PlayerSettings.WebGL.threadsSupport = true;
      PlayerSettings.WebGL.wasm2023 = true;
      PlayerSettings.runInBackground = true;
      // Desktop sample headroom; payload and snapshot pools remain bounded.
      PlayerSettings.WebGL.maximumMemorySize = 4096;
      PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
      PlayerSettings.WebGL.decompressionFallback = true;
      AssetDatabase.SaveAssets();
      Debug.Log("StreamingMesh Web receiver configured for WebGPU with WebGL 2 fallback.");
    }

    [MenuItem("Tools/StreamingMesh/Build Web KAGURA Receiver")]
    public static void BuildKaguraReceiver()
    {
      BuildReceiver(DefaultOutput, KaguraReceiverScene);
    }

    public static void BuildKaguraReceiverCommandLine()
    {
      string output = GetCommandLineValue("-streamingMeshOutput") ?? DefaultOutput;
      BuildReceiver(output, KaguraReceiverScene);
    }

    static void BuildReceiver(string output, string scene)
    {
      ConfigureWebGpuReceiver();
      string fullOutput = Path.GetFullPath(output);
      Directory.CreateDirectory(fullOutput);

      BuildPlayerOptions options = new BuildPlayerOptions
      {
        scenes = new[] { scene },
        locationPathName = fullOutput,
        target = BuildTarget.WebGL,
        options = BuildOptions.None
      };
      BuildReport report = BuildPipeline.BuildPlayer(options);
      if (report.summary.result != BuildResult.Succeeded)
      {
        throw new InvalidOperationException(
          "StreamingMesh Web receiver build failed: " + report.summary.result);
      }

      Debug.Log("StreamingMesh Web receiver built at " + fullOutput);
    }

    static string GetCommandLineValue(string key)
    {
      string[] arguments = Environment.GetCommandLineArgs();
      for (int i = 0; i < arguments.Length - 1; i++)
      {
        if (string.Equals(arguments[i], key, StringComparison.OrdinalIgnoreCase))
          return arguments[i + 1];
      }
      return null;
    }
  }
}
