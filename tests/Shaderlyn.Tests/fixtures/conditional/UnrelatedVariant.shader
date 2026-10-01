Shader "Conditional/UnrelatedVariant"
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

            // _A の宣言は並べられ、使う側は並べられない (#define を含む)。_B は別に構成として展開される。
            // _B のバリアントは _A の分岐も並べて展開しないと、宣言の条件に無関係な !_B が付く。
            #ifdef _A
            float4 _Tint;
            #endif

            #ifdef _B
            #define USE_B 1
            #endif

            #ifdef _A
            #define USE_A 1
            half4 tint() { return _Tint + missingInA; }
            #endif

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
