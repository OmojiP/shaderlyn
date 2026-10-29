using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>GLSL の定数の式の値。</summary>
/// <param name="Value">値。整数と真偽値も <see cref="double"/> で持つ (真は 1)。</param>
/// <param name="Kind">値の種類。</param>
/// <param name="IsLiteral">リテラルをそのまま書いたものか (glslang の isLiteral。layout の値はリテラルでなければ 4.40 から)。</param>
internal readonly record struct GlslConstant(double Value, GlslConstantKind Kind, bool IsLiteral)
{
    /// <summary>整数の値。</summary>
    public long Integer => (long)Value;
}

/// <summary>定数の値の種類。</summary>
internal enum GlslConstantKind
{
    /// <summary>符号付きの整数。</summary>
    Int,

    /// <summary>符号無しの整数。</summary>
    Uint,

    /// <summary>浮動小数点数。</summary>
    Float,

    /// <summary>真偽値。</summary>
    Bool,
}

/// <summary>
/// GLSL の定数の式の値を求める (glslang の定数の畳み込みのうち、スカラーのもの)。
/// </summary>
/// <remarks>
/// <para>
/// リテラル、<c>const</c> の変数 (初期値が定数のもの)、単項・二項・条件の演算子、スカラーの値の作成 (<c>int(x)</c>) を畳む。
/// ベクトル・行列・配列・関数の呼び出しの値は求めない (<see langword="null"/>)。
/// </para>
/// <para>
/// 求められなければ <see langword="null"/> を返す。定数でないと言うのではなく、分からないということである。
/// 定数でないと言い切れるか (<see cref="IsCertainlyNotConstant"/>) は別に答える。
/// </para>
/// </remarks>
internal sealed class GlslConstantEvaluator(ExpressionTypeBinder binder)
{
    private const int MaxDepth = 32;

    /// <summary>式の値を求める。</summary>
    /// <param name="expression">式。</param>
    /// <returns>値。求められなければ <see langword="null"/>。</returns>
    public GlslConstant? Evaluate(ExpressionSyntax expression) => Evaluate(expression, 0);

    /// <summary>
    /// 式が定数でないと言い切れるかを判定する。<c>const</c> でない変数や、関数の呼び出しを含む式である。
    /// </summary>
    /// <param name="expression">式。</param>
    /// <returns>言い切れれば <see langword="true"/>。</returns>
    public bool IsCertainlyNotConstant(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax => false,
        ParenthesizedExpressionSyntax parenthesized => IsCertainlyNotConstant(parenthesized.Expression),
        PrefixUnaryExpressionSyntax unary => unary.OperatorToken.Kind is ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken
                                             || IsCertainlyNotConstant(unary.Operand),
        PostfixUnaryExpressionSyntax => true,
        AssignmentExpressionSyntax => true,
        BinaryExpressionSyntax binary => IsCertainlyNotConstant(binary.Left) || IsCertainlyNotConstant(binary.Right),
        ConditionalExpressionSyntax conditional => IsCertainlyNotConstant(conditional.Condition)
                                                   || IsCertainlyNotConstant(conditional.WhenTrue)
                                                   || IsCertainlyNotConstant(conditional.WhenFalse),
        IdentifierExpressionSyntax identifier => IsNonConstantVariable(identifier),
        MemberAccessExpressionSyntax member => IsCertainlyNotConstant(member.Target),
        ElementAccessExpressionSyntax element => IsCertainlyNotConstant(element.Target) || IsCertainlyNotConstant(element.Index),

        // 値の作成 (ivec2(x, 1)) は、実引数のどれかが定数でなければ定数でない。
        InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation when GlslKeywords.IsBuiltInType(callee.Name)
            => invocation.ArgumentExpressions.Any(IsCertainlyNotConstant),
        _ => false,
    };

    /// <summary>
    /// 式の成分ごとの値を求める。スカラーなら 1 つ、<c>ivec2(1, -2)</c> のような値の作成なら成分ごと。
    /// </summary>
    /// <param name="expression">式。</param>
    /// <returns>成分ごとの値。どれか 1 つでも求められなければ <see langword="null"/>。</returns>
    public ImmutableArray<GlslConstant>? EvaluateComponents(ExpressionSyntax expression)
    {
        if (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            return EvaluateComponents(parenthesized.Expression);
        }

        if (Evaluate(expression) is { } scalar)
        {
            return [scalar];
        }

        if (expression is not InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation || !GlslKeywords.IsBuiltInType(callee.Name))
        {
            return null;
        }

        ImmutableArray<GlslConstant>.Builder components = ImmutableArray.CreateBuilder<GlslConstant>();

        foreach (ExpressionSyntax argument in invocation.ArgumentExpressions)
        {
            if (Evaluate(argument) is not { } value)
            {
                return null;
            }

            components.Add(value);
        }

        return components.ToImmutable();
    }

