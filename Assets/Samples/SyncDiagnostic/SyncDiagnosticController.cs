using System.Collections.Generic;
using UnityEngine;
using TimeWire;
using TimeWire.Unity;

namespace StreamingMesh.Samples
{
  /// <summary>One-revolution-per-second clock and DSP-scheduled beep.</summary>
  public sealed class SyncDiagnosticController : MonoBehaviour
  {
    public STMHttpSender sender;
    public Transform marker;
    public Transform skinnedBone;
    public AudioSource beepSource;
    public AudioDspClockSource clockSource;
    [Min(2)] public float captureSeconds = 30;

    AudioClip beepClip;
    GameObject clockRoot;
    Mesh dialMesh;
    Mesh handMesh;
    Material dialMaterial;
    double firstBeatDspTime;
    ClockTime firstBeatTime;
    bool running;

    void Awake()
    {
      BuildClock();
      if (clockSource == null)
        clockSource = GetComponent<AudioDspClockSource>() ?? gameObject.AddComponent<AudioDspClockSource>();
      if (sender.captureClockSource == null) sender.captureClockSource = clockSource;
      sender.BeforeCapture += EvaluateCapturePose;
      int sampleRate = AudioSettings.outputSampleRate;
      beepClip = AudioClip.Create("Sync Diagnostic 1 Hz Beep", sampleRate, 1, sampleRate, false);
      var samples = new float[sampleRate];
      int beepSamples = Mathf.RoundToInt(sampleRate * 0.1f);
      int fadeSamples = Mathf.Max(1, Mathf.RoundToInt(sampleRate * 0.005f));
      for (int i = 0; i < beepSamples; i++)
      {
        float envelope = Mathf.Min(1f, i / (float)fadeSamples,
          (beepSamples - 1 - i) / (float)fadeSamples);
        samples[i] = 0.6f * envelope * Mathf.Sin(2f * Mathf.PI * 880f * i / sampleRate);
      }
      beepClip.SetData(samples, 0);
      beepSource.clip = beepClip;
      beepSource.loop = true;
      beepSource.playOnAwake = false;
      beepSource.spatialBlend = 0;
    }

    static void AddQuad(List<Vector3> vertices, List<int> triangles,
      Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
      int first = vertices.Count;
      vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
      triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
      triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3);
    }

    static Mesh CreateDialMesh()
    {
      var vertices = new List<Vector3>();
      var triangles = new List<int>();
      const int ringSegments = 120;
      for (int i = 0; i < ringSegments; i++)
      {
        float a0 = i * Mathf.PI * 2f / ringSegments;
        float a1 = (i + 1) * Mathf.PI * 2f / ringSegments;
        Vector3 inner0 = new Vector3(Mathf.Sin(a0) * 1.88f, Mathf.Cos(a0) * 1.88f, 0);
        Vector3 outer0 = new Vector3(Mathf.Sin(a0) * 1.92f, Mathf.Cos(a0) * 1.92f, 0);
        Vector3 inner1 = new Vector3(Mathf.Sin(a1) * 1.88f, Mathf.Cos(a1) * 1.88f, 0);
        Vector3 outer1 = new Vector3(Mathf.Sin(a1) * 1.92f, Mathf.Cos(a1) * 1.92f, 0);
        AddQuad(vertices, triangles, inner0, outer0, outer1, inner1);
      }
      for (int i = 0; i < 60; i++)
      {
        float angle = i * Mathf.PI * 2f / 60f;
        bool major = i % 5 == 0;
        float halfWidth = major ? 0.035f : 0.015f;
        float innerRadius = major ? 1.58f : 1.72f;
        Vector3 radial = new Vector3(Mathf.Sin(angle), Mathf.Cos(angle), 0);
        Vector3 tangent = new Vector3(Mathf.Cos(angle), -Mathf.Sin(angle), 0);
        AddQuad(vertices, triangles,
          radial * innerRadius - tangent * halfWidth,
          radial * 1.87f - tangent * halfWidth,
          radial * 1.87f + tangent * halfWidth,
          radial * innerRadius + tangent * halfWidth);
      }
      var mesh = new Mesh { name = "Sync Clock 60 Tick Dial" };
      mesh.SetVertices(vertices);
      mesh.SetTriangles(triangles, 0);
      mesh.RecalculateBounds();
      return mesh;
    }

    static Mesh CreateHandMesh(bool skinned)
    {
      var mesh = new Mesh { name = skinned ? "Sync Skinned Clock Hand" : "Sync Clock Hand" };
      mesh.vertices = new[] {
        new Vector3(-0.10f, -0.35f, -0.03f),
        new Vector3( 0.10f, -0.35f, -0.03f),
        new Vector3( 0.065f, 1.50f, -0.03f),
        new Vector3( 0f, 1.78f, -0.03f),
        new Vector3(-0.065f, 1.50f, -0.03f)
      };
      mesh.triangles = new[] { 0, 1, 2, 0, 2, 4, 4, 2, 3 };
      if (skinned)
      {
        var weights = new BoneWeight[mesh.vertexCount];
        for (int i = 0; i < weights.Length; i++)
          weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
        mesh.boneWeights = weights;
        mesh.bindposes = new[] { Matrix4x4.identity };
      }
      mesh.RecalculateBounds();
      return mesh;
    }

