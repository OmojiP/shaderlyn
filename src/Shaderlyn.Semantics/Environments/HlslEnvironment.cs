using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// 汎用の HLSL 環境の設定。
/// </summary>
public sealed record HlslEnvironmentOptions
{
    /// <summary>既定の設定。HLSL 2021、構成の軸なし。</summary>
    public static HlslEnvironmentOptions Default { get; } = new();

    /// <summary>環境の名前。既定は <c>hlsl</c>。同じ作りで設定だけが違う環境 (<c>unreal</c>) は名前を変える。</summary>
    public string Name { get; init; } = HlslEnvironment.EnvironmentName;

    /// <summary>コンパイラ。既定は <see cref="HlslCompiler.Dxc"/>。</summary>
    /// <remarks>FXC のときは言語のバージョンを使わず、DXC が定義するマクロ (<c>__HLSL_VERSION</c> など) も定義しない。</remarks>
    public HlslCompiler Compiler { get; init; } = HlslCompiler.Dxc;

    /// <summary>
    /// HLSL の言語のバージョン (DXC の <c>-HV</c>)。既定は <see cref="HlslLanguageVersion.Hlsl2021"/> (DXC 1.7 以降の既定)。
    /// </summary>
    public HlslLanguageVersion LanguageVersion { get; init; } = HlslLanguageVersion.Hlsl2021;

    /// <summary>このコードをコンパイルする方言。</summary>
    public HlslDialect Dialect => Compiler == HlslCompiler.Fxc
        ? HlslDialect.Fxc
        : HlslDialect.Dxc(LanguageVersion) with { Enable16BitTypes = Enable16BitTypes };

    /// <summary>
    /// 16 ビットの型を使うか (DXC の <c>-enable-16bit-types</c>)。立っていれば <c>__HLSL_ENABLE_16_BIT</c> を 1 で定義する。
    /// </summary>
    /// <remarks>
    /// DXC の版によって、このオプションでマクロを定義するかが違う (1.6 は定義しない)。設定で明示したときだけ定義する。
    /// </remarks>
    public bool Enable16BitTypes { get; init; }

    /// <summary>
    /// どの単位にも共通する構成の空間。単位ごとの指定 (<see cref="HlslCompilationUnit.Variants"/>) があれば、そちらを使う。
    /// </summary>
    public VariantSpace Variants { get; init; } = VariantSpace.Empty;

    /// <summary>
    /// ビルドのときに生成される <c>#include</c> のパスの接頭辞 (<c>/Engine/Generated/</c>)。
    /// </summary>
    /// <remarks><see cref="IHlslEnvironment.IsGeneratedInclude"/> を見ること。</remarks>
    public ImmutableArray<string> GeneratedIncludePrefixes { get; init; } = [];

    /// <summary>
    /// 生成される取り込みのうち、宣言する関数が分かっているもの。キーはパスで、`/` で終わるものはその下のすべてを指す。
    /// 値は宣言しうる関数の名前 (関数を宣言しないなら空)。
    /// </summary>
    /// <remarks><see cref="IHlslEnvironment.GetFunctionsDeclaredIn"/> を見ること。</remarks>
    public ImmutableDictionary<string, ImmutableArray<string>> GeneratedIncludeFunctions { get; init; } =
        ImmutableDictionary<string, ImmutableArray<string>>.Empty;

    /// <summary>
    /// どの単位にも共通する定義済みマクロ。DXC が定義するものの後、解析の設定で与えたものの前に重ねる。
    /// </summary>
    /// <remarks>エンジンのシェーダーコンパイラが定義するもの (Unreal の <c>COMPILER_HLSL</c> など) を与える。</remarks>
    public ImmutableDictionary<string, string> PredefinedMacros { get; init; } = ImmutableDictionary<string, string>.Empty;
}

