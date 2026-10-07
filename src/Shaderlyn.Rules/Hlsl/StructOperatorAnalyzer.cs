using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// 構造体の値に使えない演算子と、多重定義できない演算子の定義を検査する (HL0346)。
/// </summary>
/// <remarks>
/// <para>
/// 規則は DXC (<c>-HV 2021</c>) で確かめた。演算子は数値型の値にしか使えない。
/// HLSL 2021 では、構造体の中で定義した演算子 (<c>S operator+(S o)</c>) を、その構造体を<b>左辺</b>にして使える。
/// 右辺だけが構造体の演算 (<c>2 * s</c>) は使えない。<c>!=</c> は <c>==</c> から導かれない。
/// </para>
/// <para>
/// 条件演算子 (<c>c ? a : b</c>) は、演算子を定義していても構造体の値を選べない。
/// <c>++</c> / <c>--</c> / 複合代入 (<c>+=</c> など) / <c>=</c> / <c>&lt;&lt;</c> / <c>&gt;&gt;</c> / <c>-&gt;</c> は多重定義できない。
/// </para>
/// <para>
/// 単項の <c>+ - ! ~</c> は、DXC 1.6 では定義していても使えなかった。新しい DXC で使えるかもしれないので、
/// 定義があれば報告しない。
/// </para>
/// <para>
/// <b>型が構造体だと分かった値だけを見る。</b>型が分からない値 (template の型引数 <c>T</c> など) は見ない。
/// </para>
/// </remarks>
internal sealed class StructOperatorAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>多重定義できない演算子。</summary>
    private static readonly FrozenSet<string> NotOverloadable = new[]
    {
        "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", "++", "--", "<<", ">>", "->",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslRuleDescriptors.StructOperatorNotAvailable];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 同じコードが Pass ごと・構成ごとに解析されるため、同じ位置を重ねて報告しない。
        HashSet<int> reported = [];

        foreach ((HlslSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (!compilation.IsReportable(node))
            {
                continue;
            }

            if (node is FunctionDeclarationSyntax { IsOperator: true } declaration)
            {
                AnalyzeDeclaration(context, declaration, reported);
                continue;
            }

            if (node is HlslExpressionSyntax expression
                && Describe(compilation, compilation.GetExpressionTypeBinder(program), expression) is { } problem)
            {
                Report(context, problem.Token, problem.Message, reported);
            }
        }
    }

    /// <summary>多重定義できない演算子の定義を報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="declaration">演算子の定義。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void AnalyzeDeclaration(
        SyntaxTreeAnalysisContext context,
        FunctionDeclarationSyntax declaration,
        HashSet<int> reported)
    {
        string symbol = string.Concat(declaration.OperatorTokens.Select(t => t.Text));

        if (NotOverloadable.Contains(symbol))
        {
            Report(context, declaration.NameToken, $"operator{symbol} は多重定義できません。", reported);
        }
    }

    /// <summary>式 1 つについて、構造体に使えない演算子を使っていればその中身を返す。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="expression">対象の式。</param>
    /// <returns>報告する位置と文。問題が無ければ <see langword="null"/>。</returns>
    private static (HlslSyntaxToken Token, string Message)? Describe(
        ShaderCompilation compilation,
        ExpressionTypeBinder binder,
        HlslExpressionSyntax expression)
    {
        ExpressionTypeEvaluator? evaluator = null;

        string? StructOf(HlslExpressionSyntax operand)
        {
            evaluator ??= binder.GetEvaluatorFor(expression);
            return evaluator.Evaluate(operand) is { } type && binder.IsStructType(type) ? type : null;
        }

        switch (expression)
        {
            case BinaryExpressionSyntax { OperatorToken.Kind: not HlslSyntaxKind.CommaToken } binary:
            {
                string symbol = binary.OperatorToken.Text;

                // 構造体の外で定義した演算子 (S operator*(float k, S s))。DXC 1.6 は定義を受け付けるが、式には使わない。
                // 新しい DXC が使うかもしれないので、定義があれば報告しない。
                if (!compilation.GetFunctionDeclarations("operator" + symbol, binder.Program).IsEmpty)
                {
                    return null;
                }

                if (StructOf(binary.Left) is { } left)
                {
                    return HasOperator(binder, left, symbol, parameters: 1)
                        ? null
                        : (binary.OperatorToken, $"構造体 '{left}' の値に演算子 '{symbol}' は使えません。{HintFor(left, symbol)}");
                }

                return StructOf(binary.Right) is { } right
                    ? (binary.OperatorToken, $"構造体 '{right}' の値に演算子 '{symbol}' は使えません。構造体の中で定義した演算子は、左辺が構造体のときだけ使われます ({right} を左に書いてください)。")
                    : null;
            }

            // 構造体の増減 (a++ / ++a) は、FXC だけが受け付ける (成分ごとに増減する)。DXC は 2016 から拒む。
            // まだコンパイラを区別していないので、DXC だけが使う HLSL 2021 のときだけ報告する。
            case PrefixUnaryExpressionSyntax { OperatorToken.Kind: HlslSyntaxKind.PlusPlusToken or HlslSyntaxKind.MinusMinusToken }
                when binder.Program.Tree.LanguageVersion != HlslLanguageVersion.Hlsl2021:
                return null;

            case PostfixUnaryExpressionSyntax when binder.Program.Tree.LanguageVersion != HlslLanguageVersion.Hlsl2021:
                return null;

            case PrefixUnaryExpressionSyntax unary:
            {
                string symbol = unary.OperatorToken.Text;

                return StructOf(unary.Operand) is { } operand
                       && !HasOperator(binder, operand, symbol, parameters: 0)
                    ? (unary.OperatorToken, $"構造体 '{operand}' の値に演算子 '{symbol}' は使えません。{HintFor(operand, symbol)}")
                    : null;
            }

            case PostfixUnaryExpressionSyntax postfix:
            {
                string symbol = postfix.OperatorToken.Text;

                return StructOf(postfix.Operand) is { } operand
                    ? (postfix.OperatorToken, $"構造体 '{operand}' の値に演算子 '{symbol}' は使えません。{HintFor(operand, symbol)}")
                    : null;
            }

            // 複合代入 (+= など) は多重定義できない。ふつうの代入 (=) は構造体どうしでも使える。
            case AssignmentExpressionSyntax { OperatorToken.Kind: not HlslSyntaxKind.EqualsToken } assignment:
            {
                string symbol = assignment.OperatorToken.Text;

                return (StructOf(assignment.Left) ?? StructOf(assignment.Right)) is { } operand
                    ? (assignment.OperatorToken, $"構造体 '{operand}' の値に演算子 '{symbol}' は使えません。{HintFor(operand, symbol)}")
                    : null;
            }

            case ConditionalExpressionSyntax conditional:
                return (StructOf(conditional.WhenTrue) ?? StructOf(conditional.WhenFalse)) is { } chosen
                    ? (conditional.QuestionToken, $"条件演算子 (?:) で構造体 '{chosen}' の値は選べません。数値型の値しか選べないので、if 文で書いてください。")
                    : null;

            default:
                return null;
        }
    }

    /// <summary>構造体がその演算子を定義しているかを調べる。</summary>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="structName">構造体の名前。</param>
    /// <param name="symbol">演算子の記号。</param>
    /// <param name="parameters">仮引数の個数 (二項なら 1、単項なら 0)。</param>
    /// <returns>定義していれば <see langword="true"/>。</returns>
    private static bool HasOperator(ExpressionTypeBinder binder, string structName, string symbol, int parameters)
        => binder.TryGetStructFunctions(structName, "operator" + symbol, out ImmutableArray<FunctionDeclarationSyntax> overloads)
           && overloads.Any(o => CallCompatibility.AcceptsArgumentCount(o.ParameterList, parameters));

    /// <summary>直し方を添える。</summary>
    /// <param name="structName">構造体の名前。</param>
    /// <param name="symbol">演算子の記号。</param>
    /// <returns>添える文。</returns>
    private static string HintFor(string structName, string symbol)
        => NotOverloadable.Contains(symbol)
            ? $"'{symbol}' は多重定義できないので、メンバーごとに計算してください。"
            : $"HLSL 2021 では、'{structName}' の中で operator{symbol} を定義すると使えます。";

    /// <summary>報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="token">報告する位置のトークン。</param>
    /// <param name="message">文。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void Report(SyntaxTreeAnalysisContext context, HlslSyntaxToken token, string message, HashSet<int> reported)
    {
        if (token.GetLocation() is not { } location || !reported.Add(location.Span.Start))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.StructOperatorNotAvailable, location, message));
    }
}
