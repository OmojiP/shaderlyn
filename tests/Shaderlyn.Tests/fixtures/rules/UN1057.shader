﻿Shader "Rules/UN1057"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return 1; }
            ENDHLSL
        }
    }
}
