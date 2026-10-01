Shader "Conditional/ConditionalDeclarationUse"
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

            // _Tint は _A のときしか宣言されないが、無条件で使っている。
            // _A が無い構成では未宣言の名前になる。
            #ifdef _A
            float4 _Tint;
            #endif

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return _Tint; }
            ENDHLSL
        }
    }
}
