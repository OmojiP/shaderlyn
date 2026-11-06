using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Godot;

namespace Shaderlyn.Semantics.Environments;

/// <summary>
/// Godot シェーダー言語の環境。1 ファイルを 1 つの単位にする。
/// </summary>
/// <remarks>
/// <para>
/// シェーダーの種類はファイルの <c>shader_type</c> で決まる (<see cref="GodotLanguageOptions.ShaderType"/>)。組み込みの変数は種類で変わる (<see cref="Symbols.GodotTypeSystem"/>)。
/// </para>
/// <para>
/// <b>エントリポイントを決めない。</b> Godot のステージ関数 (<c>vertex()</c> / <c>fragment()</c> など) は書かなくてもよい。
/// <c>main</c> のような必ず要る関数は無いので、無いことを報告しない。
/// </para>
/// <para>
/// 設定ファイルの <c>symbols:</c> を構成の軸にする (GLSL の環境と同じ)。
/// </para>
/// <para>
/// レンダラーは、設定 (<c>godot: renderer</c>)、ファイルのプロジェクトの <c>project.godot</c>、Forward+ の順に決める (<see cref="ResolveRenderer"/>)。
/// </para>
/// </remarks>
/// <param name="variants">構成の軸。</param>
/// <param name="renderer">設定で決めたレンダラー。<see langword="null"/> なら <c>project.godot</c> から読む。</param>
/// <param name="projectRoot">設定で決めたプロジェクトのフォルダー。<see langword="null"/> なら、ファイルから上へ辿って探す。</param>
internal sealed class GodotEnvironment(VariantSpace? variants = null, GodotRenderer? renderer = null, string? projectRoot = null) : IShaderEnvironment
{
    /// <summary>環境の名前。</summary>
    public const string EnvironmentName = "godot";

    private readonly VariantSpace _variants = variants ?? VariantSpace.Empty;

    /// <inheritdoc/>
    public string Name => EnvironmentName;

    /// <inheritdoc/>
    public Symbols.ShaderTypeSystem TypeSystem => Symbols.GodotTypeSystem.Instance;

    /// <inheritdoc/>
    public ShaderSyntaxTree Parse(SourceText text, PreprocessorOptions options, ISyntaxTimingRecorder? timings)
        => GodotSyntaxTree.Parse(text, options);

    /// <inheritdoc/>
    public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(semantics);

        return options with
        {
            Language = GodotLanguageOptions.Default,

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

        return options with
        {
            Language = new GodotLanguageOptions(GodotLanguageOptions.ReadShaderType(tokens)) { Renderer = ResolveRenderer(filePath) },
        };
    }

    /// <summary>ファイルを描くレンダラーを決める。設定、プロジェクトの <c>project.godot</c>、Forward+ の順。</summary>
    /// <param name="filePath">ファイルのパス。</param>
    /// <returns>レンダラー。</returns>
    public GodotRenderer ResolveRenderer(string? filePath)
        => renderer
            ?? ((projectRoot ?? GodotProject.FindRoot(filePath)) is { } root ? GodotProject.ReadRenderer(root) : null)
            ?? GodotRenderer.ForwardPlus;

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

    /// <summary>ファイル全体を 1 つの単位にする。取り込まれる断片 (<c>.gdshaderinc</c>) は、取り込む側で宣言した名前を使ってよい。</summary>
    /// <param name="text">ファイル。</param>
    /// <returns>単位。</returns>
    public static ShaderCompilationUnit WholeFile(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ShaderCompilationUnit(text, new TextSpan(0, text.Length))
        {
            AllowsExternalDefinitions = string.Equals(Path.GetExtension(text.FilePath), ".gdshaderinc", StringComparison.OrdinalIgnoreCase),
        };
    }

    private VariantSpace VariantsOf(ShaderCompilationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.Variants ?? _variants;
    }
}
