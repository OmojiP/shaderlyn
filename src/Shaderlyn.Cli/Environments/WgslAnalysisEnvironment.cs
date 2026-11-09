using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Cli.Environments;

/// <summary>
/// WGSL の環境。1 ファイルを 1 つの単位にし、意味解析のモデルを作る。
/// </summary>
/// <remarks>
/// WGSL にはプリプロセッサも取り込みも無いので、設定ファイルの <c>symbols:</c> と <c>units:</c> は読まない。
/// <b>ルールは、WGSL で確かめたものだけが働く</b> (<see cref="Semantics.Analysis.ShaderRuleAnalyzer.AnalyzesWgsl"/>)。
/// </remarks>
internal sealed class WgslAnalysisEnvironment : IAnalysisEnvironment
{
    /// <summary>WGSL のファイル。</summary>
    public static ImmutableArray<string> Extensions { get; } = [".wgsl"];

    private readonly WgslEnvironment _environment = new();

    /// <inheritdoc/>
    public string Name => WgslEnvironment.EnvironmentName;

    /// <inheritdoc/>
    public string DisplayName => "WGSL";

    /// <inheritdoc/>
    public ImmutableArray<string> DefaultExtensions => Extensions;

    /// <inheritdoc/>
    public IShaderEnvironment ShaderEnvironment => _environment;

    /// <inheritdoc/>
    public PreparedFile Prepare(SourceText text, SemanticsOptions? semantics, AnalysisTimings? timings)
    {
        ArgumentNullException.ThrowIfNull(text);

        // 意味解析をしないときも、構文は読む。
        if (semantics is null)
        {
            CFamily.Syntax.ShaderSyntaxTree tree = Wgsl.WgslSyntaxTree.Parse(text);
            return new PreparedFile(new AnalysisTarget(text, tree.Root, tree.Diagnostics), [], null);
        }

        long expandStart = AnalysisTimings.Now();
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [WgslEnvironment.WholeFile(text)], text, _environment, ShaderSourceInfo.None, semantics);
        timings?.Add(AnalysisStep.Expand, expandStart);

        long unitStart = AnalysisTimings.Now();
        AnalysisTarget target = compilation.CreateAnalysisTarget();
        timings?.Add(AnalysisStep.Target, unitStart);

        return new PreparedFile(target, compilation.AllIncludePaths, compilation);
    }
}
