using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Wgsl;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// WGSL の環境。1 ファイルを 1 つの単位にする。
/// </summary>
/// <remarks>
/// <para>
/// 構文は <see cref="WgslSyntaxTree"/> で読む。型と組み込み関数は <see cref="Symbols.WgslTypeSystem"/>。
/// WGSL にはプリプロセッサも取り込みも無いので、ファイルの中だけを見る。
/// </para>
/// <para>
/// <b>エントリポイントを決めない。</b> 1 つのファイルに頂点・フラグメント・計算のエントリポイントを並べてよく、どれを使うかはパイプラインを作るときに選ぶ。
/// </para>
/// </remarks>
internal sealed class WgslEnvironment : IShaderEnvironment
{
    /// <summary>環境の名前。</summary>
    public const string EnvironmentName = "wgsl";

    /// <inheritdoc/>
    public string Name => EnvironmentName;

    /// <inheritdoc/>
    public Symbols.ShaderTypeSystem TypeSystem => Symbols.WgslTypeSystem.Instance;

    /// <inheritdoc/>
    public ShaderSyntaxTree Parse(SourceText text, PreprocessorOptions options, ISyntaxTimingRecorder? timings)
        => WgslSyntaxTree.Parse(text, options);

    /// <inheritdoc/>
    public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(semantics);

        return options with
        {
            Language = WgslLanguageOptions.Default,
            NestedBlockComments = true,
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
    public VariantSpace ReadDeclaredVariants(ShaderCompilationUnit unit, ImmutableArray<ShaderSyntaxToken> tokens) => VariantSpace.Empty;

    /// <inheritdoc/>
    public VariantSpace ReadVariants(ShaderCompilationUnit unit, PreprocessResult result) => VariantSpace.Empty;

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
    public static ShaderCompilationUnit WholeFile(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ShaderCompilationUnit(text, new TextSpan(0, text.Length));
    }
}
