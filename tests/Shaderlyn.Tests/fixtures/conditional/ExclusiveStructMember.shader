Shader "Conditional/ExclusiveStructMember"
{
    HLSLINCLUDE
        #pragma multi_compile_fragment _ _HAS_MOTION _DEPTH_ONLY

        // メンバーも使用箇所も同じ条件で守られている。どの構成でも誤りではない。
        struct FragOut
        {
        #ifndef _DEPTH_ONLY
            float4 depthValues : SV_Target0;
        #endif
            float actualDepth : SV_Depth;
        };

        float4 Vert(float4 p : POSITION) : SV_POSITION { return p; }

        FragOut Frag()
        {
            FragOut fragO;

            // 文の途中で切れているので _HAS_MOTION は並べられず、構成ごとの展開に回る。
            // その構成では _DEPTH_ONLY は決して有効にならないため、
            // 下の #ifndef の中は条件が付かずに現れる。
            // それを「どの構成でも」と読むと、メンバーの条件と食い違う。
            float m
        #ifdef _HAS_MOTION
                = 1
        #else
                = 2
        #endif
                ;

            // こちらは文の途中で切れているので並べられない。
            // メンバーの宣言は並べられるため、条件の出どころが宣言と使用箇所で食い違う。
        #ifndef _DEPTH_ONLY
            fragO.depthValues = m
        #endif
                ;
            fragO.actualDepth = 0;
            return fragO;
        }
    ENDHLSL

    SubShader
    {
        Pass
        {
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment Frag
            ENDHLSL
        }
    }
}
