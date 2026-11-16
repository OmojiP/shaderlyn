using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Slang;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// Slang の環境。1 ファイルを 1 つの単位にする。
/// </summary>
/// <remarks>
/// <para>
/// 構文は <see cref="SlangSyntaxTree"/> で読む。<c>import</c> で取り込むモジュールの宣言は、取り込んだヘッダと同じく見えるようにする (<see cref="SlangModules"/>)。
/// 型と組み込み関数は、HLSL 2021 の型システムに Slang の標準ライブラリの名前を足したもの (<see cref="Symbols.SlangTypeSystem"/>)。
/// </para>
/// <para>
/// <b>エントリポイントを決めない。</b> Slang のファイルはモジュールであることが多く、エントリポイントはコンパイルのときに選ぶ。
/// </para>
/// <para>
/// 設定ファイルの <c>symbols:</c> を構成の軸にする (GLSL の環境と同じ)。
/// </para>
/// </remarks>
/// <param name="variants">構成の軸。</param>
/// <param name="target">コンパイル先 (設定ファイルの <c>slang: target</c>)。指定が無ければ <see langword="null"/> (GPU のコンパイル先とみなす)。</param>
/// <param name="unscopedEnums">どの列挙型の列挙子も修飾せずに書けるか (設定ファイルの <c>slang: unscoped-enums</c>)。</param>
/// <param name="experimentalFeatures">実験中のモジュールを import できるか (設定ファイルの <c>slang: experimental-features</c>)。</param>
/// <param name="genericEntryPoints">ジェネリックのエントリポイントを API で特殊化するものとして通すか (設定ファイルの <c>slang: generic-entry-points</c>)。</param>
internal sealed class SlangEnvironment(VariantSpace? variants = null, string? target = null, bool unscopedEnums = false, bool experimentalFeatures = false, bool genericEntryPoints = false)
    : IShaderEnvironment
{
    /// <summary>実験中のモジュール (slang.neural など) を import できるか (slangc の <c>-experimental-feature</c>)。</summary>
    public bool ExperimentalFeatures { get; } = experimentalFeatures;

    /// <summary>ジェネリックのエントリポイントを、API (<c>specialize</c>) で特殊化するものとして通すか。</summary>
    public bool GenericEntryPoints { get; } = genericEntryPoints;

    private readonly SlangLanguageOptions _language = unscopedEnums ? new SlangLanguageOptions { UnscopedEnums = true } : SlangLanguageOptions.Default;

    /// <summary>コンパイル先 (<c>spirv</c>・<c>cpp</c> など)。指定が無ければ <see langword="null"/>。</summary>
    public string? Target { get; } = target;

    /// <summary>CPU か CUDA のコンパイル先か。局所変数のアドレスを取れるなど、GPU のコンパイル先と決まりが違う。</summary>
    public bool TargetsHost => Target is "cpp" or "cuda" or "ptx";

    /// <summary>
    /// 資源を StructuredBuffer の要素に入れられるコンパイル先か (CPU・CUDA と Metal。SPIR-V・HLSL・GLSL・WGSL は E38204 にする。2026-11-14 に slangc 2026.19 で確かめた)。
    /// </summary>
    public bool AllowsResourcesInBuffers => TargetsHost || Target == "metal";

    /// <summary>環境の名前。</summary>
    public const string EnvironmentName = "slang";

    private readonly VariantSpace _variants = variants ?? VariantSpace.Empty;

    /// <inheritdoc/>
    public string Name => EnvironmentName;

    /// <inheritdoc/>
    public Symbols.ShaderTypeSystem TypeSystem => Symbols.SlangTypeSystem.Instance;

    /// <inheritdoc/>
    public ShaderSyntaxTree Parse(SourceText text, PreprocessorOptions options, ISyntaxTimingRecorder? timings)
        => SlangSyntaxTree.Parse(text, options.Language is SlangLanguageOptions ? options : options with { Language = _language }, Symbols.SlangTypeSystem.Instance.IsStandardModule);

    /// <inheritdoc/>
    public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(semantics);

        return options with
        {
            Language = _language,

            // 解決できない取り込みは、その行で伝える (GLSL と同じ)。
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
        return options;
    }

    /// <inheritdoc/>
    public VariantSpace ReadDeclaredVariants(ShaderCompilationUnit unit, ImmutableArray<ShaderSyntaxToken> tokens) => VariantsOf(unit);

    /// <inheritdoc/>
    public VariantSpace ReadVariants(ShaderCompilationUnit unit, PreprocessResult result) => VariantsOf(unit);

    /// <inheritdoc/>
    public ImmutableArray<ShaderEntryPoint> ReadEntryPoints(ShaderCompilationUnit unit, PreprocessResult result)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.EntryPoints.IsDefault ? [] : unit.EntryPoints;
    }

    /// <inheritdoc/>
    public bool LooksLikeVariantSymbol(string name) => false;

    /// <inheritdoc/>
    public bool IsGeneratedInclude(string path) => false;

    /// <summary>ファイル全体を 1 つの単位にする。</summary>
    /// <param name="text">ファイル。</param>
    /// <returns>単位。</returns>
    /// <remarks>
    /// Slang のファイルはモジュールの一部 (<c>implementing</c>) や、取り込まれる側のモジュールであることが多い。
    /// 取り込む側で宣言した名前を使ってよいとする (<c>.gdshaderinc</c> と同じ)。
    /// </remarks>
    public static ShaderCompilationUnit WholeFile(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ShaderCompilationUnit(text, new TextSpan(0, text.Length));
    }

    private VariantSpace VariantsOf(ShaderCompilationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.Variants ?? _variants;
    }
}
