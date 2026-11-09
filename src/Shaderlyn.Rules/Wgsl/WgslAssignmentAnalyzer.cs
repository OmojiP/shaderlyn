using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>
/// WGSL の代入・増減の書き込み先 (WG0309) と、<c>&amp;</c> のアドレスの取り先 (WG0330) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// 書き込めるのは、<c>var</c> の変数 (<c>var&lt;uniform&gt;</c> と、<c>read_write</c> でない <c>var&lt;storage&gt;</c> を除く) と、そのメンバー・要素・1 成分のスウィズルだけである。
/// <c>let</c>・<c>const</c>・<c>override</c>・仮引数・2 つ以上の成分のスウィズル・変数でない値 (演算の結果、呼び出しの結果) には書けない (naga 30 の「invalid left-hand side of assignment」と
/// 「doesn't relate to a valid destination for a store」)。ポインターを通した書き込み (<c>*p = 1.0</c>) は、ポインターの指す先が分からないので見ない。
/// </para>
/// <para>
/// ベクトルの成分 (<c>v.x</c>・<c>v[0]</c>・<c>m[0][0]</c>) のアドレスは取れない (「cannot take the address of a vector component」)。行列の列 (<c>m[0]</c>) は取れる。
/// </para>
/// </remarks>
internal sealed class WgslAssignmentAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesWgsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [WgslRuleDescriptors.NotWritable, WgslRuleDescriptors.NotAddressable];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsWgsl)
        {
            return;
        }

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            ExpressionSyntax? target = node switch
            {
                AssignmentExpressionSyntax { Left: not IncompleteExpressionSyntax } assignment => assignment.Left,
                PostfixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } increment => increment.Operand,
                _ => null,
            };

            if (target is not null)
            {
                if (compilation.IsReportable(target) && WhyNotWritable(compilation.GetExpressionTypeBinder(program), target) is { } reason
                    && target.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(WgslRuleDescriptors.NotWritable, location, Describe(target), reason));
                }

                continue;
            }

            if (node is PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.AmpersandToken } address
                && compilation.IsReportable(address)
                && IsVectorComponent(compilation.GetExpressionTypeBinder(program), address.Operand)
                && address.GetLocation() is { } addressLocation)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    WgslRuleDescriptors.NotAddressable, addressLocation, $"ベクトルの成分 {Describe(address.Operand)} のアドレスは取れません。"));
            }
        }
    }

    /// <summary>書き込めない理由。書き込めるか、分からなければ <see langword="null"/>。</summary>
    private static string? WhyNotWritable(ExpressionTypeBinder binder, ExpressionSyntax target)
    {
        ExpressionSyntax current = target;

        while (true)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case MemberAccessExpressionSyntax member:
                    if (member.Name.Length > 1 && IsVector(binder, member.Target))
                    {
                        return "WGSL ではスウィズルに代入できません。成分ごとに代入してください";
                    }

                    current = member.Target;
                    continue;

                case ElementAccessExpressionSyntax element:
                    current = element.Target;
                    continue;

                // *p = … は、ポインターの指す先が分からないので見ない。
                case PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.AsteriskToken }:
                    return null;

                case IdentifierExpressionSyntax identifier:
                    return WhyNotWritable(binder.ResolveName(identifier));

                case LiteralExpressionSyntax or BinaryExpressionSyntax or InvocationExpressionSyntax or PrefixUnaryExpressionSyntax:
                    return "変数でない値です";

                default:
                    return null;
            }
        }
    }

    /// <summary>名前が指す宣言に書き込めない理由。</summary>
    private static string? WhyNotWritable(NameResolution resolution)
    {
        if (resolution.Kind is not (DeclaredNameKind.Local or DeclaredNameKind.Global) || resolution.Candidates is not [{ } candidate])
        {
            return null;
        }

        switch (candidate.Declarator)
        {
            case ParameterSyntax:
                return "関数の仮引数は書き換えられません";

            case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration }:
                ImmutableArray<ShaderSyntaxToken> modifiers = declaration.ModifierTokens;
                int keyword = modifiers.IndexOf(modifiers.LastOrDefault(t => t.Text is "var" or "let" or "const" or "override")!);

                if (keyword < 0)
                {
                    return null;
                }

                string kind = modifiers[keyword].Text;

                if (kind is "let" or "const" or "override")
                {
                    return $"{kind} の値は書き換えられません。書き換えるなら var で宣言してください";
                }

                // var<uniform> と、read_write でない var<storage>。
                string[] space = [.. modifiers.Skip(keyword + 1).Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken).Select(t => t.Text)];

                return space switch
                {
                    ["uniform", ..] => "var<uniform> の変数は読み取りだけです",
                    ["storage"] or ["storage", "read"] => "読み取りだけの var<storage> の変数です。書き込むなら var<storage, read_write> にしてください",
                    _ => null,
                };

            default:
                return null;
        }
    }

    /// <summary>式がベクトルの成分 (スウィズルか添字) か。</summary>
    private static bool IsVectorComponent(ExpressionTypeBinder binder, ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsVectorComponent(binder, parenthesized.Expression),
        MemberAccessExpressionSyntax member => IsVector(binder, member.Target),
        ElementAccessExpressionSyntax element => IsVector(binder, element.Target),
        _ => false,
    };

    private static bool IsVector(ExpressionTypeBinder binder, ExpressionSyntax expression)
        => binder.GetEvaluatorFor(expression).Evaluate(expression) is { } type
           && binder.TypeSystem.TryDescribeNumeric(type, out ShaderNumericShape shape)
           && shape.Kind == ShaderNumericKind.Vector;

    private static string Describe(ExpressionSyntax expression)
        => $"'{string.Concat(expression.DescendantTokens().Where(t => !t.IsMissing).Select(t => t.Text))}'";
}
