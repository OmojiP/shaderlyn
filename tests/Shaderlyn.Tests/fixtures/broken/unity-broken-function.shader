// Unity の HLSLPROGRAM の関数の本体の中の構文の誤り。ほかの関数は検査する。
// 期待: 22 CF0001
// 期待: 26 CF0310
Shader "Tests/Broken"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 vert(float4 position : POSITION) : SV_Position
            {
                return position;
            }

            float Broken(float v)
            {
                return v *
            }

            float4 frag() : SV_Target
            {
                return float4(1, 1, 1, 1) * undefinedTint;
            }
            ENDHLSL
        }
    }
}
