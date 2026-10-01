using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// <c>[numthreads]</c> のスレッド数を確かめる。
/// </summary>
/// <remarks>
/// GPU の実行単位 (wave / warp) の倍数でないと、
/// スレッドが遊んだまま起動されることになる。
/// </remarks>
public sealed class ThreadGroupSizeAnalyzer(int multipleOf) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.ThreadGroupSize];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<FunctionDeclarationSyntax>(c =>
        {
            // このブロックのカーネルの関数だけを見る。
            if (c.Program.KernelName is not { } kernel
                || !string.Equals(c.Node.Name, kernel, StringComparison.Ordinal))
            {
                return;
            }

            foreach (HlslAttributeSyntax attribute in c.Node.Attributes)
            {
                if (!string.Equals(attribute.Name, "numthreads", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int total = 1;
                bool known = true;

                foreach (HlslSyntaxToken token in attribute.ArgumentTokens)
                {
                    if (token.Kind == HlslSyntaxKind.NumericLiteralToken)
                    {
                        if (int.TryParse(token.Text, out int value))
                        {
                            total *= value;
                        }
                        else
                        {
                            known = false;
                        }
                    }
                    else if (token.Kind == HlslSyntaxKind.IdentifierToken)
                    {
                        // マクロが展開されずに残っている。分からないので報告しない。
                        known = false;
                    }
                }

                if (known && total % multipleOf != 0)
                {
                    c.ReportDiagnostic(
                        CookbookRules.ThreadGroupSize, attribute.Span, kernel, total, multipleOf);
                }
            }
        });
    }
}
