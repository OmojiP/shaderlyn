Shader "Conditional/LocalRedeclaration"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _A _B
            #pragma multi_compile _C _D

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            float ReturnFloat()
            {
                #if defined(_A)
                float a = 1.0;
                #elif defined(_B)
                float3 a = float3(1.0, 2.0, 3.0);
                #endif

                // _A と _D、_B と _D は別の行の宣言なので同時に有効になる。
                // その構成では同じ波括弧の直下に 'a' が 2 つある。
                #if defined(_C)
                a = 2;
                #elif defined(_D)
                float4 a = float4(1, 1, 1, 1);
                #endif
                return a.x;
            }

            half4 frag() : SV_Target { return ReturnFloat(); }
            ENDHLSL
        }
    }
}
