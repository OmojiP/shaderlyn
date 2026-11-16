// 2 つの Pass の、1 つ目にだけ構文の誤りがある。2 つ目の Pass は検査する。1 つ目の Pass のほかの関数も検査する。
// 期待: 17 CF0001
// 期待: 22 CF0310
// 期待: 37 CF0310
Shader "Tests/TwoPasses"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float Broken(float v)
            {
                return v *;
            }

            float4 vert(float4 position : POSITION) : SV_Position
            {
                return position * missingInFirstPass;
            }

            float4 frag() : SV_Target { return 1; }
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 vert(float4 position : POSITION) : SV_Position { return position; }

            float4 frag() : SV_Target { return missingInSecondPass; }
            ENDHLSL
        }
    }
}
