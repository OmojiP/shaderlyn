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

            // FXC (Unity の既定のコンパイラ) は template と演算子の多重定義を拒む。class / interface は使える。
            template<typename T> T Twice(T x) { return x + x; }

            struct Light
            {
                half3 color;
                Light operator+(Light other) { Light r; r.color = color + other.color; return r; }
            };

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
