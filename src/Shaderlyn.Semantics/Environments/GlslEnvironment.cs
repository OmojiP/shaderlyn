using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Glsl;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// GLSL の環境の設定。
/// </summary>
internal sealed record GlslEnvironmentOptions
{
    /// <summary>既定の設定。構成の軸なし。</summary>
    public static GlslEnvironmentOptions Default { get; } = new();

    /// <summary>
    /// どの単位にも共通する構成の空間 (設定ファイルの <c>symbols:</c>)。単位ごとの指定 (<see cref="ShaderCompilationUnit.Variants"/>) があれば、そちらを使う。
    /// </summary>
    public VariantSpace Variants { get; init; } = VariantSpace.Empty;
}

/// <summary>
/// GLSL の環境。glslang でコンパイルするファイル (<c>.vert</c> / <c>.frag</c> など) を、1 ファイル 1 単位で読む。
/// </summary>
/// <param name="options">環境の設定。</param>
/// <remarks>
/// <para>
/// <b>ステージはファイルの拡張子、言語のバージョンはファイルの <c>#version</c> で決まる</b> (glslang と同じ)。
/// ステージのファイルのエントリポイントは <c>main</c> である。
/// ステージを表さない拡張子 (<c>.glsl</c>) は取り込まれる断片として読み、エントリポイントもバージョンも決めない。
/// </para>
/// <para>
/// 定義済みマクロは、glslang が定義するもの (<see cref="GlslLanguageOptions.CreateCompilerMacros"/>) と、
/// 解析の設定で与えたもの (<see cref="SemanticsOptions.PredefinedMacros"/>) である。同じ名前なら設定が勝つ。
/// </para>
/// <para>
/// 型と組み込みは GLSL のもの (<see cref="Symbols.GlslTypeSystem"/>) を使う。
/// </para>
/// </remarks>
internal sealed class GlslEnvironment(GlslEnvironmentOptions? options = null) : IShaderEnvironment
{
    /// <summary>環境の名前。</summary>
    public const string EnvironmentName = "glsl";

    /// <summary>エントリポイントの出どころとして、診断に出す語の始まり。</summary>
    private const string EntryOrigin = "GLSL";

    /// <summary>環境の設定。</summary>
    public GlslEnvironmentOptions Options { get; } = options ?? GlslEnvironmentOptions.Default;

    /// <inheritdoc/>
    public string Name => EnvironmentName;

    /// <inheritdoc/>
    public Symbols.ShaderTypeSystem TypeSystem => Symbols.GlslTypeSystem.Instance;

    /// <inheritdoc/>
    public ShaderSyntaxTree Parse(SourceText text, PreprocessorOptions options, ISyntaxTimingRecorder? timings)
        => GlslSyntaxTree.Parse(text, options);

    /// <inheritdoc/>
    public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(semantics);

        return options with
        {
            Language = GlslLanguageOptions.Default,

            // 解決できない取り込みは、その行で伝える (0.3.0 の構文だけの環境と同じ)。
            // HLSL の環境は取り込みのルール (IncludePathAnalyzer) が伝える。そのルールは、1 つも解決できないときに環境の問題 (TOOL0004) として伝えるので、GLSL では働かせない。
            ReportUnresolvedIncludes = true,
        };
    }

    /// <inheritdoc/>
    public PreprocessorOptions PrepareUnit(
        ShaderCompilationUnit unit,
        ImmutableArray<ShaderSyntaxToken> tokens,
        PreprocessorOptions options,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(options);

        GlslShaderStage? stage = GlslLanguageOptions.StageFromFileName(filePath);
        GlslLanguageOptions language = GlslLanguageOptions.TryReadVersion(tokens, out int version, out GlslProfile profile)
            ? new GlslLanguageOptions(version, profile, stage)
            : stage is null
                ? GlslLanguageOptions.Default
                : new GlslLanguageOptions(GlslLanguageOptions.VersionWithoutDirective, GlslProfile.Es, stage);

        // コンパイラが定義するマクロは、設定で与えたマクロ (共通・単位ごと) に負ける。
        return options with
        {
            Language = language,
            PredefinedMacros = language.CreateCompilerMacros().SetItems(options.PredefinedMacros),
        };
    }

    /// <inheritdoc/>
    public VariantSpace ReadDeclaredVariants(ShaderCompilationUnit unit, ImmutableArray<ShaderSyntaxToken> tokens)
        => VariantsOf(unit);

    /// <inheritdoc/>
    public VariantSpace ReadVariants(ShaderCompilationUnit unit, PreprocessResult result) => VariantsOf(unit);

    /// <inheritdoc/>
    /// <remarks>ステージのファイルは <c>main</c>。取り込まれる断片 (<c>.glsl</c>) には無い。</remarks>
    public ImmutableArray<ShaderEntryPoint> ReadEntryPoints(ShaderCompilationUnit unit, PreprocessResult result)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (!unit.EntryPoints.IsDefaultOrEmpty)
        {
            return unit.EntryPoints;
        }

        return GlslLanguageOptions.StageFromFileName(unit.Text.FilePath) is null
            ? []
            : [new ShaderEntryPoint("main", $"{EntryOrigin} ({Path.GetExtension(unit.Text.FilePath)})")];
    }

    /// <inheritdoc/>
    /// <remarks>構成の軸は設定で宣言するものなので、宣言に無い名前をシンボルとは見なさない。</remarks>
    public bool LooksLikeVariantSymbol(string name) => false;

    /// <inheritdoc/>
    public bool IsGeneratedInclude(string path) => false;

    /// <summary>
    /// ファイル全体を 1 つの単位にする。
    /// </summary>
    /// <param name="text">ファイル。</param>
    /// <returns>単位。取り込まれる断片 (<c>.glsl</c>) なら、宣言だけの関数の定義が取り込む側にありうる (<see cref="ShaderCompilationUnit.AllowsExternalDefinitions"/>)。</returns>
    public static ShaderCompilationUnit WholeFile(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ShaderCompilationUnit(text, new TextSpan(0, text.Length))
        {
            AllowsExternalDefinitions = GlslLanguageOptions.StageFromFileName(text.FilePath) is null,
        };
    }

    /// <summary>単位の構成の空間。単位の指定が無ければ共通のもの。</summary>
    /// <param name="unit">単位。</param>
    /// <returns>構成の空間。</returns>
    private VariantSpace VariantsOf(ShaderCompilationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.Variants ?? Options.Variants;
    }
}
