Shader "Rules/CF0406"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Light { float3 color; float intensity; };
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target
            {
                Light a = (Light)0;
                Light b = (Light)0;
                Light sum = a + b;
                return half4(sum.color, 1);
            }
            ENDHLSL
        }
    }
}
