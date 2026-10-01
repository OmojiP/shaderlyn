Shader "Conditional/TypeByCondition"
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

            // _A と _B が同時に有効なときだけ、float3 に .w を書くことになる。
            // どちらか一方だけの構成では正しい。
            half4 frag() : SV_Target
            {
            #ifdef _A
            #define USE_A 1
                float3 color = 1;
            #else
                float4 color = 1;
            #endif
            #ifdef _B
            #define USE_B 1
                color.w = 0.5;
            #endif
                return color.x;
            }
            ENDHLSL
        }
    }
}
