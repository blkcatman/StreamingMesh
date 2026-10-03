Shader "Hidden/StreamingMesh/MaterialFixture"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _Vector ("Vector", Vector) = (0,0,0,0)
        _Float ("Float", Float) = 0
        _Integer ("Integer", Integer) = 0
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader { Pass { } }
}
