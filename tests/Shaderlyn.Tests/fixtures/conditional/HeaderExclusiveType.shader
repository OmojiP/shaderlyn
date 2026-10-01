Shader "Conditional/HeaderExclusiveType"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "HeaderExclusiveTypes.hlsl"

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            half4 frag() : SV_Target
            {
                PRECISE_VECTOR v = 0;

                // _HIGH_PRECISION では 3 + 1 で足りるが、_LOW_PRECISION では 2 + 1 で足りない。
                return half4(v, 1);
            }
            ENDHLSL
        }
    }
}
