using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>数値リテラルの上限を確かめる。</summary>
public sealed class LiteralLimitAnalyzer(double maximum) : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.LiteralTooLarge];

    protected override void InitializeShader(ShaderAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<LiteralExpressionSyntax>(c =>
        {
            // 接尾辞・16 進・8 進の読み分けは ShaderLiteral が行う。読めないものは報告しない。
            if (ShaderLiteral.TryGetDouble(c.Node.Token, out double value) && value > maximum)
            {
                c.ReportDiagnostic(CookbookRules.LiteralTooLarge, c.Node.Token.Text, maximum);
            }
        });
    }
}
