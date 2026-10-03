using StreamingMesh.Samples;
using TimeWire.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;

namespace StreamingMesh.Editor
{
    public static class TimeWireSampleSetup
    {
        [MenuItem("Tools/StreamingMesh/TimeWire/Configure Local Sample Clocks")]
        public static void Configure()
        {
            if (EditorApplication.isPlaying)
                throw new System.InvalidOperationException("Configure clocks outside Play mode.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string[] scenes = {
                "Assets/Samples/SyncDiagnostic/SyncDiagnosticSender.unity",
                "Assets/Samples/SyncDiagnostic/SyncDiagnosticSkinnedSender.unity",
                "Assets/Samples/UnityChanKAGURA/Scenes/KaguraDemo.unity"
            };
            foreach (string path in scenes)
            {
                var scene = EditorSceneManager.OpenScene(path);
                var sender = Object.FindAnyObjectByType<STMHttpSender>();
                var source = sender.GetComponent<AudioDspClockSource>() ?? sender.gameObject.AddComponent<AudioDspClockSource>();
                sender.captureClockSource = source;
                sender.useTimeWireClock = true;
                var diagnostic = Object.FindAnyObjectByType<SyncDiagnosticController>();
                if (diagnostic != null) diagnostic.clockSource = source;
                var kagura = Object.FindAnyObjectByType<KaguraDemoControls>();
                if (kagura != null)
                {
                    var director = kagura.director;
                    director.timeUpdateMode = DirectorUpdateMode.DSPClock;
                    var transport = director.GetComponent<TransportClockSource>() ?? director.gameObject.AddComponent<TransportClockSource>();
                    var serialized = new SerializedObject(transport);
                    serialized.FindProperty("source").objectReferenceValue = source;
                    serialized.FindProperty("playOnEnable").boolValue = true;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    var controller = director.GetComponent<TimelineClockController>() ?? director.gameObject.AddComponent<TimelineClockController>();
                    controller.director = director;
                    controller.source = transport;
                    controller.mode = TimelineClockMode.CorrectPlayback;
                    controller.loop = false;
                    controller.preserveNativeRate = true;
                    controller.maximumSpeedAdjustment = 0;
                    controller.hardThresholdSeconds = 1;
                    kagura.playbackClock = transport;
                    kagura.timelineClock = controller;
                }
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            Debug.Log("TimeWire local sample clocks configured. Network clock inputs are not required.");
        }
    }
}
