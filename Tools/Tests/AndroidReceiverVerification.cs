using System;
using System.IO;
using StreamingMesh;
using StreamingMesh.Samples;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Build in the isolated indexed-resource project; source scenes are not edited.
public static class AndroidReceiverVerification
{
  public static void Build()
  {
    try
    {
      var args = Environment.GetCommandLineArgs();
      string output = Path.GetFullPath(args[Array.IndexOf(args, "-androidOutput") + 1]);
      var scene = EditorSceneManager.OpenScene("Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity");
      var controls = UnityEngine.Object.FindFirstObjectByType<KaguraReceiverControls>();
      controls.channelAddress = "http://127.0.0.1:8005/channels/channel_KAGURA_MULTI_V6/";
      controls.connectOnStart = true;
      controls.autoPlayAfterBuffering = true;
      if (controls.GetComponent<AndroidReceiverProbe>() == null) controls.gameObject.AddComponent<AndroidReceiverProbe>();
      var settings = new SerializedObject(controls.receiverTemplate);
      settings.FindProperty("m_LogMemoryDiagnostics").boolValue = true;
      settings.ApplyModifiedPropertiesWithoutUndo();
      EditorSceneManager.SaveScene(scene);
      PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
      PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.blkcatman.streamingmesh.multiformatverification");
      PlayerSettings.productName = "StreamingMesh KAGURA GPU Formats";
      PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
      PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
      PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
      PlayerSettings.runInBackground = true;
      PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
      PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
      PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
      EditorUserBuildSettings.buildAppBundle = false;
      Directory.CreateDirectory(Path.GetDirectoryName(output));
      var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
        scenes = new[] { scene.path }, locationPathName = output,
        target = BuildTarget.Android, options = BuildOptions.Development });
      if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Android build failed: " + report.summary.result);
      Debug.Log("STM_ANDROID_BUILD PASS " + output);
      EditorApplication.Exit(0);
    }
    catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
  }
}
