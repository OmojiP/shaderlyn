Shader "Conditional/NestedInDeclined"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma shader_feature_local _BASE
            #pragma shader_feature_local _DETAIL
            #pragma shader_feature_local _EXTRA

            // 外側はマクロを定義するので並べられない。既定の構成では丸ごと読み飛ばす。
            // 内側の条件も読み、外側と掛け合わせた組を構成として作る。
            #ifdef _BASE
            #define USE_BASE 1
            half4 withBase()
            {
                half4 c = missingBaseOnly;
            #ifdef _DETAIL
                c *= missingBaseAndDetail;
            #ifdef _EXTRA
                c *= missingAllThree;
            #endif
            #endif
                return c;
            }
            #else
            half4 withoutBase()
            {
            #ifdef _DETAIL
                return missingDetailWithoutBase;
            #endif
                return 0;
            }
            #endif

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
