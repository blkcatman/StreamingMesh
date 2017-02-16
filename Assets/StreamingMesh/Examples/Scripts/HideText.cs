using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HideText : MonoBehaviour {

	// Use this for initialization
	void Start () {
		Invoke("Hide", 0.1f);
	}
	
	// Update is called once per frame
	void Update () {
		
	}

	void Hide() {
		gameObject.SetActive(false);
	}
}
