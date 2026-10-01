Shader "Rules/HL0353"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _A _B
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float Varying()
            {
                #if defined(_A)
                float a = 1.0;
                #elif defined(_B)
                float3 a = float3(1.0, 2.0, 3.0);
                #endif
                return a;
            }
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return Varying(); }
            ENDHLSL
        }
    }
}
