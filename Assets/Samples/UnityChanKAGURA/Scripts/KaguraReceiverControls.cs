using System;
using UnityEngine;

namespace StreamingMesh.Samples
{
    public sealed class KaguraReceiverControls : MonoBehaviour
    {
        public Receiver receiverTemplate;
        public string channelAddress = "http://127.0.0.1:8000/channels/channel_KAGURA/";
        public bool connectOnStart;
        public bool autoPlayAfterBuffering = true;
        Receiver activeReceiver;
        ReceiverCameraControls cameraControls;
        string status = "Start recording on the sender, then connect.";
        int meshDelayMs;
        bool scrubbing;
        float scrubTime;
        bool bufferSettingsOpen;
        int vertexPrefetchChunks = 3;
        int webAudioPrefetchChunks = 3;
        float webAudioBackSeconds = 30;

        void Update()
        {
            if (scrubbing && !Input.GetMouseButton(0) && activeReceiver != null)
            {
                activeReceiver.Seek(scrubTime);
                scrubbing = false;
            }
        }

        void Start()
        {
            vertexPrefetchChunks = receiverTemplate.VertexPrefetchChunks;
            webAudioPrefetchChunks = receiverTemplate.WebAudioPrefetchChunks;
            webAudioBackSeconds = (float)receiverTemplate.WebAudioBackBufferSeconds;
            cameraControls = GetComponent<ReceiverCameraControls>() ?? gameObject.AddComponent<ReceiverCameraControls>();
#if UNITY_IOS || UNITY_ANDROID
            Application.targetFrameRate = 60;
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
            var pageUrl = Application.absoluteURL;
            int queryStart = pageUrl.IndexOf('?');
            if (queryStart >= 0)
            {
                var query = pageUrl.Substring(queryStart + 1).Split('&');
                foreach (var pair in query)
                {
                    var parts = pair.Split(new[] { '=' }, 2);
                    if (parts.Length != 2 || !string.Equals(parts[0], "channel", StringComparison.OrdinalIgnoreCase))
                        continue;
                    channelAddress = Uri.UnescapeDataString(parts[1]);
                    connectOnStart = true;
                    break;
                }
            }
#endif
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-streamingMeshChannel");
            if (index >= 0 && index + 1 < args.Length)
            {
                channelAddress = args[index + 1];
                connectOnStart = true;
            }
            if (connectOnStart) Connect();
        }

        public void Connect()
        {
            try
            {
                if (activeReceiver != null)
                {
                    activeReceiver.Reconnect(channelAddress.Trim(), autoPlayAfterBuffering);
                    activeReceiver.MeshPresentationDelaySeconds = meshDelayMs / 1000.0;
                    scrubbing = false;
                    status = "Reconnecting / checking model resources.";
                    return;
                }
                activeReceiver = Instantiate(receiverTemplate);
                activeReceiver.name = "KAGURA Live Receiver";
                activeReceiver.ConfigureChannel(channelAddress.Trim());
                activeReceiver.ConfigurePlayback(autoPlayAfterBuffering);
                activeReceiver.ConfigureBuffering(vertexPrefetchChunks, webAudioPrefetchChunks, webAudioBackSeconds);
                activeReceiver.MeshPresentationDelaySeconds = meshDelayMs / 1000.0;
                activeReceiver.gameObject.SetActive(true);
                scrubbing = false;
                Debug.Log("KAGURA receiver connecting to " + channelAddress);
                status = "Connecting / receiving. Reconnect after starting a new recording.";
            }
            catch (Exception exception)
            {
                if (activeReceiver != null) Destroy(activeReceiver.gameObject);
                activeReceiver = null;
                status = exception.Message;
            }
        }

        void OnGUI()
        {
            float scale = Mathf.Max(1, Screen.height / 540f);
            var previous = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            Rect safe = Screen.safeArea;
            GUILayout.BeginArea(new Rect(safe.x / scale + 12, (Screen.height - safe.yMax) / scale + 12,
                Mathf.Min(450, safe.width / scale - 24),
                Mathf.Min(cameraControls != null && cameraControls.IsOpen ? 340 : 370, safe.height / scale - 24)), GUI.skin.box);
            GUILayout.Label("StreamingMesh KAGURA Receiver");
            if (GUILayout.Button(bufferSettingsOpen ? "Close buffer settings" : "Buffer settings", GUILayout.Height(26)))
                bufferSettingsOpen = !bufferSettingsOpen;
            if (bufferSettingsOpen)
            {
                GUILayout.Label($"Vertex buffer: {vertexPrefetchChunks} files (including current)");
                vertexPrefetchChunks = Mathf.RoundToInt(GUILayout.HorizontalSlider(vertexPrefetchChunks, 1, 16));
                GUILayout.Label($"Web audio buffer: {webAudioPrefetchChunks} files (including current)");
                webAudioPrefetchChunks = Mathf.RoundToInt(GUILayout.HorizontalSlider(webAudioPrefetchChunks, 1, 16));
                GUILayout.Label($"Web audio history: {webAudioBackSeconds:F1} s");
                webAudioBackSeconds = GUILayout.HorizontalSlider(webAudioBackSeconds, 0, 120);
                GUILayout.Label("Audio history is additional to the file window.");
                GUILayout.Label("Native audio uses its player's buffer settings.");
                if (GUILayout.Button("Apply / rebuffer", GUILayout.Height(32)))
                {
                    receiverTemplate.ConfigureBuffering(vertexPrefetchChunks, webAudioPrefetchChunks, webAudioBackSeconds);
                    if (activeReceiver != null) activeReceiver.ConfigureBuffering(vertexPrefetchChunks, webAudioPrefetchChunks, webAudioBackSeconds);
                    scrubbing = false;
                }
                GUILayout.EndArea(); GUI.matrix = previous; return;
            }
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
            GUI.enabled = activeReceiver == null || !activeReceiver.IsInitializing;
            if (GUILayout.Button("Connect / Reconnect", GUILayout.Height(32))) Connect();
            GUI.enabled = true;
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
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Mesh delay: {meshDelayMs:+#;-#;0} ms", GUILayout.Width(180));
            if (GUILayout.Button("-50 ms")) SetMeshDelay(meshDelayMs - 50);
            if (GUILayout.Button("Reset")) SetMeshDelay(0);
            if (GUILayout.Button("+50 ms")) SetMeshDelay(meshDelayMs + 50);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUI.matrix = previous;
        }

        static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            int minutes = (int)(seconds / 60);
            return $"{minutes:00}:{seconds - minutes * 60:00.0}";
        }

        void SetMeshDelay(int milliseconds)
        {
            int next = Mathf.Clamp(milliseconds, -500, 500);
            if (next == meshDelayMs) return;
            meshDelayMs = next;
            if (activeReceiver != null) Connect();
        }
    }
}
