using System;
using UnityEngine;

namespace StreamingMesh.Samples
{
    public sealed class KaguraReceiverControls : MonoBehaviour
    {
        public Receiver receiverTemplate;
        public string channelAddress = "http://127.0.0.1:8000/channels/channel_KAGURA/";
        public bool connectOnStart;
        Receiver activeReceiver;
        string status = "Start recording on the sender, then connect.";

        void Start()
        {
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
            if (activeReceiver != null)
            {
                Destroy(activeReceiver.gameObject);
                activeReceiver = null;
            }
            try
            {
                activeReceiver = Instantiate(receiverTemplate);
                activeReceiver.name = "KAGURA Live Receiver";
                activeReceiver.ConfigureChannel(channelAddress.Trim());
                activeReceiver.gameObject.SetActive(true);
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
            GUILayout.BeginArea(new Rect(12, 12, Mathf.Min(650, Screen.width / scale - 24), 140), GUI.skin.box);
            GUILayout.Label("StreamingMesh KAGURA Receiver");
            channelAddress = GUILayout.TextField(channelAddress);
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
            GUILayout.Label(activeReceiver != null ? activeReceiver.ConnectionStatus : status);
            GUILayout.EndArea();
            GUI.matrix = previous;
        }
    }
}
