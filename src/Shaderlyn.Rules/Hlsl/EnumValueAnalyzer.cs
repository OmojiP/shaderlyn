using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// enum の値の変換と演算子を検査する (HL0347、HLSL 2021)。
/// </summary>
/// <remarks>
/// <para>
/// 規則は DXC (<c>-HV 2021</c>) で確かめた (<see cref="EnumInfo"/>)。
/// 見るのは、変数の初期化・代入・return・関数の実引数と、演算子である。
/// </para>
/// <para>
/// <b>値の型が分かったときだけ見る。</b>型が分からない値 (構造体のメンバーの型が引けないなど) は見ない。
/// 数のリテラル (<c>1</c>) は数として扱う。DXC も <c>Mode m = 1;</c> を拒む。
/// </para>
/// </remarks>
internal sealed class EnumValueAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [HlslRuleDescriptors.EnumValueMismatch];

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

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            switch (node)
            {
                case VariableDeclarationSyntax declaration:
                    foreach (VariableDeclaratorSyntax variable in declaration.Variables)
                    {
                        if (!variable.IsArray && variable.Initializer is { } initializer and not InitializerListExpressionSyntax)
                        {
                            CheckConversion(context, binder, declaration.Type.Name, initializer, $"変数 '{variable.Name}'", reported);
                        }
                    }

                    break;

                case AssignmentExpressionSyntax { OperatorToken.Kind: HlslSyntaxKind.EqualsToken } assignment
                    when binder.GetEvaluatorFor(assignment).Evaluate(assignment.Left) is { } target:
                    CheckConversion(context, binder, target, assignment.Right, "代入先", reported);
                    break;

                case AssignmentExpressionSyntax compound:
                    CheckOperands(context, binder, compound, compound.OperatorToken, [compound.Left, compound.Right], reported);
                    break;

                case ReturnStatementSyntax { Expression: { } value } statement
                    when statement.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is { } function:
                    CheckConversion(context, binder, function.ReturnType.Name, value, $"関数 '{function.Name}' の戻り値", reported);
                    break;

                case InvocationExpressionSyntax { TargetName: { } name, HasTypeArguments: false } invocation:
                    CheckArguments(context, compilation, binder, invocation, name, reported);
                    break;

                case BinaryExpressionSyntax { OperatorToken.Kind: not HlslSyntaxKind.CommaToken } binary:
                    CheckOperands(context, binder, binary, binary.OperatorToken, [binary.Left, binary.Right], reported);
                    break;

                case PrefixUnaryExpressionSyntax unary:
                    CheckOperands(context, binder, unary, unary.OperatorToken, [unary.Operand], reported);
                    break;

                case PostfixUnaryExpressionSyntax postfix:
                    CheckOperands(context, binder, postfix, postfix.OperatorToken, [postfix.Operand], reported);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>値を、その型の入れ物へ入れられるかを検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="target">入れ物の型名。</param>
    /// <param name="value">入れる値。</param>
    /// <param name="subject">入れ物を表す言葉 (<c>変数 'm'</c> など)。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void CheckConversion(
        SyntaxTreeAnalysisContext context,
        ExpressionTypeBinder binder,
        string target,
        HlslExpressionSyntax value,
        string subject,
        HashSet<int> reported)
    {
        if (binder.GetEvaluatorFor(value).Evaluate(value) is not { } valueType || valueType == target)
        {
            return;
        }

        bool valueIsNumeric = HlslTypeClassifier.TryDescribeNumeric(valueType, out _);
        bool valueIsEnum = binder.TryGetEnum(valueType, out EnumInfo? valueEnum);

        // enum の入れ物には、同じ enum の値しか入らない。
        if (binder.TryGetEnum(target, out EnumInfo? targetEnum))
        {
            if (valueIsNumeric || valueIsEnum)
            {
                Report(context, value, $"{subject} は {DescribeEnum(targetEnum)} ですが、{valueType} の値は入れられません。型変換 (({target}) x) を書いてください。", reported);
            }

            return;
        }

        // enum class の値は、数へ暗黙に変換されない。
        if (valueEnum is { IsScoped: true } && HlslTypeClassifier.TryDescribeNumeric(target, out _))
        {
            Report(context, value, $"{subject} は {target} ですが、{DescribeEnum(valueEnum)} の値は数へ暗黙に変換されません。型変換 (({target}) x) を書いてください。", reported);
        }
    }

    /// <summary>関数の実引数を検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="invocation">呼び出し。</param>
    /// <param name="name">関数の名前。</param>
    /// <param name="reported">報告済みの位置。</param>
    /// <remarks>
    /// 多重定義を選ぶ規則は扱わないので、宣言がどれも同じ仮引数の型を持つ (プロトタイプと定義だけの) 関数に限る。
    /// <c>out</c> / <c>inout</c> の仮引数は書き戻しの向きもあるので見ない。
    /// </remarks>
    private static void CheckArguments(
        SyntaxTreeAnalysisContext context,
        ShaderCompilation compilation,
        ExpressionTypeBinder binder,
        InvocationExpressionSyntax invocation,
        string name,
        HashSet<int> reported)
    {
        ImmutableArray<FunctionDeclarationSyntax> declarations = compilation.GetFunctionDeclarations(name, binder.Program);

        if (declarations.IsEmpty)
        {
            return;
        }

        string[] shape = [.. declarations[0].ParameterList.Select(p => p.Type.Name)];

        if (declarations.Any(d => !d.ParameterList.Select(p => p.Type.Name).SequenceEqual(shape)))
        {
            return;
        }

        ParameterSyntax[] parameters = [.. declarations[0].ParameterList];
        HlslExpressionSyntax[] arguments = [.. invocation.ArgumentExpressions];

        for (int i = 0; i < parameters.Length && i < arguments.Length; i++)
        {
            ParameterSyntax parameter = parameters[i];

            if (parameter.IsArray || parameter.ModifierTokens.Any(m => m.Text is "out" or "inout"))
            {
                continue;
            }

            CheckConversion(context, binder, parameter.Type.Name, arguments[i], $"関数 '{name}' の第 {i + 1} 引数", reported);
        }
    }

    /// <summary>演算子の被演算子に enum の値があれば、その演算子が使えるかを検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="expression">演算の式。</param>
    /// <param name="operatorToken">演算子。</param>
    /// <param name="operands">被演算子。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void CheckOperands(
        SyntaxTreeAnalysisContext context,
        ExpressionTypeBinder binder,
        HlslExpressionSyntax expression,
        HlslSyntaxToken operatorToken,
        HlslExpressionSyntax[] operands,
        HashSet<int> reported)
    {
        ExpressionTypeEvaluator evaluator = binder.GetEvaluatorFor(expression);
        string?[] types = [.. operands.Select(evaluator.Evaluate)];
        string symbol = operatorToken.Text;

        for (int i = 0; i < operands.Length; i++)
        {
            if (types[i] is not { } type || !binder.TryGetEnum(type, out EnumInfo? enumeration))
            {
                continue;
            }

            // ふつうの enum の値は数として使える。増減だけは使えない。
            if (!enumeration.IsScoped)
            {
                if (symbol is "++" or "--")
                {
                    Report(context, expression, $"{DescribeEnum(enumeration)} の値に '{symbol}' は使えません。", reported);
                }

                continue;
            }

            // enum class は、同じ enum どうしの == / != だけが使える。
            if (symbol is "==" or "!=" && operands.Length == 2)
            {
                string? other = types[1 - i];

                if (other is null || other == type || !(HlslTypeClassifier.TryDescribeNumeric(other, out _) || binder.TryGetEnum(other, out _)))
                {
                    return;
                }

                Report(context, expression, $"{DescribeEnum(enumeration)} の値と {other} の値は比べられません。同じ enum の値どうしでだけ比べられます。", reported);
                return;
            }

            Report(context, expression, $"{DescribeEnum(enumeration)} の値に '{symbol}' は使えません。数として使うなら型変換 ((int) x) を書いてください。", reported);
            return;
        }
    }

    /// <summary>enum の型を、画面に出す形にする。</summary>
    /// <param name="enumeration">enum の型。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string DescribeEnum(EnumInfo enumeration)
        => enumeration.IsScoped ? $"enum class '{enumeration.Name}'" : $"enum '{enumeration.Name}'";

    /// <summary>報告する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="node">報告する位置のノード。</param>
    /// <param name="message">文。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void Report(SyntaxTreeAnalysisContext context, HlslSyntaxNode node, string message, HashSet<int> reported)
    {
        if (node.GetLocation() is not { } location || !reported.Add(location.Span.Start))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.EnumValueMismatch, location, message));
    }
}
