//SampleHTTPServerEditor.cs
//
//Copyright (c) 2017 Tatsuro Matsubara.
//Creative Commons License
//This file is licensed under a Creative Commons Attribution-ShareAlike 4.0 International License.
//https://creativecommons.org/licenses/by-sa/4.0/
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
