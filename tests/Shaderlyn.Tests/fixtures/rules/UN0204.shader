Shader "Rules/UN0204"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma never_use_dxc
            #pragma require int64 : _USE_INT64
            #pragma require wavebasic
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return 1; }
            ENDHLSL
        }
    }
}
