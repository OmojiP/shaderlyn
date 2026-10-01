using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>数値リテラルの上限を確かめる。</summary>
public sealed class LiteralLimitAnalyzer(double maximum) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.LiteralTooLarge];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<LiteralExpressionSyntax>(c =>
        {
            // 接尾辞・16 進・8 進の読み分けは HlslLiteral が行う。読めないものは報告しない。
            if (HlslLiteral.TryGetDouble(c.Node.Token, out double value) && value > maximum)
            {
                c.ReportDiagnostic(CookbookRules.LiteralTooLarge, c.Node.Token.Text, maximum);
            }
        });
    }
}
