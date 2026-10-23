Shader "Rules/HL0008"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #define TILE_SIZE 8
            #define TILE_SIZE 16

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return TILE_SIZE; }
            ENDHLSL
        }
    }
}
