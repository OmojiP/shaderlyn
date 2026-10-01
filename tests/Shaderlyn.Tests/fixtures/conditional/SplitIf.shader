Shader "Conditional/SplitIf"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile_local _ _FLIP

            // if の条件と本体が #endif をまたいで分かれている。並べられない形である。
            half4 flip(float2 uv)
            {
                half4 c = 0;
            #ifdef _FLIP
                if (uv.y < missingThreshold)
            #else
                uv.y = 1 - uv.y;
                if (uv.y < 0.5)
            #endif
                {
                    c = missingInBody;
                }
                return c;
            }

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return flip(0); }
            ENDHLSL
        }
    }
}
