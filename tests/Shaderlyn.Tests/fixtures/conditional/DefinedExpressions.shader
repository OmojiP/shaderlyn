Shader "Conditional/DefinedExpressions"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma shader_feature_local _A
            #pragma shader_feature_local _B
            #pragma shader_feature_local _C

            half4 combine()
            {
                half4 c = 0;
            #if defined(_A) || defined(_B)
                c += missingEither;
            #endif
            #if defined(_A) && !defined(_B)
                c += missingOnlyA;
            #endif
            #if _C
                c += missingBareC;
            #endif
                return c;
            }

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return combine(); }
            ENDHLSL
        }
    }
}
