// 取り込まれる前提のハッシュ関数とバリューノイズ。
#ifndef VALID_NOISE_INCLUDED
#define VALID_NOISE_INCLUDED

float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float ValueNoise(float2 uv)
{
    float2 cell = floor(uv);
    float2 local = frac(uv);
    float2 blend = local * local * (3.0 - 2.0 * local);

    float a = Hash21(cell);
    float b = Hash21(cell + float2(1.0, 0.0));
    float c = Hash21(cell + float2(0.0, 1.0));
    float d = Hash21(cell + float2(1.0, 1.0));

    return lerp(lerp(a, b, blend.x), lerp(c, d, blend.x), blend.y);
}

#endif // VALID_NOISE_INCLUDED