    void BuildClock()
    {
      clockRoot = new GameObject("Streamed stopwatch");
      marker.SetParent(clockRoot.transform, false);
      marker.localPosition = Vector3.zero;
      marker.localRotation = Quaternion.identity;
      marker.localScale = Vector3.one;
      sender.targetGameObject = clockRoot;

      dialMesh = CreateDialMesh();
      dialMaterial = new Material(Shader.Find("StreamingMesh/Standard")) { name = "SyncDial" };
      dialMaterial.SetColor("_Color", new Color(0.42f, 0.9f, 1f));
      var dial = new GameObject("Sixty second marks and rim");
      dial.transform.SetParent(clockRoot.transform, false);
      dial.AddComponent<MeshFilter>().sharedMesh = dialMesh;
      dial.AddComponent<MeshRenderer>().sharedMaterial = dialMaterial;

      handMesh = CreateHandMesh(skinnedBone != null);
      if (skinnedBone != null)
      {
        var skinned = marker.GetComponent<SkinnedMeshRenderer>();
        skinned.sharedMesh = handMesh;
        skinned.localBounds = new Bounds(Vector3.zero, Vector3.one * 4f);
      }
      else
        marker.GetComponent<MeshFilter>().sharedMesh = handMesh;
    }

    public void RecordFromStart()
    {
      if (sender == null || marker == null || beepSource == null || sender.IsStartRecord)
        return;
      beepSource.Stop();
      clockRoot.transform.position = Vector3.zero;
      marker.localRotation = Quaternion.identity;
      if (skinnedBone != null) skinnedBone.localRotation = Quaternion.identity;
      sender.Record();
      if (!sender.IsStartRecord) return;

      // Leave half a second of silence before the first paired visual/audio tick.
      firstBeatTime = clockSource.GetSnapshot().Time.Add(ClockTime.FromSeconds(0.5));
      firstBeatDspTime = sender.IsTimeWireRecording ? clockSource.ToDspTime(firstBeatTime) : AudioSettings.dspTime + 0.5;
      beepSource.PlayScheduled(firstBeatDspTime);
      // The audio mixer can render ahead of LateUpdate. End between beats so
      // stopping capture cannot append an unmatched 31st beep.
      beepSource.SetScheduledEndTime(firstBeatDspTime + captureSeconds - 0.2);
      running = true;
      Debug.Log($"Sync diagnostic started: first beat DSP={firstBeatDspTime:F6}, " +
        $"record realtime={Time.realtimeSinceStartupAsDouble:F6}");
    }

    public void StopCapture()
    {
      running = false;
      beepSource.Stop();
      if (sender != null && sender.IsStartRecord) sender.Stop();
    }

    void LateUpdate()
    {
      if (!running) return;
      double elapsed = sender.IsTimeWireRecording
        ? clockSource.GetSnapshot().Time.Subtract(firstBeatTime).TotalSeconds
        : AudioSettings.dspTime - firstBeatDspTime;
      if (elapsed < 0) return;
      if (elapsed >= captureSeconds)
      {
        StopCapture();
        return;
      }

      SetPose(elapsed);
    }

    void EvaluateCapturePose(ClockTime time)
    {
      if (running) SetPose(time.Subtract(firstBeatTime).TotalSeconds);
    }

    void SetPose(double elapsed)
    {
      if (elapsed < 0) return;
      float phase = (float)(elapsed - System.Math.Floor(elapsed));
      // The hand completes a clockwise revolution on the same DSP clock as
      // the scheduled beep. The dial stays fixed throughout the recording.
      Quaternion handRotation = Quaternion.Euler(0, 0, -360f * phase);
      if (skinnedBone != null)
        skinnedBone.localRotation = handRotation;
      else
        marker.localRotation = handRotation;
    }

    void OnGUI()
    {
      GUILayout.BeginArea(new Rect(16, 16, 410, 150), GUI.skin.box);
      GUILayout.Label("StreamingMesh 1 Hz sync diagnostic");
      GUILayout.Label("Beep when the clockwise hand crosses 12 o'clock");
      GUILayout.Label(running ? $"Recording: {AudioSettings.dspTime - firstBeatDspTime:F2} / {captureSeconds:F0} s"
        : "Create channel, then record from start");
#if UNITY_EDITOR
      GUILayout.BeginHorizontal();
      if (GUILayout.Button("Create Channel")) sender.CreateChannel();
      GUI.enabled = sender != null && !sender.IsStartRecord;
      if (GUILayout.Button("Record from Start")) RecordFromStart();
      GUI.enabled = sender != null && sender.IsStartRecord;
      if (GUILayout.Button("Stop")) StopCapture();
      GUI.enabled = true;
      GUILayout.EndHorizontal();
#endif
      GUILayout.EndArea();
    }

    void OnDisable()
    {
      if (sender != null) sender.BeforeCapture -= EvaluateCapturePose;
      if (running) StopCapture();
      if (beepClip != null) Destroy(beepClip);
      if (dialMesh != null) Destroy(dialMesh);
      if (handMesh != null) Destroy(handMesh);
      if (dialMaterial != null) Destroy(dialMaterial);
    }
  }
}
