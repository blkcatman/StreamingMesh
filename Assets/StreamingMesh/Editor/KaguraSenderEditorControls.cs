using System;
using StreamingMesh.Samples;
using UnityEditor;
using UnityEngine;

namespace StreamingMesh.Editor
{
  public static class KaguraSenderEditorControls
  {
    [MenuItem("Tools/StreamingMesh/KAGURA/Create Channel")]
    public static void CreateChannel()
    {
      GetControls().sender.CreateChannel();
    }

    [MenuItem("Tools/StreamingMesh/KAGURA/Record from Start")]
    public static void RecordFromStart()
    {
      var controls = GetControls();
      controls.Restart();
      controls.sender.Record();
    }

    [MenuItem("Tools/StreamingMesh/KAGURA/Stop Recording")]
    public static void StopRecording()
    {
      GetControls().sender.Stop();
    }

    static KaguraDemoControls GetControls()
    {
      if (!EditorApplication.isPlaying)
        throw new InvalidOperationException("Play the KaguraDemo scene first.");
      var controls = UnityEngine.Object.FindAnyObjectByType<KaguraDemoControls>();
      if (controls == null || controls.sender == null)
        throw new InvalidOperationException("KaguraDemo Sender is not available in the playing scene.");
      return controls;
    }
  }
}
