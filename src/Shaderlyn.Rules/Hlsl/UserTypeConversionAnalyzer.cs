using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules;

/// <summary>
/// 利用者が定義した型 (enum と構造体) の値の変換と、enum への演算子を検査する
/// (HL0347、構造体は HL0350 / HL0340 / HL0351、HLSL 2021)。
/// </summary>
/// <remarks>
/// <para>
/// 規則は DXC (<c>-HV 2021</c>) で確かめた。見るのは、変数の初期化・代入・return・関数の実引数と、enum への演算子である。
/// </para>
/// <para>
/// enum の型へ入れられるのは同じ enum の値だけである (<see cref="EnumInfo"/>)。
/// 構造体へ入れられるのは同じ構造体か、そこから派生した構造体の値だけで、構造体と数の間も暗黙には変換されない。
/// </para>
/// <para>
/// <b>構造体の検査は HLSL 2021 のときだけ行う。</b>
/// Unity が使う FXC (HLSL 2018 相当) の変換の規則は確かめていない。
/// template の構造体は、型引数が違えば別の型だが名前だけでは区別できないので見ない。
/// </para>
/// <para>
/// <b>値の型が分かったときだけ見る。</b>数のリテラル (<c>1</c>) は数として扱う。DXC も <c>Mode m = 1;</c> を拒む。
/// </para>
/// </remarks>
internal sealed class UserTypeConversionAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        HlslRuleDescriptors.EnumValueMismatch,
        HlslRuleDescriptors.ValueNotConvertible,
        HlslRuleDescriptors.ArgumentNotConvertible,
        HlslRuleDescriptors.ReturnNotConvertible,
    ];

    /// <summary>値を受け取る側。</summary>
    /// <param name="Kind">受け取り方。</param>
    /// <param name="TypeName">受け取る側の型名。</param>
    /// <param name="Label">受け取る側を表す言葉 (<c>変数 'm'</c> など)。</param>
    /// <param name="FunctionName">関数の名前 (実引数と return のとき)。</param>
    /// <param name="Index">実引数の位置 (1 から)。</param>
    private readonly record struct Receiver(ReceiverKind Kind, string TypeName, string Label, string? FunctionName = null, int Index = 0)
    {
        /// <summary>初期化する変数の名前。初期化でなければ <see langword="null"/>。</summary>
        public string? VariableName { get; init; }
    }

    /// <summary>値の受け取り方。</summary>
    private enum ReceiverKind
    {
        /// <summary>変数の初期化。</summary>
        Initializer,

        /// <summary>代入。</summary>
        Assignment,

        /// <summary>関数の実引数。</summary>
        Argument,

        /// <summary>return の値。</summary>
        Return,
    }

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
                            Receiver receiver = new(ReceiverKind.Initializer, declaration.Type.Name, $"{declaration.Type.Name} の '{variable.Name}'") { VariableName = variable.Name };
                            CheckConversion(context, binder, receiver, initializer, reported);
                        }
                    }

                    break;

                case AssignmentExpressionSyntax { OperatorToken.Kind: HlslSyntaxKind.EqualsToken } assignment
                    when binder.GetEvaluatorFor(assignment).Evaluate(assignment.Left) is { } target:
                    CheckConversion(context, binder, new Receiver(ReceiverKind.Assignment, target, target), assignment.Right, reported);
                    break;

                case AssignmentExpressionSyntax compound:
                    CheckOperands(context, binder, compound, compound.OperatorToken, [compound.Left, compound.Right], reported);
                    break;

                case ReturnStatementSyntax { Expression: { } value } statement
                    when statement.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is { } function:
                    Receiver returned = new(ReceiverKind.Return, function.ReturnType.Name, $"関数 '{function.Name}' の戻り値", function.Name);
                    CheckConversion(context, binder, returned, value, reported);
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

    /// <summary>値を、受け取る側の型へ暗黙に変換できるかを検査する。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="receiver">受け取る側。</param>
    /// <param name="value">渡す値。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void CheckConversion(
        SyntaxTreeAnalysisContext context,
        ExpressionTypeBinder binder,
        Receiver receiver,
        HlslExpressionSyntax value,
        HashSet<int> reported)
    {
        string target = receiver.TypeName;

        if (binder.GetEvaluatorFor(value).Evaluate(value) is not { } valueType || valueType == target)
        {
            return;
        }

        bool valueIsNumeric = HlslTypeClassifier.TryDescribeNumeric(valueType, out _);
        bool targetIsNumeric = HlslTypeClassifier.TryDescribeNumeric(target, out _);
        bool valueIsEnum = binder.TryGetEnum(valueType, out EnumInfo? valueEnum);

        // enum の受け取る側には、同じ enum の値しか入らない。
        if (binder.TryGetEnum(target, out EnumInfo? targetEnum))
        {
            if (valueIsNumeric || valueIsEnum)
            {
                ReportEnum(context, value, $"{DescribeReceiver(receiver, DescribeEnum(targetEnum))} に {valueType} は{DescribeVerb(receiver)}。型変換 (({target}) x) を書いてください。", reported);
            }

            return;
        }

        // enum class の値は、数へ暗黙に変換されない。
        if (valueEnum is { IsScoped: true } && targetIsNumeric)
        {
            ReportEnum(context, value, $"{DescribeReceiver(receiver, target)} に {DescribeEnum(valueEnum)} の値は{DescribeVerb(receiver)}。enum class の値は数へ暗黙に変換されません。型変換 (({target}) x) を書いてください。", reported);
            return;
        }

        if (!binder.Program.Tree.Features.ImplicitUserTypeConversion
            && DescribeStructMismatch(binder, target, targetIsNumeric, valueType, valueIsNumeric) is { } reason)
        {
            ReportStruct(context, receiver, value, valueType, reason, reported);
        }
    }

    /// <summary>構造体が関わる変換が成り立たない理由を返す (HLSL 2021)。</summary>
    /// <param name="binder">式の型を引く仕組み。</param>
    /// <param name="target">受け取る側の型。</param>
    /// <param name="targetIsNumeric">受け取る側が数値型か。</param>
    /// <param name="value">値の型。</param>
    /// <param name="valueIsNumeric">値が数値型か。</param>
    /// <returns>成り立たない理由。成り立つか、判断しないなら <see langword="null"/>。</returns>
    private static string? DescribeStructMismatch(
        ExpressionTypeBinder binder,
        string target,
        bool targetIsNumeric,
        string value,
        bool valueIsNumeric)
    {
        bool targetIsStruct = binder.IsStructType(target);
        bool valueIsStruct = binder.IsStructType(value);

        if ((targetIsStruct && binder.IsTemplateStruct(target)) || (valueIsStruct && binder.IsTemplateStruct(value)))
        {
            return null;
        }

        if (targetIsStruct && valueIsStruct)
        {
            // 派生した構造体の値は、基底の構造体へ入る (基底の部分だけが残る)。
            return binder.IsDerivedFrom(value, target) is false
                ? $"構造体へは同じ構造体か、そこから派生した構造体の値しか入りません。型変換 (({target}) x) を書いてください。"
                : null;
        }

        if (targetIsStruct && valueIsNumeric)
        {
            return $"数は構造体へ暗黙に変換されません。型変換 (({target}) x) を書くか、メンバーごとに入れてください。";
        }

        return valueIsStruct && targetIsNumeric
            ? "構造体は数へ暗黙に変換されません。メンバーを取り出してください。"
            : null;
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
        // 大域の関数の呼び出しでなければ見ない。取り込みを解決できていなければ、見えていない多重定義があるかもしれない
        // (CallSignatureAnalyzer と同じ)。
        if (!compilation.HasCompleteDependencies || binder.CallsNonGlobalFunction(invocation, name))
        {
            return;
        }

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

            Receiver receiver = new(ReceiverKind.Argument, parameter.Type.Name, $"関数 '{name}' の第 {i + 1} 引数", name, i + 1);
            CheckConversion(context, binder, receiver, arguments[i], reported);
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
                    ReportEnum(context, expression, $"{DescribeEnum(enumeration)} の値に '{symbol}' は使えません。", reported);
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

                ReportEnum(context, expression, $"{DescribeEnum(enumeration)} の値と {other} の値は比べられません。同じ enum の値どうしでだけ比べられます。", reported);
                return;
            }

            ReportEnum(context, expression, $"{DescribeEnum(enumeration)} の値に '{symbol}' は使えません。数として使うなら型変換 ((int) x) を書いてください。", reported);
            return;
        }
    }

    /// <summary>enum の型を、画面に出す形にする。</summary>
    /// <param name="enumeration">enum の型。</param>
    /// <returns>画面に出す文字列。</returns>
    private static string DescribeEnum(EnumInfo enumeration)
        => enumeration.IsScoped ? $"enum class '{enumeration.Name}'" : $"enum '{enumeration.Name}'";

    /// <summary>受け取る側を、型を添えて言う (<c>enum class 'Mode' の 'm'</c> / <c>関数 'f' の第 1 引数 (int)</c>)。</summary>
    /// <param name="receiver">受け取る側。</param>
    /// <param name="type">受け取る側の型の言い方。</param>
    /// <returns>文に入れる言葉。</returns>
    private static string DescribeReceiver(Receiver receiver, string type) => receiver.Kind switch
    {
        ReceiverKind.Initializer when receiver.VariableName is { } name => $"{type} の '{name}'",
        ReceiverKind.Argument or ReceiverKind.Return => $"{receiver.Label} ({type})",
        _ => type,
    };

    /// <summary>受け取り方に合う「できない」の言い方。</summary>
    /// <param name="receiver">受け取る側。</param>
    /// <returns>文に入れる言葉。</returns>
    private static string DescribeVerb(Receiver receiver) => receiver.Kind switch
    {
        ReceiverKind.Argument => "指定できません",
        ReceiverKind.Return => "返せません",
        _ => "代入できません",
    };

    /// <summary>enum の食い違いを報告する (HL0347)。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="node">報告する位置のノード。</param>
    /// <param name="message">文。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void ReportEnum(SyntaxTreeAnalysisContext context, HlslSyntaxNode node, string message, HashSet<int> reported)
    {
        if (node.GetLocation() is not { } location || !reported.Add(location.Span.Start))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.EnumValueMismatch, location, message));
    }

    /// <summary>構造体の食い違いを、受け取り方に合ったルールで報告する (HL0350 / HL0340 / HL0351)。</summary>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="receiver">受け取る側。</param>
    /// <param name="value">渡した値。</param>
    /// <param name="valueType">値の型。</param>
    /// <param name="reason">成り立たない理由。</param>
    /// <param name="reported">報告済みの位置。</param>
    private static void ReportStruct(
        SyntaxTreeAnalysisContext context,
        Receiver receiver,
        HlslExpressionSyntax value,
        string valueType,
        string reason,
        HashSet<int> reported)
    {
        if (value.GetLocation() is not { } location || !reported.Add(location.Span.Start))
        {
            return;
        }

        Diagnostic diagnostic = receiver.Kind switch
        {
            ReceiverKind.Argument => Diagnostic.Create(
                HlslRuleDescriptors.ArgumentNotConvertible, location, receiver.FunctionName, receiver.Index, valueType, receiver.TypeName, reason),
            ReceiverKind.Return => Diagnostic.Create(
                HlslRuleDescriptors.ReturnNotConvertible, location, receiver.FunctionName, receiver.TypeName, $"{valueType} の値を返しています", reason),
            _ => Diagnostic.Create(HlslRuleDescriptors.ValueNotConvertible, location, receiver.Label, valueType, reason),
        };

        context.ReportDiagnostic(diagnostic);
    }
}
