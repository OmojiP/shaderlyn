Shader "Conditional/SwitchesMacros"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile_local _ _TRACE

            // マクロを切り替える領域は 1 本の木に並べられない。
            #ifdef _TRACE
            #define TRACE_VALUE(x) (x + missingTraced)
            #else
            #define TRACE_VALUE(x) (x)
            #endif

            half4 traced(half4 c)
            {
            #ifdef _TRACE
                c += missingInTraceBranch;
            #endif
                return TRACE_VALUE(c);
            }

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return traced(0); }
            ENDHLSL
        }
    }
}
