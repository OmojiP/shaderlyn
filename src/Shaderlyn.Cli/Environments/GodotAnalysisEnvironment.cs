using System.Collections.Immutable;
using Shaderlyn.Configuration;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Cli.Environments;

/// <summary>
/// Godot シェーダー言語の環境。1 ファイルを 1 つの単位にし、意味解析のモデルを作る。
/// </summary>
/// <remarks>
/// <para>
/// 設定ファイルの <c>symbols:</c> を構成の軸にする (GLSL の環境と同じ書き方)。<c>units:</c> は読まない。
/// シェーダーの種類はファイルの <c>shader_type</c> で決まる (<see cref="GodotEnvironment"/>)。
/// </para>
/// <para>
/// <b>ルールは、Godot で確かめたものだけが働く</b> (<see cref="Semantics.Analysis.ShaderRuleAnalyzer.AnalyzesGodot"/>)。
/// </para>
/// </remarks>
/// <param name="configuration">設定ファイルの内容。</param>
internal sealed class GodotAnalysisEnvironment(ShaderlynConfiguration configuration) : IAnalysisEnvironment
{
    /// <summary>Godot のシェーダーと、取り込まれる側の <c>.gdshaderinc</c>。</summary>
    public static ImmutableArray<string> Extensions { get; } = [".gdshader", ".gdshaderinc"];

    private readonly GodotEnvironment _environment = new(
        HlslAnalysisEnvironment.BuildVariants(configuration ?? throw new ArgumentNullException(nameof(configuration)), null));

    /// <inheritdoc/>
    public string Name => GodotEnvironment.EnvironmentName;

    /// <inheritdoc/>
    public string DisplayName => "Godot";

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
            CFamily.Syntax.ShaderSyntaxTree tree = Godot.GodotSyntaxTree.Parse(text);
            return new PreparedFile(new AnalysisTarget(text, tree.Root, tree.Diagnostics), [], null);
        }

        long expandStart = AnalysisTimings.Now();
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [GodotEnvironment.WholeFile(text)], text, _environment, ShaderSourceInfo.None, semantics);
        timings?.Add(AnalysisStep.Expand, expandStart);

        long unitStart = AnalysisTimings.Now();
        AnalysisTarget target = compilation.CreateAnalysisTarget();
        timings?.Add(AnalysisStep.Target, unitStart);

        return new PreparedFile(target, compilation.AllIncludePaths, compilation);
    }
}
