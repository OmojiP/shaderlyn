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
internal sealed class SlangEnvironment(VariantSpace? variants = null, string? target = null) : IShaderEnvironment
{
    /// <summary>コンパイル先 (<c>spirv</c>・<c>cpp</c> など)。指定が無ければ <see langword="null"/>。</summary>
    public string? Target { get; } = target;

    /// <summary>CPU か CUDA のコンパイル先か。資源をバッファーに入れられるなど、GPU のコンパイル先と決まりが違う。</summary>
    public bool TargetsHost => Target is "cpp" or "cuda" or "ptx";

    /// <summary>環境の名前。</summary>
    public const string EnvironmentName = "slang";

    private readonly VariantSpace _variants = variants ?? VariantSpace.Empty;

    /// <inheritdoc/>
    public string Name => EnvironmentName;

    /// <inheritdoc/>
    public Symbols.ShaderTypeSystem TypeSystem => Symbols.SlangTypeSystem.Instance;

    /// <inheritdoc/>
    public ShaderSyntaxTree Parse(SourceText text, PreprocessorOptions options, ISyntaxTimingRecorder? timings)
        => SlangSyntaxTree.Parse(text, options, Symbols.SlangTypeSystem.Instance.IsStandardModule);

    /// <inheritdoc/>
    public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(semantics);

        return options with
        {
            Language = SlangLanguageOptions.Default,

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
