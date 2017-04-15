using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class AutoRecorder : MonoBehaviour {

	public StreamingMesh.STMHttpSender sender;
	public AudioSource audioSource;

	// Use this for initialization
	void Start () {
		Invoke("Record", 1.0f);
	}
	void Record() {
		if(sender != null && audioSource) {
#if UNITY_EDITOR
			sender.Record();
			audioSource.Play();
#endif
		}		
	}
	
	// Update is called once per frame
	void Update () {
		
	}
}
