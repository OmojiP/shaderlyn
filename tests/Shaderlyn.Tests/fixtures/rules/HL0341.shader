Shader "Rules/HL0341"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            half4 f(float3 v) { return v.x; }
            half4 use() { return f(float3(0,0,0), 1); }
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