    private GlslConstant? Evaluate(ExpressionSyntax expression, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                return EvaluateLiteral(literal.Token);

            case ParenthesizedExpressionSyntax parenthesized:
                return Evaluate(parenthesized.Expression, depth + 1) is { } inner ? inner with { IsLiteral = false } : null;

            case PrefixUnaryExpressionSyntax unary when Evaluate(unary.Operand, depth + 1) is { } operand:
                return ApplyUnary(unary.OperatorToken.Kind, operand);

            case BinaryExpressionSyntax binary:
                return Evaluate(binary.Left, depth + 1) is { } left && Evaluate(binary.Right, depth + 1) is { } right
                    ? ApplyBinary(binary.OperatorToken.Kind, left, right)
                    : null;

            case ConditionalExpressionSyntax conditional when Evaluate(conditional.Condition, depth + 1) is { Kind: GlslConstantKind.Bool } condition:
                return Evaluate(condition.Value != 0 ? conditional.WhenTrue : conditional.WhenFalse, depth + 1) is { } chosen ? chosen with { IsLiteral = false } : null;

            case IdentifierExpressionSyntax identifier:
                return EvaluateVariable(identifier, depth);

            // スカラーの値の作成 (int(x)、uint(x)、float(x)、bool(x))。
            case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax { Name: "int" or "uint" or "float" or "bool" or "double" } callee } invocation
                when invocation.ArgumentExpressions.ToArray() is [{ } argument] && Evaluate(argument, depth + 1) is { } value:
                return Construct(callee.Name, value);

