using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;
using Shaderlyn.Semantics.Conditional;

namespace Shaderlyn.Cookbook;

/// <summary>
/// あるシンボルが定義されている構成でのみ禁じられる関数を報告する。
/// </summary>
/// <remarks>
/// <para>
/// <b>「そのノードがどの構成に存在するか」は出現条件が答える。</b>
/// <c>#ifdef</c> の中にある呼び出しは、その条件のもとでしか存在しない。
/// </para>
/// <para>
/// 判定は「シンボルが未定義のときには存在しえない」かどうかで行う。
/// 条件と「シンボルが未定義」を論理積にして、成り立たなくなれば
/// その呼び出しはシンボルが定義された構成にしか無い。
/// </para>
/// </remarks>
public sealed class BannedUnderSymbolAnalyzer(string keyword, string function) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [CookbookRules.BannedUnderSymbol];

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

            ConditionMap condition = c.Compilation.GetConditionMap();
            SymbolCondition here = condition.GetCondition(c.Node);

            // 条件が分からないなら報告しない。分からないことを誤りにしてはならない。
            if (here.IsUnknown)
            {
                return;
            }

            // シンボルが無い構成では存在しない = そのシンボルのときだけ存在する。
            if (!condition.IsPossible(here.And(SymbolCondition.Symbol(keyword, isDefined: false))))
            {
                c.ReportDiagnostic(CookbookRules.BannedUnderSymbol, name.Span, function, keyword);
            }
        });
    }
}
