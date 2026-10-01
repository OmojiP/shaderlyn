Shader "Rules/HL0211"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            Texture2D _A : register(t0);
            Texture2D _B : register(t0);
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return _A.Load(int3(0, 0, 0)) + _B.Load(int3(0, 0, 0)); }
            ENDHLSL
        }
    }
}
