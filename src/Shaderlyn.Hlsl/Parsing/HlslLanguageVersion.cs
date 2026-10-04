namespace Shaderlyn.Hlsl.Parsing;

/// <summary>
/// 受け付ける HLSL の言語のバージョン。
/// </summary>
/// <remarks>
/// <para>
/// <b>環境が決める。</b> Unity のシェーダーのコンパイルは <c>template</c> / <c>enum</c> / <c>class</c> /
/// <c>interface</c> / 演算子の多重定義 / ビットフィールドを受け付けない。DXC は HLSL 2021 (<c>-HV 2021</c>) で受け付ける。
/// </para>
/// <para>
/// <see cref="Hlsl2018"/> では、これらを使えない構文 (HL0003) として報告して読み飛ばす。
/// <see cref="Hlsl2021"/> では報告しない。
/// </para>
/// </remarks>
public enum HlslLanguageVersion
{
    /// <summary>HLSL 2021 の構文を受け付けない (Unity)。既定。</summary>
    Hlsl2018,

    /// <summary>HLSL 2021 の構文を受け付ける (DXC の <c>-HV 2021</c>)。</summary>
    Hlsl2021,
}
