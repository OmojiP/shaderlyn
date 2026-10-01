Shader "Conditional/RequiredSet"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile MODE_A MODE_B
            #pragma multi_compile _ _X

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            // MODE_A か MODE_B のどちらかが必ず有効。#else の中はコンパイルされない。
            half4 frag() : SV_Target
            {
            #if defined(MODE_A)
                return 1;
            #elif defined(MODE_B)
            #ifdef _X
                return missingInBAndX;
            #endif
                return 0;
            #else
                return neverCompiled;
            #endif
            }
            ENDHLSL
        }
    }
}
