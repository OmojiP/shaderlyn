namespace Shaderlyn.Hlsl.Parsing;

/// <summary>
/// DXC の HLSL の言語のバージョン (<c>-HV</c>)。
/// </summary>
/// <remarks>
/// <para>
/// 値はバージョンの数そのものである。大小で比べられる。
/// FXC には言語のバージョンが無い (<see cref="HlslDialect"/>)。
/// </para>
/// <para>
/// DXC 1.8 が受け付けるのは 2016 / 2017 / 2018 / 2021。2015 は言語サービス専用でコンパイルできない。
/// どのバージョンで何が使えるかは <see cref="HlslLanguageFeatures"/> にある。
/// </para>
/// </remarks>
public enum HlslLanguageVersion
{
    /// <summary>HLSL 2016。int64 が使える。</summary>
    Hlsl2016 = 2016,

    /// <summary>HLSL 2017。<c>enum</c> が使える。</summary>
    Hlsl2017 = 2017,

    /// <summary>HLSL 2018。明示のビット幅の型 (<c>float32_t</c>) と 16 ビットの型が使える。Unity の DXC はこれ。</summary>
    Hlsl2018 = 2018,

    /// <summary>HLSL 2021。<c>template</c>・演算子の多重定義・ビットフィールドが使え、型の規則が厳しくなる。</summary>
    Hlsl2021 = 2021,
}
