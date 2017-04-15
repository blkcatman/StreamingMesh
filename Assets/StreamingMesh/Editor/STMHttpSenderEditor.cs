using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

using StreamingMesh;

[CustomEditor(typeof(STMHttpSender))]
public class STMHttpSenderEditor : Editor {
#if UNITY_EDITOR
	public override void OnInspectorGUI() {
		DrawDefaultInspector();
		serializedObject.Update();
		DrawProperties();
		serializedObject.ApplyModifiedProperties();
	}

	void DrawProperties() {
		STMHttpSender obj = target as STMHttpSender;

        if(GUILayout.Button("Create Channel")) {
            if (obj.targetGameObject != null) {
                Debug.Log("Channel creating...");
                obj.CreateChannel();
            }
        }

        GUILayout.Space(15);

        if (GUILayout.Button("Start Recording")) {
            if (obj.targetGameObject != null) {
                Debug.Log("Start Recording...");
                obj.Record();
            }
        }
    }
#endif
}
