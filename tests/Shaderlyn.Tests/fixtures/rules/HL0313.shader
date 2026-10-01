Shader "Rules/HL0313"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ SHADER_DEBUG
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct V { float2 uv : TEXCOORD0;
            #ifdef SHADER_DEBUG
                float4 dbg : COLOR;
            #endif
            };
            half4 use(V i) { return i.dbg; }
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
