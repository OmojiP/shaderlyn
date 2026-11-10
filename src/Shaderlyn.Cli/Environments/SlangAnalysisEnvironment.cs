using System.Collections.Immutable;
using Shaderlyn.Configuration;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Cli.Environments;

/// <summary>
/// Slang の環境。1 ファイルを 1 つの単位にし、意味解析のモデルを作る。
/// </summary>
/// <remarks>
/// <para>
/// 設定ファイルの <c>symbols:</c> を構成の軸にする (GLSL の環境と同じ書き方)。<c>units:</c> は読まない。
/// <c>import</c> で取り込むモジュールは、取り込みと同じ探し方 (ファイルのフォルダー、<c>include-paths</c>) で探す (<see cref="SlangEnvironment"/>)。
/// </para>
/// <para>
/// <b>ルールは、Slang で確かめたものだけが働く</b> (<see cref="Semantics.Analysis.ShaderRuleAnalyzer.AnalyzesSlang"/>)。
/// </para>
/// </remarks>
/// <param name="configuration">設定ファイルの内容。</param>
internal sealed class SlangAnalysisEnvironment(ShaderlynConfiguration configuration) : IAnalysisEnvironment
{
    /// <summary>Slang のファイル。</summary>
    public static ImmutableArray<string> Extensions { get; } = [".slang"];

    private readonly SlangEnvironment _environment = new(
        HlslAnalysisEnvironment.BuildVariants(configuration ?? throw new ArgumentNullException(nameof(configuration)), null),
        configuration.SlangTarget);

    /// <inheritdoc/>
    public string Name => SlangEnvironment.EnvironmentName;

    /// <inheritdoc/>
    public string DisplayName => "Slang";

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
            CFamily.Syntax.ShaderSyntaxTree tree = Slang.SlangSyntaxTree.Parse(text);
            return new PreparedFile(new AnalysisTarget(text, tree.Root, tree.Diagnostics), [], null);
        }

        long expandStart = AnalysisTimings.Now();
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [SlangEnvironment.WholeFile(text)], text, _environment, ShaderSourceInfo.None, semantics);
        timings?.Add(AnalysisStep.Expand, expandStart);

        long unitStart = AnalysisTimings.Now();
        AnalysisTarget target = compilation.CreateAnalysisTarget();
        timings?.Add(AnalysisStep.Target, unitStart);

        return new PreparedFile(target, compilation.AllIncludePaths, compilation);
    }
}
