using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Hlsl.Parsing;

/// <summary>
/// HLSL の言語の設定 (<see cref="PreprocessorOptions.Language"/>)。方言とコンパイル先を持つ。
/// </summary>
/// <remarks>
/// 展開そのものには関わらない。構文解析 (<see cref="HlslSyntaxTree.Parse"/>) とルールが読む。
/// 展開と構文解析の設定を 1 つにまとめて渡しているので、プリプロセッサの設定に入れて渡す。
/// </remarks>
public sealed record HlslLanguageOptions : ShaderLanguageOptions
{
    /// <summary>既定の設定。FXC (Unity の既定のコンパイラ) で読む。</summary>
    public static HlslLanguageOptions Default { get; } = new();

    /// <summary>
    /// このコードをコンパイルする方言。既定は FXC (Unity の既定のコンパイラ)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>1 つのコードが複数の方言でコンパイルされることがある。</b>
    /// パーサーは、どれかの方言が受け付ける構文を読む (<see cref="HlslLanguageFeatures.Any"/>)。
    /// どの方言も受け付けない構文を HL0003 として報告する。
    /// </para>
    /// </remarks>
    public ImmutableArray<HlslDialect> Dialects { get; init; } = [HlslDialect.Fxc];

    /// <summary>
    /// DXC の言語のバージョン。設定すると、方言をその版の DXC 1 つにする (<see cref="Dialects"/> の略記)。
    /// </summary>
    /// <remarks>
    /// 読むと、並べた方言のうち最も新しい DXC の版を返す。DXC が無ければ <see cref="HlslLanguageVersion.Hlsl2018"/>。
    /// </remarks>
    public HlslLanguageVersion LanguageVersion
    {
        get => Dialects.Where(d => d.Compiler == HlslCompiler.Dxc).Select(d => d.LanguageVersion).Max() ?? HlslLanguageVersion.Hlsl2018;
        init => Dialects = [HlslDialect.Dxc(value)];
    }

    /// <summary>並べた方言の<b>どれかで</b>使える構文と型。パーサーはこれで読む。</summary>
    public HlslLanguageFeatures Features => HlslLanguageFeatures.Any(Dialects);

    /// <summary>
    /// コンパイル先 (API と方言の組)。環境が分かるときだけ並べる。空なら分からない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 設定すると、方言 (<see cref="Dialects"/>) をコンパイル先の方言にする。
    /// 使える API のコンパイル先があれば、その方言だけにする。使えない API でのエラーは利用者に見えない。
    /// </para>
    /// <para>
    /// Unity の環境が、パスごとの <c>#pragma use_dxc</c> などから決める。
    /// </para>
    /// </remarks>
    public ImmutableArray<HlslCompileTarget> CompileTargets
    {
        get;
        init
        {
            field = value.IsDefault ? [] : value;

            if (field.IsEmpty)
            {
                return;
            }

            IEnumerable<HlslCompileTarget> usable = field.Any(t => t.IsUsable) ? field.Where(t => t.IsUsable) : field;
            Dialects = [.. usable.Select(t => t.Dialect).Distinct()];
        }
    } = [];

    /// <summary>
    /// 使えない構文を報告するとき (HL0003) に、どこで使えないかを伝える名前 (<c>Unity のシェーダー</c>)。
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> なら方言で言う (<c>FXC</c> / <c>HLSL 2018</c>)。
    /// 環境が与える。Unity の環境は、Unity のコンパイルでエラーになることを伝える。
    /// </remarks>
    public string? DialectName { get; init; }

    /// <summary>HL0003 の文に入れる、どこで使えないかを伝える名前。<see cref="DialectName"/> が無ければ方言で言う。</summary>
    /// <returns>名前。</returns>
    public override string DescribeUnavailable() => DialectName ?? HlslLanguageFeatures.Describe(Dialects);

    /// <inheritdoc/>
    public override bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens) => HlslParser.IsCompleteUnits(tokens);
}

/// <summary>
/// プリプロセッサの設定と構文木から、HLSL の言語の設定を読み書きする。
/// </summary>
public static class HlslLanguageExtensions
{
    /// <summary>HLSL の言語の設定を返す。設定が無ければ既定 (<see cref="HlslLanguageOptions.Default"/>)。</summary>
    /// <param name="options">プリプロセッサの設定。</param>
    /// <returns>HLSL の言語の設定。</returns>
    public static HlslLanguageOptions Hlsl(this PreprocessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Language as HlslLanguageOptions ?? HlslLanguageOptions.Default;
    }

    /// <summary>HLSL の言語の設定を変えた設定を返す。</summary>
    /// <param name="options">プリプロセッサの設定。</param>
    /// <param name="change">今の HLSL の言語の設定から、新しい設定を作る。</param>
    /// <returns>変えた設定。</returns>
    public static PreprocessorOptions WithHlsl(this PreprocessorOptions options, Func<HlslLanguageOptions, HlslLanguageOptions> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return options with { Language = change(options.Hlsl()) };
    }

    /// <summary>
    /// 木を読んだ HLSL の言語の設定 (方言・言語のバージョン・コンパイル先・使える構文) を返す。
    /// </summary>
    /// <param name="tree">構文木。</param>
    /// <returns>HLSL の言語の設定。HLSL の木でなければ既定 (<see cref="HlslLanguageOptions.Default"/>)。</returns>
    public static HlslLanguageOptions Hlsl(this ShaderSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.Language as HlslLanguageOptions ?? HlslLanguageOptions.Default;
    }
}
