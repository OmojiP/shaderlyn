using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Conditional;

namespace Shaderlyn.Rules;

/// <summary>
/// 出現条件を追えなかった箇所があることを報告する (SL0004)。
/// </summary>
/// <remarks>
/// <para>
/// <b>条件を根拠にするルールを入れた以上、これが要る。</b>
/// HL0311 / HL0313 / HL0340 は、条件が分からない箇所では何も報告しない。
/// それは正しい振る舞いだが、利用者から見ると
/// 「問題が無かった」のと区別が付かない。
/// </para>
/// <para>
/// <b>報告は追えなかった箇所そのものに出す。</b>
/// ファイルの先頭に出す SL0002 / SL0003 と違い、
/// ここでは原因の場所が分かっている。
/// 実測では 545 組のうち 2 組でしか起きないので、量の問題にもならない。
/// </para>
/// </remarks>
internal sealed class UnresolvedConditionAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [SemanticRuleDescriptors.UnresolvedCondition];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        ConditionMap condition = compilation.GetConditionMap();

        if (condition.IsComplete)
        {
            return;
        }

        // 同じ箇所が Pass ごとに現れる。位置で 1 つに寄せる。
        HashSet<int> reported = [];

        foreach (Location location in condition.UnmergedLocations)
        {
            // 利用者が書いた行だけを指す。
            // 取り込んだヘッダの中で条件を追えなくても、直しようがない。
            if (!string.Equals(location.FilePath, compilation.Text.FilePath, StringComparison.Ordinal)
                || !reported.Add(location.Span.Start))
            {
                continue;
            }

            context.ReportDiagnostic(SemanticRuleDescriptors.UnresolvedCondition, location.Span);
        }
    }
}
