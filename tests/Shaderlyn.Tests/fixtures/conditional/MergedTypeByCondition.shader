Shader "Conditional/MergedTypeByCondition"
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

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            // TypeByCondition と同じ誤りを、両方の分岐を並べられる形で書いたもの。
            half4 frag() : SV_Target
            {
            #ifdef _A
                float3 color = 1;
            #else
                float4 color = 1;
            #endif
            #ifdef _B
                color.w = 0.5;
            #endif
                return color.x;
            }
            ENDHLSL
        }
    }
}
