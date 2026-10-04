using System;
using System.Reflection;
using StreamingMesh;
using StreamingMesh.Core.Serialization;
using UnityEngine;
using UnityEngine.Profiling;

// Allocating verification snapshots only. Not included in the production scene.
public sealed class AndroidReceiverProbe : MonoBehaviour
{
  double next;
  Receiver receiver;
  [Serializable] sealed class Snapshot
  {
    public double seconds, playback;
    public string status;
    public int meshes, textures;
    public long nativeAllocated, nativeReserved, monoUsed;
    public float fps;
  }
  void Awake()
  {
#if UNITY_ANDROID && !UNITY_EDITOR
    // Optional adb am start --es override for comparing the exported variants.
    using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
    using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
    using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
    {
      string format = intent.Call<string>("getStringExtra", "streamingMeshTextureFormat");
      if (!string.IsNullOrEmpty(format))
      {
        new TexturePayloadInfo { format = format }.UnityFormat();
        var controls = GetComponent<StreamingMesh.Samples.KaguraReceiverControls>();
        typeof(Receiver).GetField("m_PreferredTextureFormat", BindingFlags.Instance | BindingFlags.NonPublic)
          .SetValue(controls.receiverTemplate, format);
        Debug.Log("STM_ANDROID_REQUESTED_FORMAT " + format);
      }
    }
#endif
  }
  void Start()
  {
    Debug.Log("STM_ANDROID_DEVICE " + SystemInfo.deviceModel + " / " + SystemInfo.operatingSystem +
      " / " + SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceType + " maxTexture=" + SystemInfo.maxTextureSize);
    foreach (GpuTextureFormat format in Enum.GetValues(typeof(GpuTextureFormat)))
      Debug.Log("STM_ANDROID_CAPABILITY " + format + "=" + new TexturePayloadInfo {
        format = format.ToString(), width = 8192, height = 8192, mipCount = 1, linear = false }.IsSupported());
  }
  void Update()
  {
    if (Time.realtimeSinceStartupAsDouble < next) return;
    next = Time.realtimeSinceStartupAsDouble + 5;
    if (receiver == null) receiver = FindFirstObjectByType<Receiver>();
    var renderer = receiver == null ? null : typeof(Receiver).GetField("m_MeshRenderer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(receiver)
      as StreamingMesh.Core.Rendering.StreamingMeshRenderer;
    Debug.Log("STM_ANDROID_STATE " + JsonUtility.ToJson(new Snapshot {
      seconds = Time.realtimeSinceStartupAsDouble, playback = receiver == null ? 0 : receiver.CurrentTimeSeconds,
      status = receiver == null ? "Waiting" : receiver.ConnectionStatus,
      meshes = receiver == null ? 0 : receiver.GetComponentsInChildren<MeshFilter>().Length,
      textures = renderer == null ? 0 : renderer.TextureDictionary.Count,
      nativeAllocated = Profiler.GetTotalAllocatedMemoryLong(), nativeReserved = Profiler.GetTotalReservedMemoryLong(),
      monoUsed = Profiler.GetMonoUsedSizeLong(), fps = Time.unscaledDeltaTime > 0 ? 1 / Time.unscaledDeltaTime : 0 }));
  }
}
