using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>分岐を禁じる。</summary>
public sealed class NoBranchAnalyzer : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.NoBranch];

    protected override void InitializeShader(ShaderAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // if キーワードだけを指す。文全体を指すと波線が数十行に広がる。
        context.RegisterNodeAction<IfStatementSyntax>(
            c => c.ReportDiagnostic(CookbookRules.NoBranch, c.Node.IfKeyword.Span));
    }
}
