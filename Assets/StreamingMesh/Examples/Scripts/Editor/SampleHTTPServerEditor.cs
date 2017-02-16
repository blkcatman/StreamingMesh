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

[CustomEditor(typeof(SampleHTTPServer))]
public class SampleHTTPServerEditor : Editor {
#if !UNITY_WEBGL
	public override void OnInspectorGUI() {
		DrawDefaultInspector();
		serializedObject.Update();
		DrawProperties();
		serializedObject.ApplyModifiedProperties();
	}

	void DrawProperties() {
		SampleHTTPServer obj = target as SampleHTTPServer;

        if(GUILayout.Button("ResetServer")) {
			obj.ResetChannels();
        }

	}
#endif
}
