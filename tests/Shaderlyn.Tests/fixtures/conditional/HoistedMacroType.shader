Shader "Conditional/HoistedMacroType"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile _ _A
            #pragma multi_compile _ _B

            // 条件によって中身が変わるマクロ。使っている文を定義ごとに複製する。
            #ifdef _A
            #define CTYPE float3
            #else
            #define CTYPE float4
            #endif

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            half4 frag() : SV_Target
            {
                CTYPE color = 1;
            #ifdef _B
                // _A と _B が同時に有効な構成でだけ、float3 に w がある。
                color.w = 0.5;
            #endif
                return color.x;
            }
            ENDHLSL
        }
    }
}
