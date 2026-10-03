Shader "Hidden/StreamingMesh/ExportNormal"
{
    Properties { _MainTex ("Normal", 2D) = "bump" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 frag(v2f_img input) : SV_Target
            {
                // Export decoded XYZ rather than platform-specific AG/RG packing.
                return float4(UnpackNormal(tex2D(_MainTex, input.uv)) * 0.5 + 0.5, 1);
            }
            ENDCG
        }
    }
}
