using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
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
/// <para>コードをコンパイルする方言がどれも 2021 のときだけ見る。FXC と DXC 2021 より前は成分ごとに計算する。</para>
/// </remarks>
internal sealed class VectorLogicalOperatorAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.VectorLogicalOperator];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        ReportedLocations reported = new();

        foreach ((HlslSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (program.Tree.Features.VectorLogicalOperators || !compilation.IsReportable(node))
            {
                continue;
            }

            (ShaderSyntaxToken Token, string Message)? problem = node switch
            {
                BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken } binary
                    when IsNonScalar(compilation, program, binary, binary.Left) || IsNonScalar(compilation, program, binary, binary.Right)
                    => (binary.OperatorToken,
                        $"HLSL 2021 では '{binary.OperatorToken.Text}' はベクトルには使用できません。成分ごとに計算するなら "
                        + $"{(binary.OperatorToken.Kind == ShaderSyntaxKind.AmpersandAmpersandToken ? "and" : "or")}() を使ってください。"),
                ConditionalExpressionSyntax conditional
                    when IsNonScalar(compilation, program, conditional, conditional.Condition)
                    => (conditional.QuestionToken,
                        "HLSL 2021 では条件演算子 (?:) の条件はベクトルには使用できません。成分ごとに選ぶなら select() を使ってください。"),
                _ => null,
            };

            if (problem is { } found
                && found.Token.GetLocation() is { } location
                && reported.Add(location))
            {
                context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.VectorLogicalOperator, location, found.Message));
            }
        }
    }

    /// <summary>値の型がベクトルか行列だと分かるか。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">式が属する木。</param>
    /// <param name="expression">式。</param>
    /// <param name="operand">値。</param>
    /// <returns>分かれば <see langword="true"/>。型が分からなければ <see langword="false"/>。</returns>
    private static bool IsNonScalar(ShaderCompilation compilation, AnalyzedProgram program, HlslExpressionSyntax expression, HlslExpressionSyntax operand)
    {
        ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
        string? type = binder.GetEvaluatorFor(expression).Evaluate(operand);

        // 1 成分のベクトル (float1) も、DXC はスカラーとして扱わない。
        return HlslTypeClassifier.Classify(type) is HlslTypeClass.Vector or HlslTypeClass.Matrix
               || type is [.., >= 'a' and <= 'z', '1'];
    }
}
