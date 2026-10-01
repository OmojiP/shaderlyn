// ビルトインの UnityCG.cginc / Lighting.cginc を取り込む、取り込まれる前提の断片。
#ifndef VALID_BUILTIN_LIGHTING_HELPERS_INCLUDED
#define VALID_BUILTIN_LIGHTING_HELPERS_INCLUDED

#include "UnityCG.cginc"
#include "Lighting.cginc"

// 平行光源の Lambert 拡散と球面調和の環境光を足し合わせる。
fixed3 SimpleDiffuseLighting(fixed3 albedo, float3 worldNormal)
{
    float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
    fixed3 diffuse = _LightColor0.rgb * saturate(dot(worldNormal, lightDir));
    fixed3 ambient = ShadeSH9(float4(worldNormal, 1.0));
    return albedo * (diffuse + ambient);
}

#endif // VALID_BUILTIN_LIGHTING_HELPERS_INCLUDED
