Shader "Conditional/Nested"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _DETAIL

            half4 shade(float2 uv)
            {
            #ifdef _NORMALMAP
                half4 c = 1;
            #ifdef _DETAIL
                c *= missingDetail;
            #else
                c *= missingNormalOnly;
            #endif
                return c;
            #else
                return missingNeither;
            #endif
            }

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return shade(0); }
            ENDHLSL
        }
    }
}
