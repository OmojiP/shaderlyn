// 動作確認用。HLSL 2021 の構文 (enum class・ビットフィールド・演算子の多重定義・template) を読んで検査する。
// 意図的に誤りを入れてある (samples/environments/README.md)。
#include "Common.hlsli"

enum class Tonemap : uint { None, Reinhard, Aces };

struct PackedFlags
{
    uint tonemap : 2;
    uint useFog : 1;
};

struct Rgb
{
    float3 value;

    Rgb operator+(Rgb other) { Rgb r; r.value = value + other.value; return r; }
    Rgb operator*(float scale) { Rgb r; r.value = value * scale; return r; }
};

template<typename T>
T Lerp3(T a, T b, float t) { return a + (b - a) * t; }

template<typename T>
T MaxOf(T a, T b) { return max(a, b); }

VsOut MainVS(float3 position : POSITION, float2 uv : TEXCOORD0)
{
    VsOut o;
    o.Position = mul(ViewProj, float4(position, 1));
    o.Uv = uv;
    return o;
}

float4 MainPS(VsOut input, uint packed : FLAGS) : SV_Target
{
    PackedFlags flags;
    flags.tonemap = packed & 3;
    flags.useFog = (packed >> 2) & 1;

    Rgb base;
    base.value = Albedo.Sample(LinearSampler, input.Uv).rgb;
    Rgb fog;
    fog.value = FogColor.rgb;

    // 演算子の戻り値 (Rgb) として読む。
    Rgb color = base;

    if (flags.useFog)
    {
        color = (base + fog) * 0.5f;
    }

    // T は実引数から float3 に決まる。
    float3 mixed = Lerp3(color.value, FogColor.rgb, FogColor.a);

    if (flags.tonemap == (uint)Tonemap::Reinhard)
    {
        mixed = mixed / (1 + mixed);
    }

    // 誤り 1 (HL0345): 接尾辞の無い 1.0 は float とは別の型として推論されるので、T を決められない (1.0f と書く)
    float exposure = MaxOf(1.0, FogColor.a);

    // 誤り 2 (HL0312): (color * 2) は Rgb で、alpha というメンバーは無い
    return float4(mixed * exposure, (color * 2).alpha);
}
