//MeshSenderEditor.cs
//
//Copyright (c) 2016 Tatsuro Matsubara
//Released under the MIT license
//http://opensource.org/licenses/mit-license.php
//

using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

using StreamingMesh;

[CustomEditor(typeof(STMHttpSender))]
public class STMHttpSenderEditor : Editor {
#if !UNITY_WEBGL
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
	}
#endif
}
