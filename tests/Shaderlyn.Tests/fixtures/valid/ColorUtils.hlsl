// 取り込まれる前提の共通関数。ステージの指定もキーワードの宣言も持たない。
#ifndef VALID_COLOR_UTILS_INCLUDED
#define VALID_COLOR_UTILS_INCLUDED

static const float3 LuminanceWeights = float3(0.2126, 0.7152, 0.0722);

float Luminance709(float3 color)
{
    return dot(color, LuminanceWeights);
}

float3 SrgbToLinear(float3 color)
{
    return pow(max(color, 0.0), 2.2);
}

float3 LinearToSrgb(float3 color)
{
    return pow(max(color, 0.0), 1.0 / 2.2);
}

float3 AdjustSaturation(float3 color, float saturation)
{
    float gray = Luminance709(color);
    return lerp(float3(gray, gray, gray), color, saturation);
}

#endif // VALID_COLOR_UTILS_INCLUDED
