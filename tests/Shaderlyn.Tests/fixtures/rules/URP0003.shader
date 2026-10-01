Shader "Rules/URP0003"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            sampler2D _Legacy;
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag(float2 uv : TEXCOORD0) : SV_Target { return tex2D(_Legacy, uv); }
            ENDHLSL
        }
    }
}
