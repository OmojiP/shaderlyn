Shader "Rules/HL0355"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma use_dxc
            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
            };
            v2f vert(float4 p : POSITION) { v2f o; o.pos = p; o.color = p; return o; }
            float4 frag(v2f i) : SV_Target { return GetAttributeAtVertex(i.color, 0); }
            ENDHLSL
        }
    }
}
