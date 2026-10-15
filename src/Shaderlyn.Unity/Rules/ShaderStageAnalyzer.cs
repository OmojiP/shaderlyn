using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;
using Shaderlyn.Rules;

namespace Shaderlyn.Unity.Rules;

/// <summary>
/// コードブロックにシェーダーステージの <c>#pragma</c> があるかを検査する (UN0301)。
/// </summary>
/// <remarks>
/// <para>
/// 指定した関数があるかは、どの環境にも共通するルール (HL0301) が見る。
/// ここで見るのは、Unity の <c>#pragma</c> でステージを指定しているかどうかだけである。
/// </para>
/// <para>
/// <b>依存関係が完全に解決できている場合にのみ検査する。</b>
/// 取り込めなかったヘッダにステージの指定があるかもしれない。
/// </para>
/// </remarks>
internal sealed class ShaderStageAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>
    /// これがあればシェーダーステージが指定されているとみなす <c>#pragma</c>。
    /// </summary>
    /// <remarks>
    /// <c>raytracing</c> のように、
    /// 頂点・フラグメント以外の形でステージが決まる書き方も受け入れる。
    /// 知らない書き方を「指定が無い」と報告すると誤検出になる。
    /// </remarks>
    private static readonly FrozenSet<string> StagePragmas = new[]
    {
        "vertex", "fragment", "surface", "kernel", "raytracing", "geometry", "hull", "domain",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [UnityRuleDescriptors.MissingShaderStage];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 取り込まれる前提の断片 (.hlsl など) は、ステージの #pragma を持たないのが普通である。
        // ステージは取り込む側の .shader が決める。
        if (compilation.GetUnity() is null
            || !compilation.HasCompleteDependencies
            || ShaderSourceKinds.IsIncludeFragment(compilation.Text.FilePath))
        {
            return;
        }

        // コンピュートシェーダーの入口は #pragma kernel である。頂点・フラグメントを求めると、直し方を誤らせる。
        string expected = string.Equals(Path.GetExtension(compilation.Text.FilePath), ".compute", StringComparison.OrdinalIgnoreCase)
            ? "#pragma kernel"
            : "#pragma vertex / #pragma fragment";

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (!program.Tree.Pragmas.Any(pragma => StagePragmas.Contains(pragma.Name)))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    UnityRuleDescriptors.MissingShaderStage, program.GetBlockLocation(), expected));
            }
        }
    }
}
