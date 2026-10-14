Shader "Rules/HL0348"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Counter { float value; };
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target
            {
                Counter c = (Counter)0;
                c++;
                return half4(c.value, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
