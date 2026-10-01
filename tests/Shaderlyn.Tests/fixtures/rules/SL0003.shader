Shader "Rules/SL0003"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local _K1
            #pragma shader_feature_local _K2
            #pragma shader_feature_local _K3
            #pragma shader_feature_local _K4
            #pragma shader_feature_local _K5
            #pragma shader_feature_local _K6
            #pragma shader_feature_local _K7
            #pragma shader_feature_local _K8
            #pragma shader_feature_local _K9
            #pragma shader_feature_local _K10
            #pragma shader_feature_local _K11
            #pragma shader_feature_local _K12
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #ifdef _K1
            #define V1 1
            #else
            #define V1 0
            #endif
            #ifdef _K2
            #define V2 1
            #else
            #define V2 0
            #endif
            #ifdef _K3
            #define V3 1
            #else
            #define V3 0
            #endif
            #ifdef _K4
            #define V4 1
            #else
            #define V4 0
            #endif
            #ifdef _K5
            #define V5 1
            #else
            #define V5 0
            #endif
            #ifdef _K6
            #define V6 1
            #else
            #define V6 0
            #endif
            #ifdef _K7
            #define V7 1
            #else
            #define V7 0
            #endif
            #ifdef _K8
            #define V8 1
            #else
            #define V8 0
            #endif
            #ifdef _K9
            #define V9 1
            #else
            #define V9 0
            #endif
            #ifdef _K10
            #define V10 1
            #else
            #define V10 0
            #endif
            #ifdef _K11
            #define V11 1
            #else
            #define V11 0
            #endif
            #ifdef _K12
            #define V12 1
            #else
            #define V12 0
            #endif
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return V1 + V2 + V3 + V4 + V5 + V6 + V7 + V8 + V9 + V10 + V11 + V12; }
            ENDHLSL
        }
    }
}