/// <summary>
/// 汎用の HLSL 環境。DXC で直接コンパイルする構成 (DirectX 12 のエンジン、独自エンジン) に使う。
/// </summary>
/// <param name="options">環境の設定。</param>
/// <remarks>
/// <para>
/// <b>構成の空間・エントリポイント・ターゲットは、ソースの中からは読まない。</b>
/// どれも設定ファイルで単位ごとに与える (<see cref="HlslCompilationUnit.Variants"/> /
/// <see cref="HlslCompilationUnit.EntryPoints"/> / <see cref="HlslCompilationUnit.Target"/>)。
/// <c>#pragma</c> は DXC と同じく読み流す。
/// </para>
/// <para>
/// 定義済みマクロは、DXC が定義するもの (<see cref="HlslShaderTarget.CreateDxcMacros"/>) と、
/// 解析の設定で与えたもの (<see cref="SemanticsOptions.PredefinedMacros"/>) である。同じ名前なら設定が勝つ。
/// </para>
/// </remarks>
public sealed class HlslEnvironment(HlslEnvironmentOptions? options = null) : IHlslEnvironment
{
    /// <summary>環境の名前。</summary>
    public const string EnvironmentName = "hlsl";

    /// <summary>環境の設定。</summary>
    public HlslEnvironmentOptions Options { get; } = options ?? HlslEnvironmentOptions.Default;

    /// <inheritdoc/>
    public string Name => Options.Name;

    /// <inheritdoc/>
    public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(semantics);

        // FXC は DXC のマクロ (__HLSL_VERSION / __SHADER_STAGE_* など) を定義しない。
        ImmutableDictionary<string, string> compilerMacros = Options.Compiler == HlslCompiler.Fxc
            ? ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal)
            : HlslShaderTarget.CreateCommonDxcMacros(Options.LanguageVersion)
                .SetItems(Options.Enable16BitTypes ? [new("__HLSL_ENABLE_16_BIT", "1")] : []);

        return options with
        {
            Dialects = [Options.Dialect],
            PredefinedMacros = compilerMacros
                .SetItems(Options.PredefinedMacros)
                .SetItems(semantics.PredefinedMacros ?? ImmutableDictionary<string, string>.Empty),
        };
    }

    /// <inheritdoc/>
    public PreprocessorOptions PrepareUnit(
        HlslCompilationUnit unit,
        ImmutableArray<HlslSyntaxToken> tokens,
        PreprocessorOptions options,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(options);

        // ターゲットで決まるマクロは、設定で与えたマクロ (共通・単位ごと) に負ける。
        return unit.Target is { } target && Options.Compiler == HlslCompiler.Dxc
            ? options with { PredefinedMacros = target.CreateDxcMacros(Options.LanguageVersion).SetItems(options.PredefinedMacros) }
            : options;
    }

    /// <inheritdoc/>
    public VariantSpace ReadDeclaredVariants(HlslCompilationUnit unit, ImmutableArray<HlslSyntaxToken> tokens)
        => VariantsOf(unit);

    /// <inheritdoc/>
    public VariantSpace ReadVariants(HlslCompilationUnit unit, PreprocessResult result) => VariantsOf(unit);

    /// <inheritdoc/>
    public ImmutableArray<HlslEntryPoint> ReadEntryPoints(HlslCompilationUnit unit, PreprocessResult result)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.EntryPoints;
    }

    /// <inheritdoc/>
    /// <remarks>構成の軸は設定で宣言するものなので、宣言に無い名前をシンボルとは見なさない。</remarks>
    public bool LooksLikeVariantSymbol(string name) => false;

    /// <inheritdoc/>
    public bool IsGeneratedInclude(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string normalized = path.Replace('\\', '/');
        return Options.GeneratedIncludePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public ImmutableArray<string>? GetFunctionsDeclaredIn(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string normalized = path.Replace('\\', '/');

        foreach ((string entry, ImmutableArray<string> functions) in Options.GeneratedIncludeFunctions)
        {
            if (entry.EndsWith('/')
                    ? normalized.StartsWith(entry, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(normalized, entry, StringComparison.OrdinalIgnoreCase))
            {
                return functions;
            }
        }

        return null;
    }

    /// <summary>
    /// ファイル全体を 1 つの単位にする。
    /// </summary>
    /// <param name="text">ファイル。</param>
    /// <returns>単位。構成の空間・エントリポイント・ターゲットは持たない。</returns>
    public static HlslCompilationUnit WholeFile(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new HlslCompilationUnit(text, new TextSpan(0, text.Length));
    }

    /// <summary>単位の構成の空間。単位の指定が無ければ共通のもの。</summary>
    /// <param name="unit">単位。</param>
    /// <returns>構成の空間。</returns>
    private VariantSpace VariantsOf(HlslCompilationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.Variants ?? Options.Variants;
    }
}