            default:
                return null;
        }
    }

    /// <summary>スカラーの値の作成 (<c>int(x)</c> など) を畳む。</summary>
    /// <param name="type">型の名前 (<c>int</c>、<c>uint</c>、<c>bool</c>、<c>float</c>、<c>double</c>)。</param>
    /// <param name="value">引数の値。</param>
    /// <returns>値。</returns>
    public static GlslConstant Construct(string type, GlslConstant value) => type switch
    {
        "int" => new GlslConstant(value.Kind == GlslConstantKind.Float ? Math.Truncate(value.Value) : (int)value.Integer, GlslConstantKind.Int, false),
        "uint" => new GlslConstant(value.Kind == GlslConstantKind.Float ? Math.Truncate(value.Value) : (uint)value.Integer, GlslConstantKind.Uint, false),
        "bool" => new GlslConstant(value.Value != 0 ? 1 : 0, GlslConstantKind.Bool, false),
        _ => new GlslConstant(value.Value, GlslConstantKind.Float, false),
    };

    /// <summary>リテラルのトークンの値 (<c>true</c> / <c>false</c> を含む)。</summary>
    /// <param name="token">トークン。</param>
    /// <returns>値。リテラルでなければ <see langword="null"/>。</returns>
    public static GlslConstant? EvaluateLiteral(ShaderSyntaxToken token)
    {
        if (token.Kind == ShaderSyntaxKind.IdentifierToken && token.Text is "true" or "false")
        {
            return new GlslConstant(token.Text == "true" ? 1 : 0, GlslConstantKind.Bool, true);
        }

        if (token.Kind != ShaderSyntaxKind.NumericLiteralToken)
        {
            return null;
        }

        if (ShaderLiteral.GetKind(token) == ShaderLiteralKind.Floating)
        {
            return ShaderLiteral.TryGetDouble(token, out double floating) ? new GlslConstant(floating, GlslConstantKind.Float, true) : null;
        }

        if (!ShaderLiteral.TryGetInt64(token, out long integer))
        {
            return null;
        }

        bool unsigned = ShaderLiteral.GetSuffix(token).Contains('u', StringComparison.OrdinalIgnoreCase);
        return new GlslConstant(integer, unsigned ? GlslConstantKind.Uint : GlslConstantKind.Int, true);
    }

    /// <summary>単項の演算子を畳む。</summary>
    /// <param name="kind">演算子のトークンの種類。</param>
    /// <param name="operand">値。</param>
    /// <returns>結果。畳めなければ <see langword="null"/>。</returns>
    public static GlslConstant? ApplyUnary(ShaderSyntaxKind kind, GlslConstant operand) => kind switch
    {
        ShaderSyntaxKind.MinusToken => operand with { Value = operand.Kind == GlslConstantKind.Uint ? unchecked((uint)-(long)operand.Value) : -operand.Value, IsLiteral = false },
        ShaderSyntaxKind.PlusToken => operand with { IsLiteral = false },
        ShaderSyntaxKind.ExclamationToken when operand.Kind == GlslConstantKind.Bool => new GlslConstant(operand.Value == 0 ? 1 : 0, GlslConstantKind.Bool, false),
        ShaderSyntaxKind.TildeToken when operand.Kind is GlslConstantKind.Int => new GlslConstant(~operand.Integer, GlslConstantKind.Int, false),
        ShaderSyntaxKind.TildeToken when operand.Kind is GlslConstantKind.Uint => new GlslConstant(~(uint)operand.Integer, GlslConstantKind.Uint, false),
        _ => null,
    };

    /// <summary>二項の演算子を畳む。</summary>
    /// <param name="kind">演算子のトークンの種類。</param>
    /// <param name="left">左の値。</param>
    /// <param name="right">右の値。</param>
    /// <returns>結果。畳めなければ (整数の 0 での割り算など) <see langword="null"/>。</returns>
    public static GlslConstant? ApplyBinary(ShaderSyntaxKind kind, GlslConstant left, GlslConstant right)
    {
        bool integer = left.Kind is GlslConstantKind.Int or GlslConstantKind.Uint && right.Kind is GlslConstantKind.Int or GlslConstantKind.Uint;
        GlslConstantKind arithmetic = left.Kind == GlslConstantKind.Float || right.Kind == GlslConstantKind.Float ? GlslConstantKind.Float
            : left.Kind == GlslConstantKind.Uint || right.Kind == GlslConstantKind.Uint ? GlslConstantKind.Uint
            : GlslConstantKind.Int;

        GlslConstant Make(double value, GlslConstantKind kind) => new(kind switch
        {
            GlslConstantKind.Int => unchecked((int)(long)value),
            GlslConstantKind.Uint => unchecked((uint)(long)value),
            _ => value,
        }, kind, false);

        GlslConstant Boolean(bool value) => new(value ? 1 : 0, GlslConstantKind.Bool, false);

        return kind switch
        {
            ShaderSyntaxKind.PlusToken => Make(left.Value + right.Value, arithmetic),
            ShaderSyntaxKind.MinusToken => Make(left.Value - right.Value, arithmetic),
            ShaderSyntaxKind.AsteriskToken => Make(left.Value * right.Value, arithmetic),
            ShaderSyntaxKind.SlashToken when integer && right.Value == 0 => null,
            ShaderSyntaxKind.SlashToken => Make(integer ? Math.Truncate(left.Value / right.Value) : left.Value / right.Value, arithmetic),
            ShaderSyntaxKind.PercentToken when integer && right.Value != 0 => Make(left.Integer % right.Integer, arithmetic),
            ShaderSyntaxKind.LessThanLessThanToken when integer => Make(left.Integer << (int)right.Integer, left.Kind),
            ShaderSyntaxKind.GreaterThanGreaterThanToken when integer => Make(left.Integer >> (int)right.Integer, left.Kind),
            ShaderSyntaxKind.AmpersandToken when integer => Make(left.Integer & right.Integer, arithmetic),
            ShaderSyntaxKind.BarToken when integer => Make(left.Integer | right.Integer, arithmetic),
            ShaderSyntaxKind.CaretToken when integer => Make(left.Integer ^ right.Integer, arithmetic),
            ShaderSyntaxKind.EqualsEqualsToken => Boolean(left.Value == right.Value),
            ShaderSyntaxKind.ExclamationEqualsToken => Boolean(left.Value != right.Value),
            ShaderSyntaxKind.LessThanToken => Boolean(left.Value < right.Value),
            ShaderSyntaxKind.GreaterThanToken => Boolean(left.Value > right.Value),
            ShaderSyntaxKind.LessThanEqualsToken => Boolean(left.Value <= right.Value),
            ShaderSyntaxKind.GreaterThanEqualsToken => Boolean(left.Value >= right.Value),
            ShaderSyntaxKind.AmpersandAmpersandToken => Boolean(left.Value != 0 && right.Value != 0),
            ShaderSyntaxKind.BarBarToken => Boolean(left.Value != 0 || right.Value != 0),
            ShaderSyntaxKind.CaretCaretToken => Boolean((left.Value != 0) != (right.Value != 0)),
            _ => null,
        };
    }

    /// <summary><c>const</c> の変数の値を、初期値から求める。</summary>
    private GlslConstant? EvaluateVariable(IdentifierExpressionSyntax identifier, int depth)
    {
        if (binder.ResolveName(identifier) is not { Kind: DeclaredNameKind.Local or DeclaredNameKind.Global, Candidates: [{ Declarator: VariableDeclaratorSyntax declarator }] }
            || declarator.Parent is not VariableDeclarationSyntax declaration
            || !declaration.HasModifier("const")
            || declarator.IsArray
            || declarator.Initializer is not { } initializer)
        {
            return null;
        }

        return Evaluate(initializer, depth + 1) is { } value ? value with { IsLiteral = false } : null;
    }

    /// <summary><c>const</c> でない変数と言い切れるか。</summary>
    private bool IsNonConstantVariable(IdentifierExpressionSyntax identifier)
        => binder.ResolveName(identifier) is { Kind: DeclaredNameKind.Local or DeclaredNameKind.Global, Candidates: { IsEmpty: false } candidates }
           && candidates.All(c => c.Declarator is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } && !declaration.HasModifier("const")
                                  || c.Declarator is ParameterSyntax);
}
