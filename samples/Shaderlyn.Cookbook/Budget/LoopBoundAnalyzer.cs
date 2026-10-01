using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// <c>for</c> がまわる回数の上限を確かめる。
/// </summary>
/// <remarks>
/// <para>
/// <b>回数が定数で書かれているものだけを見る。</b>
/// 上限が uniform で決まるループは、回数が実行時にしか分からない。
/// 分からないものを誤りとして報告してはならない。
/// </para>
/// <para>
/// 「動的なループそのものを禁じたい」なら、それは別のルールになる。
/// 条件が定数でないことを報告する形にすればよいが、
/// <c>static const</c> で書いた上限まで巻き込むので、誤検出を確かめてから入れること。
/// </para>
/// </remarks>
public sealed class LoopBoundAnalyzer(int maxIterations) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.LoopTooLong];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<ForStatementSyntax>(c =>
        {
            if (c.Node.Condition is not BinaryExpressionSyntax condition
                || condition.OperatorToken.Kind
                    is not (HlslSyntaxKind.LessThanToken or HlslSyntaxKind.LessThanEqualsToken)
                || condition.Right is not LiteralExpressionSyntax literal
                || !HlslLiteral.TryGetInt64(literal.Token, out long bound))
            {
                return;
            }

            long iterations =
                condition.OperatorToken.Kind == HlslSyntaxKind.LessThanEqualsToken ? bound + 1 : bound;

            if (iterations > maxIterations)
            {
                c.ReportDiagnostic(
                    CookbookRules.LoopTooLong, c.Node.ForKeyword.Span, iterations, maxIterations);
            }
        });
    }
}
