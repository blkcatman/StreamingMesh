using System;
using UnityEngine;

namespace StreamingMesh.Samples
{
  public sealed class SyncDiagnosticReceiverControls : MonoBehaviour
  {
    public Receiver receiverTemplate;
    public string channelAddress = "http://127.0.0.1:8000/channels/channel_sync_mesh/";
    public bool autoPlayAfterBuffering = true;
    const string SavedChannelKey = "StreamingMesh.SyncDiagnostic.Channel";
    Receiver activeReceiver;
    ReceiverCameraControls cameraControls;
    string status = "Record the diagnostic scene, then connect.";
    bool scrubbing;
    float scrubTime;

    void Update()
    {
      // IMGUI sliders consume MouseUp before OnGUI sees its event type. Commit
      // the selected position on release so the audio and mesh really seek.
      if (scrubbing && !Input.GetMouseButton(0) && activeReceiver != null)
      {
        activeReceiver.Seek(scrubTime);
        scrubbing = false;
      }
    }

    void Start()
    {
      cameraControls = GetComponent<ReceiverCameraControls>() ?? gameObject.AddComponent<ReceiverCameraControls>();
#if UNITY_ANDROID || UNITY_IOS
      Application.targetFrameRate = 60;
#endif
#if UNITY_IOS && !UNITY_EDITOR
      // A device cannot reach the Mac through localhost. Remember the URL
      // supplied for the local test so subsequent Home Screen launches work.
      channelAddress = PlayerPrefs.GetString(SavedChannelKey, channelAddress);
#endif
      var args = Environment.GetCommandLineArgs();
      int index = Array.IndexOf(args, "-streamingMeshChannel");
      if (index >= 0 && index + 1 < args.Length)
      {
        channelAddress = args[index + 1];
        Connect();
      }
      else
      {
        // IL2CPP on iOS does not expose the process arguments through
        // Environment.GetCommandLineArgs. devicectl can provide this instead.
        string launchChannel = Environment.GetEnvironmentVariable("STREAMINGMESH_CHANNEL");
        if (!string.IsNullOrWhiteSpace(launchChannel))
        {
          channelAddress = launchChannel;
          Connect();
        }
      }
    }

    public void Connect()
    {
      if (activeReceiver != null) Destroy(activeReceiver.gameObject);
      activeReceiver = null;
      try
      {
        activeReceiver = Instantiate(receiverTemplate);
        activeReceiver.name = "Sync Diagnostic Live Receiver";
        activeReceiver.ConfigureChannel(channelAddress.Trim());
        activeReceiver.ConfigurePlayback(autoPlayAfterBuffering);
#if UNITY_IOS && !UNITY_EDITOR
        PlayerPrefs.SetString(SavedChannelKey, channelAddress.Trim());
        PlayerPrefs.Save();
#endif
        activeReceiver.gameObject.SetActive(true);
        Debug.Log("Sync diagnostic receiver connecting to " + channelAddress);
        scrubbing = false;
        status = "Connecting...";
      }
      catch (Exception exception)
      {
        if (activeReceiver != null) Destroy(activeReceiver.gameObject);
        activeReceiver = null;
        status = exception.Message;
      }
    }

    void ConnectSample(string channel)
    {
      string address = channelAddress.TrimEnd('/');
      int lastSlash = address.LastIndexOf('/');
      if (lastSlash >= 0)
        channelAddress = address.Substring(0, lastSlash + 1) + channel + "/";
      Connect();
    }

    void OnGUI()
    {
      float scale = Mathf.Max(1, Screen.height / 540f);
      Matrix4x4 previous = GUI.matrix;
      GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
      Rect safe = Screen.safeArea;
      GUILayout.BeginArea(new Rect(safe.x / scale + 12, (Screen.height - safe.yMax) / scale + 12,
        Mathf.Min(350, safe.width / scale - 24),
        Mathf.Min(cameraControls != null && cameraControls.IsOpen ? 340 : 365, safe.height / scale - 24)), GUI.skin.box);
      GUILayout.Label("StreamingMesh 1 Hz sync receiver");
      if (cameraControls != null)
      {
        cameraControls.DrawToggle();
        if (cameraControls.IsOpen)
        {
          cameraControls.DrawPanel();
          GUILayout.EndArea();
          GUI.matrix = previous;
          return;
        }
      }
      channelAddress = GUILayout.TextField(channelAddress);
      autoPlayAfterBuffering = GUILayout.Toggle(autoPlayAfterBuffering,
        "Auto-play after initial buffering (next connect)");
      GUILayout.BeginHorizontal();
      if (GUILayout.Button("Mesh sample", GUILayout.Height(32))) ConnectSample("channel_sync_mesh");
      if (GUILayout.Button("Skinned sample", GUILayout.Height(32))) ConnectSample("channel_sync_skinned");
      GUILayout.EndHorizontal();
      GUILayout.BeginHorizontal();
      if (GUILayout.Button("Connect / Reconnect", GUILayout.Height(32))) Connect();
      if (GUILayout.Button("Disconnect", GUILayout.Height(32)))
      {
        if (activeReceiver != null) Destroy(activeReceiver.gameObject);
        activeReceiver = null;
        status = "Disconnected";
      }
      GUILayout.EndHorizontal();
      GUILayout.BeginHorizontal();
      GUI.enabled = activeReceiver != null;
      if (GUILayout.Button(activeReceiver != null && activeReceiver.IsPlaybackRequested ? "Pause" : "Play",
          GUILayout.Height(32)))
      {
        if (activeReceiver.IsPlaybackRequested) activeReceiver.Pause();
        else activeReceiver.Play();
      }
      if (GUILayout.Button("Stop", GUILayout.Height(32))) activeReceiver.Stop();
      GUI.enabled = true;
      GUILayout.EndHorizontal();

      double duration = activeReceiver != null ? activeReceiver.DurationSeconds : 0;
      double position = activeReceiver != null ? activeReceiver.CurrentTimeSeconds : 0;
      GUILayout.BeginHorizontal();
      GUI.enabled = activeReceiver != null && duration > 0;
      if (GUILayout.Button("-5 s", GUILayout.Height(28)))
      {
        scrubbing = false;
        activeReceiver.Seek(Math.Max(0, position - 5));
      }
      if (GUILayout.Button("+5 s", GUILayout.Height(28)))
      {
        scrubbing = false;
        activeReceiver.Seek(Math.Min(duration, position + 5));
      }
      GUI.enabled = true;
      GUILayout.EndHorizontal();
      float shownTime = scrubbing ? scrubTime : (float)position;
      GUILayout.Label($"{FormatTime(shownTime)} / {FormatTime(duration)}");
      GUI.enabled = activeReceiver != null && duration > 0;
      float nextTime = GUILayout.HorizontalSlider(shownTime, 0, (float)Math.Max(0.01, duration));
      if (Mathf.Abs(nextTime - shownTime) > 0.001f)
      {
        scrubTime = nextTime;
        scrubbing = true;
      }
      GUI.enabled = true;
      GUILayout.Label(activeReceiver != null ? activeReceiver.ConnectionStatus : status);
      GUILayout.EndArea();
      GUI.matrix = previous;
    }

    static string FormatTime(double seconds)
    {
      if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
      int minutes = (int)(seconds / 60);
      return $"{minutes:00}:{seconds - minutes * 60:00.0}";
    }
  }
}
