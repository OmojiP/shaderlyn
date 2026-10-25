using System.Collections.Immutable;
using Shaderlyn.Configuration;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Cli.Environments;

/// <summary>
/// GLSL の環境。1 ファイルを 1 つの単位にし、意味解析のモデルを作る。
/// </summary>
/// <remarks>
/// <para>
/// 設定ファイルの <c>symbols:</c> を構成の軸にする (HLSL の環境と同じ書き方)。<c>units:</c> は読まない。
/// ステージ・エントリポイント・言語のバージョンは、ファイルの拡張子と <c>#version</c> で決まる (<see cref="GlslEnvironment"/>)。
/// </para>
/// <para>
/// <b>ルールは、GLSL で確かめたものだけが働く</b> (<see cref="Semantics.Analysis.ShaderRuleAnalyzer.AnalyzesGlsl"/>)。
/// C 系の言語に共通のルール (CF) は働き、HLSL にしか無いルール (HL) は働かない。
/// </para>
/// </remarks>
/// <param name="configuration">設定ファイルの内容。</param>
internal sealed class GlslAnalysisEnvironment(ShaderlynConfiguration configuration) : IAnalysisEnvironment
{
    /// <summary>GLSL のステージを表す拡張子と、取り込まれる側の <c>.glsl</c>。</summary>
    public static ImmutableArray<string> Extensions { get; } =
    [
        ".vert", ".frag", ".comp", ".geom", ".tesc", ".tese",
        ".mesh", ".task", ".rgen", ".rint", ".rahit", ".rchit", ".rmiss", ".rcall", ".glsl",
    ];

    private readonly GlslEnvironment _environment = new(new GlslEnvironmentOptions
    {
        Variants = HlslAnalysisEnvironment.BuildVariants(configuration ?? throw new ArgumentNullException(nameof(configuration)), null),
    });

    /// <inheritdoc/>
    public string Name => GlslEnvironment.EnvironmentName;

    /// <inheritdoc/>
    public string DisplayName => "GLSL";

    /// <inheritdoc/>
    public ImmutableArray<string> DefaultExtensions => Extensions;

    /// <inheritdoc/>
    public IShaderEnvironment ShaderEnvironment => _environment;

    /// <inheritdoc/>
    public PreparedFile Prepare(SourceText text, SemanticsOptions? semantics, AnalysisTimings? timings)
    {
        ArgumentNullException.ThrowIfNull(text);

        // 意味解析をしないときも、構文は読む (0.3.0 の構文だけの環境と同じ)。
        if (semantics is null)
        {
            CFamily.Syntax.ShaderSyntaxTree tree = Glsl.GlslSyntaxTree.Parse(text);
            return new PreparedFile(new AnalysisTarget(text, tree.Root, tree.Diagnostics), [], null);
        }

        long expandStart = AnalysisTimings.Now();
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [GlslEnvironment.WholeFile(text)], text, _environment, ShaderSourceInfo.None, semantics);
        timings?.Add(AnalysisStep.Expand, expandStart);

        long unitStart = AnalysisTimings.Now();
        AnalysisTarget target = compilation.CreateAnalysisTarget();
        timings?.Add(AnalysisStep.Target, unitStart);

        return new PreparedFile(target, compilation.AllIncludePaths, compilation);
    }
}
