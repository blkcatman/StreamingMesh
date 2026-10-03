using System.IO;
using StreamingMesh.Samples;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StreamingMesh.Editor
{
  public static class SyncDiagnosticSceneBuilder
  {
    const string Folder = "Assets/Samples/SyncDiagnostic";

    [MenuItem("Tools/StreamingMesh/Create Sync Diagnostic Scenes")]
    public static void CreateScenes()
    {
      Directory.CreateDirectory(Folder);
      string materialPath = Folder + "/SyncMarker.mat";
      if (AssetDatabase.LoadAssetAtPath<Material>(materialPath) == null)
      {
        var material = new Material(Shader.Find("StreamingMesh/Standard"));
        material.name = "SyncMarker";
        material.SetColor("_Color", new Color(1f, 0.75f, 0.05f));
        AssetDatabase.CreateAsset(material, materialPath);
      }

      Material markerMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
      CreateSender(markerMaterial, false);
      CreateSender(markerMaterial, true);
      CreateReceiver();
      AssetDatabase.SaveAssets();
      AssetDatabase.Refresh();
      Debug.Log("Sync diagnostic scenes ready in " + Folder);
    }

    [MenuItem("Tools/StreamingMesh/Open Sync Diagnostic Sender")]
    public static void OpenSender()
    {
      EditorSceneManager.OpenScene(Folder + "/SyncDiagnosticSender.unity");
    }

    [MenuItem("Tools/StreamingMesh/Open Sync Diagnostic Skinned Sender")]
    public static void OpenSkinnedSender()
    {
      EditorSceneManager.OpenScene(Folder + "/SyncDiagnosticSkinnedSender.unity");
    }

    [MenuItem("Tools/StreamingMesh/Open Sync Diagnostic Receiver")]
    public static void OpenReceiver()
    {
      EditorSceneManager.OpenScene(Folder + "/SyncDiagnosticReceiver.unity");
    }

    [MenuItem("Tools/StreamingMesh/Sync Diagnostic/Create Channel")]
    public static void CreateDiagnosticChannel() => GetRunningController().sender.CreateChannel();

    [MenuItem("Tools/StreamingMesh/Sync Diagnostic/Record from Start")]
    public static void RecordDiagnostic() => GetRunningController().RecordFromStart();

    [MenuItem("Tools/StreamingMesh/Sync Diagnostic/Stop Recording")]
    public static void StopDiagnostic() => GetRunningController().StopCapture();

    [MenuItem("Tools/StreamingMesh/Sync Diagnostic/Connect Receiver")]
    public static void ConnectDiagnosticReceiver()
    {
      GetRunningReceiver().Connect();
    }

    [MenuItem("Tools/StreamingMesh/Sync Diagnostic/Connect Skinned Receiver")]
    public static void ConnectSkinnedDiagnosticReceiver()
    {
      var controls = GetRunningReceiver();
      controls.channelAddress = "http://127.0.0.1:8000/channels/channel_sync_skinned/";
      controls.Connect();
    }

    static SyncDiagnosticController GetRunningController()
    {
      if (!EditorApplication.isPlaying)
        throw new System.InvalidOperationException("Play the SyncDiagnosticSender scene first.");
      var controls = Object.FindAnyObjectByType<SyncDiagnosticController>();
      if (controls == null)
        throw new System.InvalidOperationException("The sync diagnostic sender is not in this scene.");
      return controls;
    }

    static SyncDiagnosticReceiverControls GetRunningReceiver()
    {
      if (!EditorApplication.isPlaying)
        throw new System.InvalidOperationException("Play the SyncDiagnosticReceiver scene first.");
      var controls = Object.FindAnyObjectByType<SyncDiagnosticReceiverControls>();
      if (controls == null)
        throw new System.InvalidOperationException("The sync diagnostic receiver is not in this scene.");
      return controls;
    }

    static Camera CreateCamera(string name)
    {
      var gameObject = new GameObject(name);
      gameObject.tag = "MainCamera";
      gameObject.transform.position = new Vector3(0, 0, -10);
      var camera = gameObject.AddComponent<Camera>();
      camera.orthographic = true;
      camera.orthographicSize = 3.3f;
      camera.clearFlags = CameraClearFlags.SolidColor;
      camera.backgroundColor = new Color(0.05f, 0.08f, 0.12f);
      gameObject.AddComponent<AudioListener>();
      return camera;
    }

    static void CreateSender(Material material, bool skinned)
    {
      string path = Folder + (skinned ? "/SyncDiagnosticSkinnedSender.unity" : "/SyncDiagnosticSender.unity");
      Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
      Camera camera = CreateCamera("Diagnostic Sender Camera");
      camera.gameObject.SetActive(false);

      GameObject marker;
      Transform bone = null;
      if (skinned)
      {
        marker = new GameObject("One Second Skinned Mesh Marker");
        var boneObject = new GameObject("Animated Bone");
        bone = boneObject.transform;
        bone.SetParent(marker.transform, false);
        string meshPath = Folder + "/SyncSkinnedCube.asset";
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        if (mesh == null)
        {
          var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
          mesh = Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
          mesh.name = "SyncSkinnedCube";
          Object.DestroyImmediate(primitive);
          var weights = new BoneWeight[mesh.vertexCount];
          for (int i = 0; i < weights.Length; i++)
            weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
          mesh.boneWeights = weights;
          mesh.bindposes = new[] { Matrix4x4.identity };
          AssetDatabase.CreateAsset(mesh, meshPath);
        }
        var renderer = marker.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.sharedMaterial = material;
        renderer.bones = new[] { bone };
        renderer.rootBone = bone;
        renderer.updateWhenOffscreen = true;
      }
      else
      {
        marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = "One Second Mesh Marker";
        marker.GetComponent<MeshRenderer>().sharedMaterial = material;
        Object.DestroyImmediate(marker.GetComponent<Collider>());
      }
      marker.transform.position = new Vector3(-2, 0, 0);
      marker.transform.localScale = Vector3.one * 0.65f;

      var serializer = camera.gameObject.AddComponent<STMHttpSerializer>();
      serializer.address = "http://127.0.0.1:8000/channels/";
      serializer.channel = skinned ? "channel_sync_skinned" : "channel_sync_mesh";
      var sender = camera.gameObject.AddComponent<STMHttpSender>();
      sender.targetGameObject = marker;
      sender.frameRate = 30;
      sender.subframesPerKeyframe = 9;
      sender.combinedFrames = 30;
      var recorder = camera.gameObject.GetComponent<STMAudioRecorder>();
      if (File.Exists("/opt/homebrew/bin/ffmpeg"))
      {
        var serialized = new SerializedObject(recorder);
        serialized.FindProperty("ffmpegPath").stringValue = "/opt/homebrew/bin/ffmpeg";
        serialized.ApplyModifiedPropertiesWithoutUndo();
      }
      var beepObject = new GameObject("Scheduled one-second beep");
      beepObject.transform.SetParent(camera.transform, false);
      var source = beepObject.AddComponent<AudioSource>();
      var controls = camera.gameObject.AddComponent<SyncDiagnosticController>();
      controls.sender = sender;
      controls.marker = marker.transform;
      controls.skinnedBone = bone;
      controls.beepSource = source;
      controls.captureSeconds = 30;
      camera.gameObject.SetActive(true);
      EditorSceneManager.SaveScene(scene, path);
    }

    static void CreateReceiver()
    {
      string path = Folder + "/SyncDiagnosticReceiver.unity";
      if (File.Exists(path)) return;
      Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
      Camera camera = CreateCamera("Diagnostic Receiver Camera");
      camera.transform.position = new Vector3(-2, 0, -10);
      camera.orthographic = false;
      // Match the former orthographic framing at the marker's Z=0 plane,
      // while allowing camera distance to change the apparent model size.
      camera.fieldOfView = 2 * Mathf.Atan(3.3f / 10f) * Mathf.Rad2Deg;

      var template = new GameObject("Inactive Receiver Template");
      template.SetActive(false);
      var receiver = template.AddComponent<Receiver>();
      receiver.m_DefaultShader = Shader.Find("StreamingMesh/Standard");
      receiver.m_CustomShaders = new ShaderTable();

      var controls = camera.gameObject.AddComponent<SyncDiagnosticReceiverControls>();
      controls.receiverTemplate = receiver;
      EditorSceneManager.SaveScene(scene, path);
    }
  }
}
