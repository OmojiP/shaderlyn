Shader "Conditional/SwitchedIncludeByCondition"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile _ _A

            // 取り込みを切り替える領域。両方のヘッダを、それぞれの条件のもとで処理する。
            #ifdef _A
            #include "SwitchedIncludeA.hlsl"
            #else
            #include "SwitchedIncludeB.hlsl"
            #endif

            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }

            half4 frag() : SV_Target
            {
            #ifdef _A
                // _OnlyWithoutA を宣言するヘッダは、_A のときには取り込まれない。
                return _OnlyWithA.x + _OnlyWithoutA.x;
            #else
                return _OnlyWithoutA.x;
            #endif
            }
            ENDHLSL
        }
    }
}
