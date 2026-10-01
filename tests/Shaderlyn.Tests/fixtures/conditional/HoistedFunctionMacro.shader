Shader "Conditional/HoistedFunctionMacro"
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

            // 条件によって中身が変わる関数形式マクロ。引数ごと複製する。
            #ifdef _A
            #define PACK(x) float2(x, 0)
            #else
            #define PACK(x) float4(x, 0, 0, 0)
            #endif

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            half4 frag() : SV_Target
            {
                // _A のときは float2 になり、z が無い。
                return PACK(1).z;
            }
            ENDHLSL
        }
    }
}
