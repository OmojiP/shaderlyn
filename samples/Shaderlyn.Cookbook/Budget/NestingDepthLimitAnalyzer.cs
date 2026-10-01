using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 制御構造の入れ子の深さを制限する。
/// </summary>
/// <remarks>
/// <para>
/// <b><c>else if</c> は同じ段として数える。</b>
/// 構文木の上では「else の中に if がある」形になっているため、
/// 素朴に親を辿ると 5 連の <c>else if</c> が 5 段に見える。
/// </para>
/// <para>
/// <b>上限を超えた最初の段でだけ報告する。</b>
/// 深いところすべてで報告すると、1 つの入れ子に何件も出る。
/// </para>
/// </remarks>
public sealed class NestingDepthLimitAnalyzer(int maxDepth) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.NestingTooDeep];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<HlslStatementSyntax>(c =>
        {
            if (!IsNesting(c.Node) || HlslSyntaxFacts.GetKeyword(c.Node) is not { } keyword)
            {
                return;
            }

            // else if の側は連なりの先頭と同じ段にある。先頭が数えるのでここでは見ない。
            if (c.Node.Parent is IfStatementSyntax head && ReferenceEquals(head.ElseStatement, c.Node))
            {
                return;
            }

            int depth = 1;
            SyntaxNode child = c.Node;

            for (SyntaxNode? node = c.Node.Parent; node is not null; child = node, node = node.Parent)
            {
                if (node is FunctionDeclarationSyntax)
                {
                    break;
                }

                if (node is IfStatementSyntax parent && ReferenceEquals(parent.ElseStatement, child))
                {
                    continue;
                }

                if (node is HlslStatementSyntax statement && IsNesting(statement))
                {
                    depth++;
                }
            }

            if (depth == maxDepth + 1)
            {
                c.ReportDiagnostic(CookbookRules.NestingTooDeep, keyword.Span, depth, maxDepth);
            }
        });
    }

    /// <summary>段を作る文かどうかを判定する。</summary>
    /// <param name="statement">対象の文。</param>
    /// <returns>段を作る文であれば <see langword="true"/>。</returns>
    private static bool IsNesting(HlslStatementSyntax statement)
        => statement is IfStatementSyntax
            or ForStatementSyntax
            or WhileStatementSyntax
            or DoWhileStatementSyntax
            or SwitchStatementSyntax;
}
