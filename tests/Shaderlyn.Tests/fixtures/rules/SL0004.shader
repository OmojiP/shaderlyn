Shader "Rules/SL0004"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile _ _MODE_A _MODE_B _MODE_C _MODE_D

            // 定義が 5 通りある。複製すると同じ文が 5 本並ぶので、巻き上げの対象外である。
            // さらに、if の本体そのものが構成によって文にもブロックにもなる。
            // if 文自身のトークンは変わらないため、選択肢として分ける単位も見つからない。
            #ifdef _MODE_A
            #define BODY { v = 1; }
            #elif defined(_MODE_B)
            #define BODY v = 2;
            #elif defined(_MODE_C)
            #define BODY v = 3;
            #elif defined(_MODE_D)
            #define BODY v = 4;
            #else
            #define BODY v = 5;
            #endif

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            half4 frag() : SV_Target
            {
                half v = 0;
                if (v > 0)
                    BODY
                return v;
            }
            ENDHLSL
        }
    }
}
