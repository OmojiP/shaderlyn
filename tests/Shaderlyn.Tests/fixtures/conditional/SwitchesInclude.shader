Shader "Conditional/SwitchesInclude"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma shader_feature_local _HELPERS

            // 取り込みを切り替える領域は 1 本の木に並べられない。
            #ifdef _HELPERS
            #include "ConditionalHelpers.hlsl"
            #endif

            half4 useHelper()
            {
            #ifdef _HELPERS
                return helperColor() + missingWithHelpers;
            #else
                return helperColor() + missingWithoutHelpers;
            #endif
            }

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return useHelper(); }
            ENDHLSL
        }
    }
}
