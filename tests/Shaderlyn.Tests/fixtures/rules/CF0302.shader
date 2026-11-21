Shader "Rules/CF0302"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct v2f
            {
                float4 pos : SV_POSITION;
                float depth : SV_Depth;
            };
            v2f vert(float4 p : POSITION) { v2f o; o.pos = p; o.depth = 0; return o; }
            half4 frag(v2f i) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
