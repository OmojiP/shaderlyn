using System.Collections.Immutable;

namespace Shaderlyn.Hlsl.Parsing;

/// <summary>HLSL をコンパイルするコンパイラ。</summary>
public enum HlslCompiler
{
    /// <summary>DirectX Shader Compiler (DXC)。言語のバージョンは <c>-HV</c> で選ぶ。</summary>
    Dxc,

    /// <summary>
    /// 旧来の D3DCompiler (FXC)。シェーダーモデル 5 まで。Unity の既定のコンパイラである。
    /// </summary>
    Fxc,
}

/// <summary>
/// HLSL の方言。どのコンパイラが、どの言語のバージョンでコンパイルするか。
/// </summary>
/// <param name="Compiler">コンパイラ。</param>
/// <param name="LanguageVersion">
/// 言語のバージョン (DXC の <c>-HV</c>)。FXC には言語のバージョンが無いので <see langword="null"/>。
/// </param>
/// <remarks>
/// <para>
/// 使える構文と型の規則は方言で決まる (<see cref="Features"/>)。
/// 表は手元の FXC (Windows SDK 10.0.26100) と DXC 1.8 / 1.6 を動かして確かめた結果である。
/// </para>
/// <para>
/// 1 つのコードが複数の方言でコンパイルされることがある (Unity は DX11 向けを FXC、DX12 向けを DXC でコンパイルしうる)。
/// その場合は方言を並べて持つ (<see cref="Preprocessing.PreprocessorOptions.Dialects"/>)。
/// </para>
/// </remarks>
public sealed record HlslDialect(HlslCompiler Compiler, HlslLanguageVersion? LanguageVersion)
{
    /// <summary>FXC。</summary>
    public static HlslDialect Fxc { get; } = new(HlslCompiler.Fxc, null);

    /// <summary>DXC の、指定した言語のバージョン。</summary>
    /// <param name="version">言語のバージョン。</param>
    /// <returns>方言。</returns>
    public static HlslDialect Dxc(HlslLanguageVersion version) => new(HlslCompiler.Dxc, version);

    /// <summary>16 ビットの型を有効にしてコンパイルするか (DXC の <c>-enable-16bit-types</c>)。</summary>
    public bool Enable16BitTypes { get; init; }

    /// <summary>
    /// DX9 の書き方 (<c>sampler2D</c> / <c>tex2D</c> / <c>sampler_state</c>) を受け付けるか。
    /// </summary>
    /// <remarks>
    /// FXC は受け付け、単体の DXC は受け付けない。
    /// Unity の DXC は受け付ける (Unity がコンパイラへ渡す前に変換していると考えられる)。
    /// </remarks>
    public bool AcceptsLegacySamplerSyntax { get; init; } = Compiler == HlslCompiler.Fxc;

    /// <summary>
    /// 画面に出す名前 (<c>dx11 (FXC)</c> など)。<see langword="null"/> ならコンパイラと言語のバージョンから作る。
    /// </summary>
    public string? Label { get; init; }

    /// <summary>この方言で使える構文と型。</summary>
    public HlslLanguageFeatures Features => HlslLanguageFeatures.Of(this);

    /// <summary>画面に出す名前。</summary>
    public string DisplayName => Label ?? (Compiler == HlslCompiler.Fxc
        ? "FXC"
        : $"HLSL {(int)(LanguageVersion ?? HlslLanguageVersion.Hlsl2018)}");
}

