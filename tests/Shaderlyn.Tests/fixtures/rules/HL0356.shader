Shader "Rules/HL0356"
{
    Properties
    {
        _HeightMap ("Height", 2D) = "black" {}
    }
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            sampler2D _HeightMap;
            float4 vert(float4 pos : POSITION, float2 uv : TEXCOORD0) : SV_POSITION
            {
                pos.y += tex2D(_HeightMap, uv).r;
                return pos;
            }
            float4 frag() : SV_Target { return 1; }
            ENDCG
        }
    }
}
