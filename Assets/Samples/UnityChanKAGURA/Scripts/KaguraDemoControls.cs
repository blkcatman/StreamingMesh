using UnityEngine;
using UnityEngine.Playables;

namespace StreamingMesh.Samples
{
    /// <summary>Playback and Editor capture controls for the local KAGURA sample.</summary>
    public sealed class KaguraDemoControls : MonoBehaviour
    {
        public PlayableDirector director;
        public STMHttpSender sender;

        public void Restart()
        {
            if (director == null || (sender != null && sender.IsStartRecord)) return;
            director.Stop();
            director.time = 0;
            director.Play();
        }

        void OnGUI()
        {
            GUI.Label(new Rect(16, Screen.height - 32, 400, 28), "© Unity Technologies Japan/UCL");
            if (director == null) return;
            GUILayout.BeginArea(new Rect(16, 16, 310, 230), GUI.skin.box);
            GUILayout.Label("UnityChan KAGURA / Unite in the sky");
            GUILayout.Label($"{director.time:F1} / {director.duration:F1} s");
            bool recording = sender != null && sender.IsStartRecord;
            GUI.enabled = !recording;
            if (GUILayout.Button(director.state == PlayState.Playing ? "Pause" : "Play"))
            {
                if (director.state == PlayState.Playing) director.Pause();
                else director.Play();
            }
            if (GUILayout.Button("Restart")) Restart();
#if UNITY_EDITOR
            if (sender != null)
            {
                if (GUILayout.Button("Create Channel")) sender.CreateChannel();
                if (GUILayout.Button("Record from start (after channel is ready)"))
                {
                    Restart();
                    sender.Record();
                }
                GUI.enabled = recording;
                if (GUILayout.Button("Stop Recording")) sender.Stop();
            }
#endif
            GUI.enabled = true;
            GUILayout.EndArea();
        }

        void OnDisable()
        {
            if (sender != null && sender.IsStartRecord) sender.Stop();
        }
    }
}