/// <summary>
/// 方言で使える構文と型の表。
/// </summary>
/// <remarks>
/// <para>
/// どの項目も「使える」の向きで持つ。複数の方言の「どれかで使える」は論理和、「どれでも使える」は論理積になる
/// (<see cref="Any"/> / <see cref="All"/>)。
/// </para>
/// <para>
/// 表は単体のコンパイラで確かめた (FXC は <c>ps_5_0</c>、DXC 1.8 は <c>-HV 2016 / 2017 / 2018 / 2021</c>)。
/// DXC 1.8 は <c>-HV 2015</c> をコンパイルできない (言語サービス専用)。
/// </para>
/// </remarks>
/// <param name="Templates"><c>template</c> (DXC 2021)。</param>
/// <param name="OperatorOverloading">構造体の中の演算子の多重定義 (DXC 2021)。</param>
/// <param name="Bitfields">ビットフィールド (DXC 2021)。</param>
/// <param name="Enums"><c>enum</c> / <c>enum class</c> (DXC 2017 以降)。</param>
/// <param name="Int64Types"><c>int64_t</c> / <c>uint64_t</c> (DXC)。</param>
/// <param name="ExplicitWidthTypes"><c>float32_t</c> / <c>int32_t</c> / <c>float64_t</c> などの明示のビット幅の型 (DXC 2018 以降)。</param>
/// <param name="SixteenBitTypes"><c>float16_t</c> / <c>int16_t</c> など (DXC 2018 以降で <c>-enable-16bit-types</c>)。</param>
/// <param name="ImplicitUserTypeConversion">
/// メンバーの形が同じ別の構造体の値を、暗黙に代入できるか (DXC 2021 より前と FXC)。
/// </param>
/// <param name="LoopVariableOutsideLoop"><c>for</c> の初期化で宣言した変数を、ループの外で使えるか (DXC 2021 より前と FXC)。</param>
/// <param name="VectorLogicalOperators">ベクトルに <c>&amp;&amp;</c> / <c>||</c> / <c>?:</c> を使えるか (DXC 2021 より前と FXC)。</param>
/// <param name="LogicalIntrinsics">
/// 組み込み関数 <c>and</c> / <c>or</c> / <c>select</c> (DXC。1.8 はどの版でも使える。1.6 は 2021 だけ)。
/// </param>
/// <param name="StructIncrement">構造体そのものへの <c>++</c> / <c>--</c> (FXC だけが通す。何もしない)。</param>
/// <param name="LegacySamplerSyntax"><c>sampler2D</c> / <c>tex2D</c> / <c>sampler_state</c> (<see cref="HlslDialect.AcceptsLegacySamplerSyntax"/>)。</param>
public sealed record HlslLanguageFeatures(
    bool Templates,
    bool OperatorOverloading,
    bool Bitfields,
    bool Enums,
    bool Int64Types,
    bool ExplicitWidthTypes,
    bool SixteenBitTypes,
    bool ImplicitUserTypeConversion,
    bool LoopVariableOutsideLoop,
    bool VectorLogicalOperators,
    bool LogicalIntrinsics,
    bool StructIncrement,
    bool LegacySamplerSyntax)
{
    /// <summary>1 つの方言の表を作る。</summary>
    /// <param name="dialect">方言。</param>
    /// <returns>表。</returns>
    public static HlslLanguageFeatures Of(HlslDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        if (dialect.Compiler == HlslCompiler.Fxc)
        {
            return new HlslLanguageFeatures(
                Templates: false,
                OperatorOverloading: false,
                Bitfields: false,
                Enums: false,
                Int64Types: false,
                ExplicitWidthTypes: false,
                SixteenBitTypes: false,
                ImplicitUserTypeConversion: true,
                LoopVariableOutsideLoop: true,
                VectorLogicalOperators: true,
                LogicalIntrinsics: false,
                StructIncrement: true,
                LegacySamplerSyntax: dialect.AcceptsLegacySamplerSyntax);
        }

        int version = (int)(dialect.LanguageVersion ?? HlslLanguageVersion.Hlsl2018);
        bool is2021 = version >= 2021;

        return new HlslLanguageFeatures(
            Templates: is2021,
            OperatorOverloading: is2021,
            Bitfields: is2021,
            Enums: version >= 2017,
            Int64Types: true,
            ExplicitWidthTypes: version >= 2018,
            SixteenBitTypes: version >= 2018 && dialect.Enable16BitTypes,
            ImplicitUserTypeConversion: !is2021,
            LoopVariableOutsideLoop: !is2021,
            VectorLogicalOperators: !is2021,
            LogicalIntrinsics: true,
            StructIncrement: false,
            LegacySamplerSyntax: dialect.AcceptsLegacySamplerSyntax);
    }

    /// <summary>並べた方言の<b>どれかで</b>使えるものを集める (論理和)。</summary>
    /// <param name="dialects">方言。</param>
    /// <returns>表。方言が無ければ、どれも使えない表。</returns>
    /// <remarks>パーサーは、どれかの方言が受け付ける構文を読む。</remarks>
    public static HlslLanguageFeatures Any(IEnumerable<HlslDialect> dialects)
        => Combine(dialects, static (a, b) => a || b, seed: false);

    /// <summary>並べた方言の<b>どれでも</b>使えるものを集める (論理積)。</summary>
    /// <param name="dialects">方言。</param>
    /// <returns>表。方言が無ければ、どれも使える表。</returns>
    public static HlslLanguageFeatures All(IEnumerable<HlslDialect> dialects)
        => Combine(dialects, static (a, b) => a && b, seed: true);

    /// <summary>方言ごとの表を、項目ごとに組み合わせる。</summary>
    /// <param name="dialects">方言。</param>
    /// <param name="combine">組み合わせ方。</param>
    /// <param name="seed">方言が無いときの値。</param>
    /// <returns>表。</returns>
    private static HlslLanguageFeatures Combine(IEnumerable<HlslDialect> dialects, Func<bool, bool, bool> combine, bool seed)
    {
        ArgumentNullException.ThrowIfNull(dialects);

        HlslLanguageFeatures result = new(seed, seed, seed, seed, seed, seed, seed, seed, seed, seed, seed, seed, seed);

        foreach (HlslLanguageFeatures features in dialects.Select(Of))
        {
            result = new HlslLanguageFeatures(
                combine(result.Templates, features.Templates),
                combine(result.OperatorOverloading, features.OperatorOverloading),
                combine(result.Bitfields, features.Bitfields),
                combine(result.Enums, features.Enums),
                combine(result.Int64Types, features.Int64Types),
                combine(result.ExplicitWidthTypes, features.ExplicitWidthTypes),
                combine(result.SixteenBitTypes, features.SixteenBitTypes),
                combine(result.ImplicitUserTypeConversion, features.ImplicitUserTypeConversion),
                combine(result.LoopVariableOutsideLoop, features.LoopVariableOutsideLoop),
                combine(result.VectorLogicalOperators, features.VectorLogicalOperators),
                combine(result.LogicalIntrinsics, features.LogicalIntrinsics),
                combine(result.StructIncrement, features.StructIncrement),
                combine(result.LegacySamplerSyntax, features.LegacySamplerSyntax));
        }

        return result;
    }

    /// <summary>
    /// 並べた方言を、画面に出す 1 つの言葉にする (<c>FXC</c> / <c>HLSL 2018</c> / <c>FXC・HLSL 2018</c>)。
    /// </summary>
    /// <param name="dialects">方言。</param>
    /// <returns>画面に出す名前。</returns>
    public static string Describe(ImmutableArray<HlslDialect> dialects)
        => dialects.IsDefaultOrEmpty
            ? "HLSL"
            : string.Join("・", dialects.Select(d => d.DisplayName).Distinct(StringComparer.Ordinal));
}
