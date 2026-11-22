using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// HLSL 2021 で、ベクトルや行列に <c>&amp;&amp;</c> / <c>||</c> / <c>?:</c> を使っている式を報告する (HL0349)。
/// </summary>
/// <remarks>
/// <para>
/// HLSL 2021 の <c>&amp;&amp;</c> / <c>||</c> / <c>?:</c> は C++ と同じく短絡評価になり、スカラーにしか使えない
/// (<c>operands for short-circuiting logical binary operator must be scalar, for non-scalar types use 'and'</c>)。
/// 成分ごとの計算は <c>and()</c> / <c>or()</c> / <c>select()</c> で書く。DXC 1.8 で確かめた。
/// </para>
/// <para>コードをコンパイルするコンパイラがどれも DXC の HLSL 2021 のときだけ見る。FXC と DXC 2021 より前は成分ごとに計算する。</para>
/// </remarks>
internal sealed class VectorLogicalOperatorAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.VectorLogicalOperator];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        ReportedLocations reported = new();

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (program.Tree.Hlsl().Features.VectorLogicalOperators || !compilation.IsReportable(node))
            {
                continue;
            }

            (ShaderSyntaxToken Token, string Message, DiagnosticFix? Fix)? problem = node switch
            {
                BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken } binary
                    when IsNonScalar(compilation, program, binary, binary.Left) || IsNonScalar(compilation, program, binary, binary.Right)
                    => (binary.OperatorToken,
                        $"HLSL 2021 では '{binary.OperatorToken.Text}' はベクトルには使用できません。成分ごとに計算する場合は "
                        + $"{(binary.OperatorToken.Kind == ShaderSyntaxKind.AmpersandAmpersandToken ? "and" : "or")}() を使用してください。",
                        CallFix(binary.OperatorToken.Kind == ShaderSyntaxKind.AmpersandAmpersandToken ? "and" : "or", [binary.Left, binary.Right], [binary.OperatorToken])),
                ConditionalExpressionSyntax conditional
                    when IsNonScalar(compilation, program, conditional, conditional.Condition)
                    => (conditional.QuestionToken,
                        "HLSL 2021 では条件演算子 (?:) の条件はベクトルには使用できません。成分ごとに選ぶ場合は select() を使用してください。",
                        CallFix("select", [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse], [conditional.QuestionToken, conditional.ColonToken])),
                _ => null,
            };

            if (problem is { } found
                && found.Token.GetLocation() is { } location
                && reported.Add(location))
            {
                Diagnostic diagnostic = Diagnostic.Create(HlslRuleDescriptors.VectorLogicalOperator, location, found.Message);
                context.ReportDiagnostic(found.Fix is { } fix && ReferenceEquals(found.Token.Source, location.Source) ? diagnostic.WithFix(fix) : diagnostic);
            }
        }
    }

    /// <summary>
    /// 演算子の式を関数の呼び出しに書き換える修正を作る (<c>a &amp;&amp; b</c> → <c>and(a, b)</c>、<c>c ? a : b</c> → <c>select(c, a, b)</c>)。
    /// </summary>
    /// <param name="function">呼び出す関数の名前。</param>
    /// <param name="operands">被演算子。この順に実引数になる。</param>
    /// <param name="operators">被演算子の間の演算子 (被演算子より 1 つ少ない)。</param>
    /// <returns>修正。書き換えられない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 被演算子の文字はそのまま残し、前後に関数の名前と括弧を入れ、演算子 (と前後の空白) を <c>, </c> に置き換える。
    /// 括弧で包むので、演算子の優先順位は変わらない。
    /// 書き換える位置がマクロの展開から来たトークンか、別のファイルにある場合と、演算子の前後にコメントがある場合 (消してしまう) は、修正を添えない。
    /// </remarks>
    private static DiagnosticFix? CallFix(string function, ExpressionSyntax[] operands, ShaderSyntaxToken[] operators)
    {
        ShaderSyntaxToken? first = operands[0].FirstToken;
        ShaderSyntaxToken? last = operands[^1].DescendantTokens().LastOrDefault();

        if (!TokenFixes.CanEdit(first) || !TokenFixes.CanEdit(last) || first!.Source is not { } source)
        {
            return null;
        }

        ImmutableArray<TextReplacement>.Builder replacements = ImmutableArray.CreateBuilder<TextReplacement>();
        replacements.Add(new TextReplacement(new TextSpan(first.Span.Start, 0), function + "("));

        for (int i = 0; i < operators.Length; i++)
        {
            ShaderSyntaxToken? before = operands[i].DescendantTokens().LastOrDefault();
            ShaderSyntaxToken? after = operands[i + 1].FirstToken;

            if (!TokenFixes.CanEdit(before) || !TokenFixes.CanEdit(after) || !TokenFixes.CanEdit(operators[i])
                || !ReferenceEquals(before!.Source, source) || !ReferenceEquals(after!.Source, source) || !ReferenceEquals(operators[i].Source, source))
            {
                return null;
            }

            TextSpan gap = TextSpan.FromBounds(before.Span.End, after.Span.Start);

            if (source.ToString(gap).Trim() != operators[i].Text)
            {
                return null;
            }

            replacements.Add(new TextReplacement(gap, ", "));
        }

        if (!ReferenceEquals(last!.Source, source))
        {
            return null;
        }

        replacements.Add(new TextReplacement(new TextSpan(last.Span.End, 0), ")"));
        return new DiagnosticFix($"{function}() に書き換える", replacements.ToImmutable(), IsPreferred: true);
    }

    /// <summary>値の型がベクトルか行列だと分かるか。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">式が属する木。</param>
    /// <param name="expression">式。</param>
    /// <param name="operand">値。</param>
    /// <returns>分かれば <see langword="true"/>。型が分からなければ <see langword="false"/>。</returns>
    private static bool IsNonScalar(ShaderCompilation compilation, AnalyzedProgram program, ExpressionSyntax expression, ExpressionSyntax operand)
    {
        ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
        string? type = binder.GetEvaluatorFor(expression).Evaluate(operand);

        // 1 成分のベクトル (float1) も、DXC はスカラーとして扱わない。
        return compilation.TypeSystem.Classify(type) is ShaderTypeClass.Vector or ShaderTypeClass.Matrix
               || type is [.., >= 'a' and <= 'z', '1'];
    }
}
