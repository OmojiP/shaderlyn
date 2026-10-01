using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 関数の長さを制限する。
/// </summary>
/// <remarks>
/// <para>
/// <b>自分で書いた文だけを数える。</b>
/// マクロが展開した文まで数えると、
/// ヘッダのマクロを 1 行書いただけの関数が長い関数として報告される。
/// </para>
/// <para>
/// <b>構成ごとに数える。</b>
/// <c>#ifdef _A</c> と <c>#else</c> の文は、両方の分岐を並べた木には並んでいるが、同時には存在しない。
/// 木の文をそのまま数えると、どの構成でも起きない長さを報告する
/// (<see cref="Semantics.ShaderCompilation.TryEnumerateConfigurations"/>)。
/// </para>
/// </remarks>
public sealed class FunctionLengthLimitAnalyzer(int maxStatements) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.FunctionTooLong];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<FunctionDeclarationSyntax>(c =>
        {
            if (c.Node.Body is not { } body || !c.Compilation.IsReportable(c.Node.NameToken))
            {
                return;
            }

            // 波括弧そのものは数えない。中身がいくつあるかを数えたいため。
            List<HlslStatementSyntax> written =
            [
                .. body.DescendantNodesAndSelf()
                    .OfType<HlslStatementSyntax>()
                    .Where(s => s is not BlockStatementSyntax && c.Compilation.IsReportable(s)),
            ];

            // 条件が分からない文があれば数えない。分からないことを誤りにしてはならない。
            if (!c.Compilation.TryEnumerateConfigurations(written, c.Program, out var configurations)
                || configurations.IsEmpty)
            {
                return;
            }

            int statements = configurations.Max(configuration => written.Count(configuration.Contains));

            if (statements > maxStatements)
            {
                c.ReportDiagnostic(
                    CookbookRules.FunctionTooLong,
                    c.Node.NameToken.Span,
                    c.Node.Name,
                    statements,
                    maxStatements);
            }
        });
    }
}
