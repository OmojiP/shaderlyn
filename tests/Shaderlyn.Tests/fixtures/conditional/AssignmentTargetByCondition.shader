Shader "Conditional/AssignmentTargetByCondition"
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

            float Assign()
            {
                // a の型は _A のとき float、_B のとき float3。代入先の型が構成で変わる。
                #if defined(_A)
                float a = 1.0;
                #elif defined(_B)
                float3 a = float3(1.0, 2.0, 3.0);
                #endif

                // _B と _D の構成では float2 を float3 へ代入しており、渡せない。
                #if defined(_C)
                a = 2;
                #elif defined(_D)
                a = float2(1, 1);
                #endif
                return a.x;
            }

            half4 frag() : SV_Target { return Assign(); }
            ENDHLSL
        }
    }
}
