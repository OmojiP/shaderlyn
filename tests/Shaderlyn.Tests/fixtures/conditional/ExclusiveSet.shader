Shader "Conditional/ExclusiveSet"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile _ _X _Y

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            // _X と _Y は同じ行なので、同時には有効にならない。どの構成にも誤りは無い。
            #ifdef _X
            float4 _Tint;
            #endif
            #ifdef _Y
            float4 _Tint;
            #endif

            #if defined(_X) || defined(_Y)
            half4 tint() { return _Tint; }
            #endif

            half4 frag() : SV_Target
            {
            #ifdef _X
            #define USE_X 1
            #ifdef _Y
                return neverBoth;
            #endif
            #endif
                return 0;
            }
            ENDHLSL
        }
    }
}
