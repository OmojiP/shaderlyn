Shader "Rules/HL0004"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _TINT_RED _TINT_BLUE

            #ifdef _TINT_RED || _TINT_BLUE
            static const half Tint = 1;
            #else
            static const half Tint = 0;
            #endif

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return Tint; }
            ENDHLSL
        }
    }
}
