using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace StreamingMesh.Editor
{
  public static class StreamingMeshMacBuild
  {
    const string DefaultOutput = "Builds/MacReceiver/Receiver-arm64.app";

    [MenuItem("Tools/StreamingMesh/Configure macOS ARM64")]
    public static void ConfigureArm64()
    {
      UnityEditor.OSXStandalone.UserBuildSettings.architecture = UnityEditor.Build.OSArchitecture.ARM64;
      Debug.Log("StreamingMesh macOS architecture configured for Apple Silicon (ARM64).");
    }

    [MenuItem("Tools/StreamingMesh/Build macOS ARM64 KAGURA Receiver")]
    public static void BuildReceiver()
    {
      BuildReceiver(DefaultOutput, BuildOptions.None);
    }

    public static void BuildReceiverCommandLine()
    {
      string[] args = Environment.GetCommandLineArgs();
      string output = DefaultOutput;
      int outputIndex = Array.IndexOf(args, "-streamingMeshOutput");
      if (outputIndex >= 0)
      {
        if (outputIndex + 1 >= args.Length || args[outputIndex + 1].StartsWith("-"))
          throw new ArgumentException("-streamingMeshOutput requires an .app path.");
        output = args[outputIndex + 1];
      }
      var options = BuildOptions.None;
      if (args.Contains("-streamingMeshDevelopment")) options |= BuildOptions.Development;
      if (args.Contains("-streamingMeshCleanBuild")) options |= BuildOptions.CleanBuildCache;
      BuildReceiver(output, options);
    }

    static void BuildReceiver(string output, BuildOptions options)
    {
      if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneOSX)
        throw new InvalidOperationException("Select macOS in Build Profiles or launch Unity with -buildTarget StandaloneOSX.");
      if (!output.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("The macOS build output must end with .app.");

      ConfigureArm64();
      string fullOutput = Path.GetFullPath(output);
      Directory.CreateDirectory(Path.GetDirectoryName(fullOutput));
      var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
      {
        scenes = new[] { "Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity" },
        locationPathName = fullOutput,
        target = BuildTarget.StandaloneOSX,
        options = options
      });
      Debug.Log($"StreamingMesh ARM64 build: {report.summary.result}, errors={report.summary.totalErrors}, warnings={report.summary.totalWarnings}, output={fullOutput}");
      if (report.summary.result != BuildResult.Succeeded)
        throw new InvalidOperationException("StreamingMesh macOS ARM64 build failed: " + report.summary.result);
    }
  }
}
