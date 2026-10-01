Shader "Conditional/DeclinedInMerged"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma shader_feature_local _OUTER
            #pragma shader_feature_local _INNER

            // 外側は並べられる。内側はマクロを定義するので並べられない。
            // 既定の構成では _INNER が無いので内側を読み飛ばし、
            // _INNER だけの構成では _OUTER が無いので外側ごと読み飛ばす。
            half4 shade()
            {
            #ifdef _OUTER
                half4 c = 1;
            #ifdef _INNER
            #define USE_INNER 1
                c *= missingInner;
            #endif
                return c;
            #else
                return 0;
            #endif
            }

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return shade(); }
            ENDHLSL
        }
    }
}
