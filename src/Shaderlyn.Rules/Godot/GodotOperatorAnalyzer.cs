using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の演算子の型 (GD0301) と、条件が bool か (GD0311) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// Godot 4.7.2 の <c>ShaderLanguage::_validate_operator</c> の型の組の表を写した。Godot には暗黙の変換が無いので、GLSL より狭い。
/// <list type="bullet">
/// <item>比較 (<c>&lt;</c> など) は、同じ型のスカラーの <c>int</c> / <c>uint</c> / <c>float</c> どうしだけ。<c>==</c> / <c>!=</c> は同じ型どうし</item>
/// <item><c>&amp;&amp;</c> / <c>||</c> / <c>!</c> は <c>bool</c> だけ</item>
/// <item>符号の反転と <c>++</c> / <c>--</c> は、整数と浮動小数点数のスカラーとベクトルだけ (行列と bool は不可)</item>
/// <item><c>+ - * /</c> は同じ型か、スカラーとそれを要素に持つベクトル・行列、<c>*</c> はベクトルと同じ大きさの行列 (順は問わない)</item>
/// <item><c>%</c>・シフト・ビット演算は整数の型だけ。スカラーとベクトルなら、スカラーは右</item>
/// <item>複合代入 (<c>+=</c> など) は、左と同じ型か、左のベクトル・行列の要素の型のスカラー</item>
/// </list>
/// 代入 (<c>=</c>) と初期化は CF0410 が見る。配列の値と、型の分からない式は検査しない。
/// </para>
/// </remarks>
internal sealed class GodotOperatorAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>Godot の <c>DataType</c> の並び (<c>_validate_operator</c> が型の大小で左右を入れ替える)。</summary>
    private static readonly ImmutableArray<string> TypeOrder =
    [
        "void", "bool", "bvec2", "bvec3", "bvec4", "int", "ivec2", "ivec3", "ivec4", "uint", "uvec2", "uvec3", "uvec4",
        "float", "vec2", "vec3", "vec4", "mat2", "mat3", "mat4",
    ];

    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GodotRuleDescriptors.WrongOperandTypes, GodotRuleDescriptors.BooleanExpected];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGodot)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];
        Dictionary<AnalyzedProgram, ExpressionTypeBinder> binders = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (!binders.TryGetValue(program, out ExpressionTypeBinder? binder))
            {
                binder = binders[program] = compilation.GetExpressionTypeBinder(program);
            }

            void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken token, string argument)
            {
                if (compilation.IsReportable(token)
                    && token.GetLocation() is { } location
                    && reported.Add((location.FilePath, location.Span.Start)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, argument));
                }
            }

            Check(node, binder, Report);
        }
    }

    private static void Check(ShaderSyntaxNode node, ExpressionTypeBinder binder, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        string? TypeOf(ExpressionSyntax expression)
            => !IsArrayValue(expression, binder) && binder.GetTypesByConfiguration(expression) is [{ Type: { } name }] ? name : null;

        void Condition(ExpressionSyntax condition)
        {
            if (TypeOf(condition) is { } type && type != "bool" && condition.FirstToken is { } first)
            {
                report(GodotRuleDescriptors.BooleanExpected, first, type);
            }
        }

        switch (node)
        {
            case BinaryExpressionSyntax binary when TypeOf(binary.Left) is { } left && TypeOf(binary.Right) is { } right:
                if (!IsValidBinary(binary.OperatorToken.Kind, left, right))
                {
                    report(GodotRuleDescriptors.WrongOperandTypes, binary.OperatorToken, $"演算子 '{binary.OperatorToken.Text}' は {left} と {right} の組に使用できません。");
                }

                break;

            case AssignmentExpressionSyntax assignment
                when assignment.OperatorToken.Kind != ShaderSyntaxKind.EqualsToken
                     && TypeOf(assignment.Left) is { } target && TypeOf(assignment.Right) is { } value:
                if (!IsValidCompoundAssignment(assignment.OperatorToken.Kind, target, value))
                {
                    report(GodotRuleDescriptors.WrongOperandTypes, assignment.OperatorToken, $"演算子 '{assignment.OperatorToken.Text}' は {target} と {value} の組に使用できません。");
                }

                break;

            case PrefixUnaryExpressionSyntax prefix when TypeOf(prefix.Operand) is { } operand:
                CheckUnary(prefix.OperatorToken, operand, report);
                break;

            case PostfixUnaryExpressionSyntax postfix when TypeOf(postfix.Operand) is { } operand:
                CheckUnary(postfix.OperatorToken, operand, report);
                break;

            case IfStatementSyntax statement:
                Condition(statement.Condition);
                break;

            case WhileStatementSyntax statement:
                Condition(statement.Condition);
                break;

            case DoWhileStatementSyntax statement:
                Condition(statement.Condition);
                break;

            case ForStatementSyntax { Condition: { } condition }:
                Condition(condition);
                break;

            case ConditionalExpressionSyntax conditional:
                Condition(conditional.Condition);

                // ?: の 2 つの枝は同じ型でなければならない。
                if (TypeOf(conditional.WhenTrue) is { } whenTrue && TypeOf(conditional.WhenFalse) is { } whenFalse && whenTrue != whenFalse)
                {
                    report(GodotRuleDescriptors.WrongOperandTypes, conditional.ColonToken, $"?: の 2 つの枝の型が違います ({whenTrue} と {whenFalse})。");
                }

                break;
        }
    }

    /// <summary>単項の演算子を調べる。</summary>
    private static void CheckUnary(ShaderSyntaxToken op, string operand, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        bool valid = op.Kind switch
        {
            ShaderSyntaxKind.ExclamationToken => operand == "bool",

            // 符号の反転と ++ / -- は、int から vec4 まで (bool と行列は不可)。
            ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken
                => Rank(operand) is { } rank && rank > Rank("bvec4") && rank < Rank("mat2"),

            // + と ~ は _validate_operator に無い (Godot の別の所で見る)。ここでは見ない。
            _ => true,
        };

        if (!valid)
        {
            report(GodotRuleDescriptors.WrongOperandTypes, op, $"演算子 '{op.Text}' は {operand} に使用できません。");
        }
    }

    /// <summary>2 項の演算子の型の組が正しいか (<c>_validate_operator</c>)。</summary>
    private static bool IsValidBinary(ShaderSyntaxKind op, string a, string b)
    {
        switch (op)
        {
            // 構造体どうしは、型の名前を比べずに通す (_validate_operator は TYPE_STRUCT どうしを同じ型とみなす)。
            case ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken:
                return a == b || (Rank(a) is null && Rank(b) is null && !IsOpaque(a) && !IsOpaque(b));

            case ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.LessThanEqualsToken
                or ShaderSyntaxKind.GreaterThanToken or ShaderSyntaxKind.GreaterThanEqualsToken:
                return a == b && a is "int" or "uint" or "float";

            case ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken:
                return a == "bool" && b == "bool";

            case ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.SlashToken:
                return IsValidArithmetic(op == ShaderSyntaxKind.AsteriskToken, a, b);

            case ShaderSyntaxKind.PercentToken or ShaderSyntaxKind.LessThanLessThanToken or ShaderSyntaxKind.GreaterThanGreaterThanToken:
                return IsValidInteger(a, b);

            // ビット演算は、代入でなければ左右を入れ替えてよい。
            case ShaderSyntaxKind.AmpersandToken or ShaderSyntaxKind.BarToken or ShaderSyntaxKind.CaretToken:
                return IsValidInteger(a, b) || IsValidInteger(b, a);

            default:
                return true;
        }
    }

    /// <summary>複合代入の型の組が正しいか。</summary>
    private static bool IsValidCompoundAssignment(ShaderSyntaxKind op, string target, string value)
        => op switch
        {
            ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken or ShaderSyntaxKind.AsteriskEqualsToken or ShaderSyntaxKind.SlashEqualsToken
                => (target == value && IsArithmetic(target))
                   || ElementScalar(target) == value
                   || (op == ShaderSyntaxKind.AsteriskEqualsToken && IsMatchingVectorMatrix(target, value)),
            ShaderSyntaxKind.PercentEqualsToken or ShaderSyntaxKind.LessThanLessThanEqualsToken or ShaderSyntaxKind.GreaterThanGreaterThanEqualsToken
                or ShaderSyntaxKind.AmpersandEqualsToken or ShaderSyntaxKind.BarEqualsToken or ShaderSyntaxKind.CaretEqualsToken
                => IsValidInteger(target, value),
            _ => true,
        };

    /// <summary><c>+ - * /</c>。同じ型、スカラーとそれを要素に持つベクトル・行列、<c>*</c> はベクトルと同じ大きさの行列 (順は問わない)。</summary>
    private static bool IsValidArithmetic(bool multiply, string a, string b)
    {
        if (a == b)
        {
            return IsArithmetic(a);
        }

        return ElementScalar(a) == b
               || ElementScalar(b) == a
               || (multiply && (IsMatchingVectorMatrix(a, b) || IsMatchingVectorMatrix(b, a)));
    }

    /// <summary><c>%</c> とシフト。同じ整数の型か、整数のベクトルとその要素の型のスカラー (スカラーは右)。</summary>
    private static bool IsValidInteger(string left, string right)
        => (left == right && IsInteger(left)) || (IsInteger(left) && left != right && ElementScalar(left) == right);

    /// <summary>数のスカラー・ベクトル・行列か (bool と bvec は除く)。</summary>
    private static bool IsArithmetic(string type) => Rank(type) is { } rank && rank > Rank("bvec4");

    /// <summary>サンプラーの型か。</summary>
    private static bool IsOpaque(string type) => type.Contains("sampler", StringComparison.Ordinal);

    private static bool IsInteger(string type) => type is "int" or "ivec2" or "ivec3" or "ivec4" or "uint" or "uvec2" or "uvec3" or "uvec4";

    /// <summary>ベクトルか行列の要素の型 (スカラーには無い)。</summary>
    private static string? ElementScalar(string type) => type switch
    {
        "ivec2" or "ivec3" or "ivec4" => "int",
        "uvec2" or "uvec3" or "uvec4" => "uint",
        "vec2" or "vec3" or "vec4" or "mat2" or "mat3" or "mat4" => "float",
        _ => null,
    };

    /// <summary>ベクトルと、同じ大きさの行列か (vec3 と mat3)。</summary>
    private static bool IsMatchingVectorMatrix(string vector, string matrix)
        => (vector, matrix) is ("vec2", "mat2") or ("vec3", "mat3") or ("vec4", "mat4");

    private static int? Rank(string type)
    {
        int index = TypeOrder.IndexOf(type);
        return index < 0 ? null : index;
    }

    /// <summary>添字を付けない配列の値か。配列の演算は別の誤りなので、ここでは見ない。</summary>
    private static bool IsArrayValue(ExpressionSyntax expression, ExpressionTypeBinder binder) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsArrayValue(parenthesized.Expression, binder),
        IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates.Any(c => c.Type.IsArray),
        _ => false,
    };
}
