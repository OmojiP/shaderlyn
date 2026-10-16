using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
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
/// 単項の <c>+ - ! ~</c> は、DXC 1.6 / 1.8 では定義していても使えなかった。Unity の DXC 1.10 は確かめていないので、
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
        [HlslRuleDescriptors.StructOperatorNotAvailable, HlslRuleDescriptors.StructIncrementIgnored];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 同じコードが Pass ごと・構成ごとに解析されるため、同じ位置を重ねて報告しない。
        ReportedLocations reported = new();

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
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

            if (node is not ExpressionSyntax expression)
            {
                continue;
            }

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            if (IsIncrement(expression, out ShaderSyntaxToken? incrementToken, out ExpressionSyntax? operand))
            {
                if (DescribeIncrement(compilation, binder, expression, operand, incrementToken) is { } increment)
                {
                    Report(context, increment.Token, increment.Message, reported, increment.Descriptor);
                }

                continue;
            }

            if (Describe(compilation, binder, expression) is { } problem)
            {
                Report(context, problem.Token, problem.Message, reported);
            }
        }
    }

    /// <summary>多重定義できない演算子の定義と、構造体の外の演算子の定義を報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="declaration">演算子の定義。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// 構造体の外の演算子 (<c>S operator*(float k, S s)</c>) は、DXC 1.8 では定義そのものがエラー
    /// (<c>overloading non-member 'operator*' is not allowed</c>)。DXC 1.6 は定義だけ通すが、式には使わない。
    /// </remarks>
    private static void AnalyzeDeclaration(
        SyntaxTreeAnalysisContext context,
        FunctionDeclarationSyntax declaration,
        ReportedLocations reported)
    {
        string symbol = string.Concat(declaration.OperatorTokens.Select(t => t.Text));

        if (NotOverloadable.Contains(symbol))
        {
            Report(context, declaration.NameToken, $"operator{symbol} は多重定義できません。", reported);
            return;
        }

        if (!declaration.Ancestors().Any(a => a is StructDeclarationSyntax))
        {
            Report(context, declaration.NameToken, $"operator{symbol} は構造体の中でだけ定義できます。構造体の外の演算子は使えません。", reported);
        }
    }

    /// <summary>式 1 つについて、構造体に使えない演算子を使っていればその中身を返す。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="expression">対象の式。</param>
    /// <returns>報告する位置と文。問題が無ければ <see langword="null"/>。</returns>
    private static (ShaderSyntaxToken Token, string Message)? Describe(
        ShaderCompilation compilation,
        ExpressionTypeBinder binder,
        ExpressionSyntax expression)
    {
        ExpressionTypeEvaluator? evaluator = null;

        string? StructOf(ExpressionSyntax operand)
        {
            evaluator ??= binder.GetEvaluatorFor(expression);
            return evaluator.Evaluate(operand) is { } type && binder.IsStructType(type) ? type : null;
        }

        switch (expression)
        {
            case BinaryExpressionSyntax { OperatorToken.Kind: not ShaderSyntaxKind.CommaToken } binary:
            {
                string symbol = binary.OperatorToken.Text;

                if (StructOf(binary.Left) is { } left)
                {
                    return HasOperator(binder, left, symbol, parameters: 1)
                        ? null
                        : (binary.OperatorToken, $"構造体 '{left}' の値に演算子 '{symbol}' は使えません。{HintFor(left, symbol)}");
                }

                // 左辺の型が分からなければ、左辺の構造体の演算子が使われるかもしれない。
                // TLaneVector2D<float, 2>::Const(1.0) - a の左辺は、型を求めていない式である (HLSL 2021)。
                if (evaluator!.Evaluate(binary.Left) is null)
                {
                    return null;
                }

                return StructOf(binary.Right) is { } right
                    ? (binary.OperatorToken, $"構造体 '{right}' の値に演算子 '{symbol}' は使えません。構造体の中で定義した演算子は、左辺が構造体のときだけ使われます ({right} を左に書いてください)。")
                    : null;
            }

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
            case AssignmentExpressionSyntax { OperatorToken.Kind: not ShaderSyntaxKind.EqualsToken } assignment:
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

    /// <summary>増減 (<c>++a</c> / <c>a++</c> / <c>--a</c> / <c>a--</c>) かを判定する。</summary>
    /// <param name="expression">式。</param>
    /// <param name="token">演算子のトークン。</param>
    /// <param name="operand">増減する値。</param>
    /// <returns>増減なら <see langword="true"/>。</returns>
    private static bool IsIncrement(
        ExpressionSyntax expression,
        [NotNullWhen(true)] out ShaderSyntaxToken? token,
        [NotNullWhen(true)] out ExpressionSyntax? operand)
    {
        (token, operand) = expression switch
        {
            PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } prefix
                => (prefix.OperatorToken, prefix.Operand),
            PostfixUnaryExpressionSyntax postfix => (postfix.OperatorToken, postfix.Operand),
            _ => ((ShaderSyntaxToken?)null, (ExpressionSyntax?)null),
        };

        return token is not null && operand is not null;
    }

    /// <summary>
    /// 構造体の増減を、届くコンパイル先ごとに判定する。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="expression">増減の式。</param>
    /// <param name="operand">増減する値。</param>
    /// <param name="token">演算子のトークン。</param>
    /// <returns>報告する中身。構造体でないか、報告しなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>構造体の増減は、FXC だけが受け付けて何もしない。</b> DXC はどの版でも拒む (HL0346)。
    /// FXC だけでコンパイルされるなら、何も起きないことを HL0348 で警告する。
    /// </para>
    /// <para>
    /// コンパイル先が分かれば (Unity)、届くコンパイル先に DXC があるかで決め、その API を文に添える。
    /// 分からなければ、どれかの方言が受け付けるか (<see cref="HlslLanguageFeatures.StructIncrement"/>) で決める。
    /// </para>
    /// </remarks>
    private static (ShaderSyntaxToken Token, string Message, DiagnosticDescriptor Descriptor)? DescribeIncrement(
        ShaderCompilation compilation,
        ExpressionTypeBinder binder,
        ExpressionSyntax expression,
        ExpressionSyntax operand,
        ShaderSyntaxToken token)
    {
        if (binder.GetEvaluatorFor(expression).Evaluate(operand) is not { } type || !binder.IsStructType(type))
        {
            return null;
        }

        string symbol = token.Text;
        AnalyzedProgram program = binder.Program;

        if (program.Tree.CompileTargets.IsDefaultOrEmpty)
        {
            return program.Tree.Features.StructIncrement
                ? (token, IgnoredMessage(type, symbol), HlslRuleDescriptors.StructIncrementIgnored)
                : (token, $"構造体 '{type}' の値に演算子 '{symbol}' は使えません。{HintFor(type, symbol)}", HlslRuleDescriptors.StructOperatorNotAvailable);
        }

        HlslCompileTarget[] reaching = [.. CompileTargets.Reaching(compilation, expression, program)];
        string[] rejecting = [.. reaching.Where(t => !t.Dialect.Features.StructIncrement).Select(t => t.Subject).Distinct(StringComparer.Ordinal)];

        if (rejecting.Length > 0)
        {
            return (token, $"構造体 '{type}' の値に演算子 '{symbol}' は {string.Join("・", rejecting)} では使えません。{HintFor(type, symbol)}", HlslRuleDescriptors.StructOperatorNotAvailable);
        }

        return reaching.Length > 0 ? (token, IgnoredMessage(type, symbol), HlslRuleDescriptors.StructIncrementIgnored) : null;
    }

    /// <summary>FXC で何もしないことを伝える文。</summary>
    /// <param name="type">構造体の名前。</param>
    /// <param name="symbol">演算子の記号。</param>
    /// <returns>文。</returns>
    private static string IgnoredMessage(string type, string symbol)
        => $"構造体 '{type}' の値への '{symbol}' は、FXC ではエラーになりませんが無視されます。";

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
            ? $"'{symbol}' はオーバーロードできないので、メンバーごとに計算してください。"
            : "HLSL 2021 以降なら演算子オーバーロードを行ってください。";

    /// <summary>報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="token">報告する位置のトークン。</param>
    /// <param name="message">文。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <param name="descriptor">ルール。省略すると HL0346。</param>
    private static void Report(
        SyntaxTreeAnalysisContext context,
        ShaderSyntaxToken token,
        string message,
        ReportedLocations reported,
        DiagnosticDescriptor? descriptor = null)
    {
        if (token.GetLocation() is not { } location || !reported.Add(location))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(descriptor ?? HlslRuleDescriptors.StructOperatorNotAvailable, location, message));
    }
}
