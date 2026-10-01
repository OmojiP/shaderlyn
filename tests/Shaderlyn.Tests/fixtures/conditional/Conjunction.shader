Shader "Conditional/Conjunction"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma shader_feature_local _FOG
            #pragma shader_feature_local _SHADOWS

            // 両方が有効なときだけマクロを定義する。並べられないので、組のまま 1 つの構成として展開される。
            #if defined(_FOG) && defined(_SHADOWS)
            #define FOG_AND_SHADOWS 1
            half4 both() { return missingInConjunction; }
            #endif

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
