Shader "Rules/HL0003"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            interface ILight
            {
                half3 Shade(half3 normal);
            };

            class PointLight : ILight
            {
                half3 color;
                half3 Shade(half3 normal) { return color * normal; }
            };

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
