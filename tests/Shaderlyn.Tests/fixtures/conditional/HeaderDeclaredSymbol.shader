Shader "Conditional/HeaderDeclaredSymbol"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // 宣言はこのファイルに 1 つも無く、取り込むヘッダ側にある。
            // ヘッダの #pragma を拾わなければ、_HEADER_QUALITY の分岐は既定の側しか読まれない。
            #include "HeaderDeclaredSymbols.hlsl"

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            half4 frag() : SV_Target
            {
                // _HEADER_QUALITY の構成では Shade が float2 を返すので、float3 には渡せない。
                float3 c = Shade(float3(0, 0, 1));
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
