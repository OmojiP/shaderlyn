Shader "Rules/HL0332"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile _ _DETAIL
            // Unity は有効なシンボルを 1 として定義する。2 と比べても通らない。
            #if _DETAIL == 2
            float _DetailScale;
            #endif
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
