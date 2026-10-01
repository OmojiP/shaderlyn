Shader "Conditional/WidenTypeByCondition"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile _A _B

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            // 同じ名前を条件ごとに違う型で宣言している。
            // _B の構成でだけ float2 を float3 へ渡すことになり、コンパイルに失敗する。
            // 構成を固定しても同じ誤りが出るので、並べる経路と並べない経路で一致しなければならない。
            float3 Widen()
            {
            #if defined(_A)
                float3 a = float3(1, 2, 3);
            #elif defined(_B)
                float2 a = float2(1, 2);
            #endif
                return a;
            }

            half4 frag() : SV_Target { return half4(Widen(), 1); }
            ENDHLSL
        }
    }
}
