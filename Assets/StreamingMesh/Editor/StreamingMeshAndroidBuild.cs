using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace StreamingMesh.Editor
{
  public static class StreamingMeshAndroidBuild
  {
    const string Scene = "Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity";
    const string DefaultOutput = "Builds/AndroidKaguraReceiver.apk";
    const string DiagnosticScene = "Assets/Samples/SyncDiagnostic/SyncDiagnosticReceiver.unity";
    const string DiagnosticOutput = "Builds/AndroidSyncDiagnosticReceiver.apk";

    [MenuItem("Tools/StreamingMesh/Build Android KAGURA Receiver")]
    public static void BuildReceiver()
    {
      BuildReceiver(Scene, DefaultOutput, "com.blkcatman.streamingmesh.receiver");
    }

    public static void BuildReceiverCommandLine()
    {
      BuildReceiver();
    }

    [MenuItem("Tools/StreamingMesh/Build Android Sync Diagnostic Receiver")]
    public static void BuildSyncDiagnosticReceiver()
    {
      BuildReceiver(DiagnosticScene, DiagnosticOutput, "com.blkcatman.streamingmesh.syncdiagnostic");
    }

    static void BuildReceiver(string scene, string output, string applicationId)
    {
      if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        throw new InvalidOperationException("Select Android in Build Profiles or launch Unity with -buildTarget Android.");

      PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
      PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
      PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
      EditorUserBuildSettings.buildAppBundle = false;

      string fullOutput = Path.GetFullPath(output);
      Directory.CreateDirectory(Path.GetDirectoryName(fullOutput));
      string previousApplicationId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
      try
      {
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, applicationId);
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
          scenes = new[] { scene },
          locationPathName = fullOutput,
          target = BuildTarget.Android,
          options = BuildOptions.Development
        });
        Debug.Log($"Android receiver ({scene}): {report.summary.result}, errors={report.summary.totalErrors}, output={fullOutput}");
        if (report.summary.result != BuildResult.Succeeded)
          throw new InvalidOperationException("StreamingMesh Android receiver build failed: " + report.summary.result);
      }
      finally
      {
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, previousApplicationId);
      }
    }
  }

}
