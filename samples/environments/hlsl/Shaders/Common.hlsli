// 取り込まれる断片。フォルダーを指定した解析では単体では解析しない (名前を挙げると解析する)。
#ifndef COMMON_HLSLI
#define COMMON_HLSLI

cbuffer PerFrame : register(b0)
{
    float4x4 ViewProj;
    float4 FogColor;
};

Texture2D<float4> Albedo : register(t0);
SamplerState LinearSampler : register(s0);

struct VsOut
{
    float4 Position : SV_Position;
    float2 Uv : TEXCOORD0;
};

#endif
