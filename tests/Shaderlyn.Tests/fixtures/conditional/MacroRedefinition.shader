Shader "Conditional/MacroRedefinition"
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

            // _A と _B が同時に有効なときだけ、TINT を中身を変えて定義し直すことになる。
            #ifdef _A
            #define TINT half4(1, 0, 0, 1)
            #endif
            #ifdef _B
            #define TINT half4(0, 0, 1, 1)
            #endif
            #ifndef TINT
            #define TINT half4(1, 1, 1, 1)
            #endif

            half4 frag() : SV_Target { return TINT; }
            ENDHLSL
        }
    }
}
