using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 使ってはいけない関数の呼び出しを報告する。
/// </summary>
/// <remarks>
/// マクロと違い、関数は展開後も呼び出しの形で残る。構文木から探すほうが正確である。
/// 呼び出しの対象が単純な名前のときだけ見る。
/// </remarks>
public sealed class BannedFunctionAnalyzer(params string[] banned) : HlslRuleAnalyzer
{
    private readonly ImmutableHashSet<string> _banned = [.. banned];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.BannedFunction];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            if (c.Node.Target is IdentifierExpressionSyntax name && _banned.Contains(name.Name))
            {
                c.ReportDiagnostic(CookbookRules.BannedFunction, name.Span, name.Name);
            }
        });
    }
}
