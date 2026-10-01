Shader "Conditional/ConditionalDeclarations"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile_local _ SHADER_DEBUG

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            #ifdef SHADER_DEBUG
                float4 debugColor : COLOR;
            #endif
            };

            // 宣言だけを先に書き、定義は SHADER_DEBUG のときだけ置く。
            half4 sampleDebug(float2 uv);

            #ifdef SHADER_DEBUG
            half4 sampleDebug(float2 uv) { return 1; }
            half4 tint(float2 uv) { return 1; }
            #else
            half4 tint(float3 uvw) { return 0; }
            #endif

            Varyings vert(float4 p : POSITION)
            {
                Varyings o = (Varyings)0;
                o.positionCS = p;
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
            #ifdef SHADER_DEBUG
                return IN.debugColor;
            #else
                return IN.debugColor + sampleDebug(IN.uv) + tint(IN.uv);
            #endif
            }
            ENDHLSL
        }
    }
}
