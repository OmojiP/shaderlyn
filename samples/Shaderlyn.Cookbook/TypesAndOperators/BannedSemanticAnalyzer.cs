using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 使ってはいけないセマンティクスを報告する。
/// </summary>
/// <remarks>
/// <para>
/// <b><c>register(t0)</c> や <c>packoffset(c0)</c> も同じノードで表される。</b>
/// 構文が「コロンと名前」で同一だからである。
/// 引数を伴うものはセマンティクスではないので外す。
/// </para>
/// <para>
/// <b>大文字小文字は区別しない。</b>
/// <c>SV_Target</c> と <c>SV_TARGET</c> はどちらも書かれる。
/// </para>
/// </remarks>
public sealed class BannedSemanticAnalyzer(params string[] banned) : HlslRuleAnalyzer
{
    private readonly ImmutableHashSet<string> _banned = banned.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.BannedSemantic];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<SemanticSyntax>(c =>
        {
            if (c.Node.HasArguments || !_banned.Contains(c.Node.Name))
            {
                return;
            }

            if (!c.Compilation.IsReportable(c.Node.NameToken))
            {
                return;
            }

            c.ReportDiagnostic(CookbookRules.BannedSemantic, c.Node.NameToken.Span, c.Node.Name);
        });
    }
}
