using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>分岐を禁じる。</summary>
public sealed class NoBranchAnalyzer : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.NoBranch];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // if キーワードだけを指す。文全体を指すと波線が数十行に広がる。
        context.RegisterNodeAction<IfStatementSyntax>(
            c => c.ReportDiagnostic(CookbookRules.NoBranch, c.Node.IfKeyword.Span));
    }
}
