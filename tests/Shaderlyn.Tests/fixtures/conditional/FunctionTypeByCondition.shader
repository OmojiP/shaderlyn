Shader "Conditional/FunctionTypeByCondition"
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

            // 同じ名前の関数を、条件ごとに違う型で返す。
            #ifdef _A
            float3 conditionalValue() { return 1; }
            #else
            float4 conditionalValue() { return 1; }
            #endif

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            half4 frag() : SV_Target
            {
                // _A のときは float3 になり、w が無い。
                return conditionalValue().w;
            }
            ENDHLSL
        }
    }
}
