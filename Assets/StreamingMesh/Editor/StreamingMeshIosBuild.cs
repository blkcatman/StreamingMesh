using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace StreamingMesh.Editor
{
    public static class StreamingMeshIosBuild
    {
        [MenuItem("Tools/StreamingMesh/Build iOS KAGURA Receiver")]
        public static void BuildReceiver()
        {
            BuildReceiver("Assets/Samples/UnityChanKAGURA/Scenes/KaguraReceiver.unity",
                "Builds/iOSKaguraReceiver", null, null);
        }

        [MenuItem("Tools/StreamingMesh/Build iOS Sync Diagnostic Receiver")]
        public static void BuildSyncDiagnosticReceiver()
        {
            BuildReceiver("Assets/Samples/SyncDiagnostic/SyncDiagnosticReceiver.unity",
                "Builds/iOSSyncDiagnosticReceiver", "com.blkcatman.streamingmesh.syncdiagnostic", "STM Sync");
        }

        static void BuildReceiver(string scene, string outputDirectory, string applicationId, string productName)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS)
                throw new InvalidOperationException("Select iOS in Build Profiles before building.");
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.iOS, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal });
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
            PlayerSettings.iOS.targetOSVersionString = "15.0";
            PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
            string output = Path.GetFullPath(outputDirectory);
            var target = UnityEditor.Build.NamedBuildTarget.iOS;
            string previousApplicationId = PlayerSettings.GetApplicationIdentifier(target);
            string previousProductName = PlayerSettings.productName;
            try
            {
                if (applicationId != null) PlayerSettings.SetApplicationIdentifier(target, applicationId);
                if (productName != null) PlayerSettings.productName = productName;
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scene },
                    locationPathName = output,
                    target = BuildTarget.iOS,
                    options = BuildOptions.Development
                });
                Debug.Log($"iOS receiver ({scene}): {report.summary.result}, errors={report.summary.totalErrors}, output={output}");
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("iOS receiver build failed.");
            }
            finally
            {
                PlayerSettings.SetApplicationIdentifier(target, previousApplicationId);
                PlayerSettings.productName = previousProductName;
                // BuildPlayer saves the temporary identity to ProjectSettings;
                // Save Project also persists the restored PlayerSettings.
                EditorApplication.ExecuteMenuItem("File/Save Project");
            }
#if UNITY_IOS
            // Only this local-test export receives LAN HTTP permissions.
            var plist = new UnityEditor.iOS.Xcode.PlistDocument();
            string path = Path.Combine(output, "Info.plist");
            plist.ReadFromFile(path);
            plist.root.SetString("NSLocalNetworkUsageDescription", "Receive StreamingMesh mesh and audio streams from your local development server.");
            var ats = plist.root.values.ContainsKey("NSAppTransportSecurity")
                ? plist.root["NSAppTransportSecurity"].AsDict()
                : plist.root.CreateDict("NSAppTransportSecurity");
            ats.SetBoolean("NSAllowsArbitraryLoads", false);
            ats.SetBoolean("NSAllowsLocalNetworking", true);
            plist.WriteToFile(path);
#endif
        }
    }
}
