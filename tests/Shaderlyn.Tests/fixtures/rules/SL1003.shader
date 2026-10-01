Shader "Rules/SL1003"
{
    Properties { _Amount ("Amount", Color) = (1,1,1,1) }
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float _Amount;
            CBUFFER_END
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { return _Amount; }
            ENDHLSL
        }
    }
}
