using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Rules;
using Shaderlyn.Semantics;

namespace Shaderlyn.Unity.Rules;

/// <summary>
/// キーワードの構成が <c>#error</c> に到達することを報告する (UN0103)。
/// </summary>
/// <remarks>
/// <para>
/// Unity は <c>multi_compile</c> / <c>shader_feature</c> で宣言したキーワードの構成をすべてコンパイルするので、
/// 到達すればコンパイルに失敗する。Unreal や汎用の HLSL と違い、誤りとして伝える。
/// </para>
/// <para>
/// どの構成でも到達するもの (条件が常に真) は、解析のマクロの状態が実際と違うためのことが多いので、
/// ここでは伝えず CF0104 (情報) に任せる。
/// </para>
/// </remarks>
internal sealed class KeywordErrorAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [UnityRuleDescriptors.ErrorReachedByKeywords];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (compilation.Environment is not UnityHlslEnvironment)
        {
            return;
        }

        foreach (ErrorConfigurationAnalyzer.ErrorConfigurationReport report in ErrorConfigurationAnalyzer.Describe(compilation))
        {
            if (report.Condition.IsAlways)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                UnityRuleDescriptors.ErrorReachedByKeywords,
                report.Location,
                report.Configuration,
                report.ErrorFile,
                report.ErrorLine));
        }
    }
}
