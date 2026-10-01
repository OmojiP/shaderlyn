// URP のライティングライブラリを取り込む、取り込まれる前提の断片。
#ifndef VALID_URP_LIGHTING_HELPERS_INCLUDED
#define VALID_URP_LIGHTING_HELPERS_INCLUDED

#ifdef SHADERLYN_TESTS

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// メインライトの Lambert 拡散と球面調和の環境光を足し合わせる。
half3 SimpleDiffuseLighting(half3 albedo, half3 normalWS)
{
    Light mainLight = GetMainLight();
    half3 diffuse = LightingLambert(mainLight.color, mainLight.direction, normalWS);
    half3 ambient = SampleSH(normalWS);
    return albedo * (diffuse + ambient);
}

#endif // SHADERLYN_TESTS

#endif // VALID_URP_LIGHTING_HELPERS_INCLUDED