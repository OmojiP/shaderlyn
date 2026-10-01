using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// <c>else if</c> の連なりが長すぎるものを報告する。
/// </summary>
/// <remarks>
/// <c>else if</c> は「else の中に if がある」形で表される。
/// 連なりの先頭でだけ数え、途中の if では数えない。
/// 数えないと、5 連なりに対して 5 件・4 件・3 件…と報告することになる。
/// </remarks>
public sealed class TooManyBranchesAnalyzer(int maxElseCount) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.TooManyBranches];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<IfStatementSyntax>(c =>
        {
            // 連なりの途中なら、先頭が数えるので何もしない。
            if (c.Node.Parent is IfStatementSyntax parent && ReferenceEquals(parent.ElseStatement, c.Node))
            {
                return;
            }

            int elseCount = 0;

            for (IfStatementSyntax? current = c.Node; current?.ElseStatement is not null;)
            {
                elseCount++;
                current = current.ElseStatement as IfStatementSyntax;
            }

            if (elseCount > maxElseCount)
            {
                c.ReportDiagnostic(
                    CookbookRules.TooManyBranches, c.Node.IfKeyword.Span, elseCount, maxElseCount);
            }
        });
    }
}
