Shader "Rules/HL0304"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment Main_PS
            #pragma use_dxc
            cbuffer UnityPerMaterial
            {
                float4 _Color;
                float4 Main_PS() : SV_Target { return _Color; }
            };
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            ENDHLSL
        }
    }
}
