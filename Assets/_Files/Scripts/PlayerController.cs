using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerController : MonoBehaviour {

	public StreamingMesh.STMHttpMeshReceiver receiver;
	public GameObject loadingUI;
	public GameObject replayUI;

	bool loading = true;
	bool reachEnd = false;

	// Use this for initialization
	void Start () {
		loadingUI.SetActive(true);
		replayUI.SetActive(false);
	}
	
	// Update is called once per frame
	void Update () {
		if(loading) {
			if(receiver.IsPlayable) {
				loadingUI.SetActive(false);
				loading = false;
			}
		}

		if(receiver != null) {
			if(receiver.IsPlayable && receiver.IsPlayEnd) {
				if(!reachEnd) {
					replayUI.SetActive(true);
					reachEnd = true;
				}
			}
			if(Input.GetKeyDown(KeyCode.Return)) {
					replayUI.SetActive(false);
					receiver.SeekToZero();
			}
		}





	}
}
