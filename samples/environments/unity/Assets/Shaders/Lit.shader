// 動作確認用のシェーダー。意図的に誤りを入れてある (samples/environments/README.md)。
// Unity のヘッダを取り込まないので、Unity が無くても解析できる。
Shader "Samples/Lit"
{
    Properties
    {
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _SHADOWS_SOFT

            float4x4 unity_MatrixVP;
            float4x4 unity_ObjectToWorld;

            // 誤り 1 (SL1003): Color のプロパティを float で受けている
            float _BaseColor;

            float4 Vert(float3 positionOS : POSITION) : SV_POSITION
            {
                return mul(unity_MatrixVP, mul(unity_ObjectToWorld, float4(positionOS, 1)));
            }

            float4 Frag() : SV_Target
            {
                float4 color = _BaseColor;
            // 誤り 2 (HL0330): シンボルの綴りが違う。この分岐は決して有効にならない
            #ifdef _SHADOWS_SOFTT
                color *= 0.5;
            #endif
            #ifdef _SHADOWS_SOFT
                float3 shadow = 0.5;
                // 誤り 3 (HL0312): _SHADOWS_SOFT のとき float3 に .w は無い
                color.a = shadow.w;
            #endif
                return color;
            }
            ENDHLSL
        }
    }
}
