Shader "Conditional/ElifChain"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile_local _ _MODE_A _MODE_B

            half4 pick()
            {
            #ifdef _MODE_A
                return missingA;
            #elif defined(_MODE_B)
                float2 v = 0;
                return v.z;
            #else
                return missingDefault;
            #endif
            }

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return pick(); }
            ENDHLSL
        }
    }
}
