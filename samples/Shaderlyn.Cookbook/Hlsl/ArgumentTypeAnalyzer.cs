using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 指定した関数の、指定した位置の引数の型を確かめる。
/// </summary>
/// <remarks>
/// <b>型が分からない式では報告しない。</b>
/// 型の判定は完全ではない。分からないものを誤りとして報告してはならない。
/// </remarks>
public sealed class ArgumentTypeAnalyzer(string function, int parameter, string expectedType) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.ArgumentType];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            if (c.Node.Target is not IdentifierExpressionSyntax name
                || !string.Equals(name.Name, function, StringComparison.Ordinal))
            {
                return;
            }

            ImmutableArray<HlslExpressionSyntax> arguments =
                [.. c.Node.Arguments.Select(a => a.Node).OfType<HlslExpressionSyntax>()];

            if (parameter >= arguments.Length)
            {
                return;
            }

            HlslExpressionSyntax argument = arguments[parameter];
            string? actual = c.Compilation.GetExpressionTypeBinder(c.Program)
                .GetEvaluatorFor(argument)
                .Evaluate(argument);

            // 型が分からないなら報告しない。
            if (actual is null || string.Equals(actual, expectedType, StringComparison.Ordinal))
            {
                return;
            }

            c.ReportDiagnostic(
                CookbookRules.ArgumentType, argument.Span, function, parameter + 1, expectedType, actual);
        });
    }
}
