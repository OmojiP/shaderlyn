Shader "Rules/CF0316"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            half Blend(half a, half b = 1.0, half c) { return a * b + c; }
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return Blend(1, 1, 1); }
            ENDHLSL
        }
    }
}
