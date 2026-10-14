Shader "Rules/HL0354"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target
            {
                float s = 0;
                for (int i = 0; i < 4; i++) { s += i; }
                return s + i;
            }
            ENDHLSL
        }
    }
}
