Shader "Rules/SL1052"
{
    Properties
    {
        _BaseColor("Base Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma require native16bit
            cbuffer UnityPerMaterial
            {
                half4 _BaseColor;
            };
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return _BaseColor; }
            ENDHLSL
        }
    }
}
