using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>WGSL の定数式の値の種類。</summary>
internal enum WgslValueKind
{
    /// <summary>整数 (AbstractInt・i32・u32・i64・u64)。</summary>
    Integer,

    /// <summary>浮動小数点数 (AbstractFloat・f32・f16・f64)。</summary>
    Float,

    /// <summary>真偽値。</summary>
    Bool,
}

/// <summary>WGSL の定数式の値。</summary>
/// <param name="Kind">種類。</param>
/// <param name="Integer">整数の値 (128 ビットで持ち、あふれを型の範囲で調べる)。</param>
/// <param name="Float">浮動小数点数の値。</param>
/// <param name="Bool">真偽値。</param>
/// <param name="Type">型の名前 (<c>AbstractInt</c>・<c>i32</c> など)。</param>
internal readonly record struct WgslValue(WgslValueKind Kind, Int128 Integer, double Float, bool Bool, string Type)
{
    public static WgslValue OfInteger(Int128 value, string type) => new(WgslValueKind.Integer, value, 0, false, type);

    public static WgslValue OfFloat(double value, string type) => new(WgslValueKind.Float, 0, value, false, type);

    public static WgslValue OfBool(bool value) => new(WgslValueKind.Bool, 0, 0, value, "bool");

    public override string ToString() => Kind switch
    {
        WgslValueKind.Integer => Integer.ToString(CultureInfo.InvariantCulture),
        WgslValueKind.Float => Float.ToString(CultureInfo.InvariantCulture),
        _ => Bool ? "true" : "false",
    };
}

/// <summary>
/// WGSL の定数式 (<c>const</c> と数のリテラルだけでできた式) の値を求める。
/// </summary>
/// <remarks>
/// <para>
/// 求めるのは、数と真偽値のスカラーの演算 (単項の <c>-</c> <c>!</c> <c>~</c>、二項の算術・ビット・シフト・比較・論理) と、<c>const</c> の名前、
/// スカラーの型の値の作成 (<c>u32(1)</c>) だけである。ベクトル・配列・組み込み関数は求めない (分からない値にする)。
/// </para>
/// <para>
/// <b>あふれは型の範囲で調べる。</b> 具体的な型 (<c>i32</c>・<c>u32</c>) の演算が範囲を超えたら、その位置を <see cref="Overflows"/> に残す (naga の「operation overflowed」)。
/// 抽象的な整数は 64 ビットの符号付きの範囲で調べる。
/// </para>
/// </remarks>
internal sealed class WgslConstants
{
    private readonly ExpressionTypeBinder _binder;
    private readonly Dictionary<string, VariableDeclaratorSyntax> _globalConstants = new(StringComparer.Ordinal);
    private readonly HashSet<ShaderSyntaxNode> _evaluating = [];

    /// <summary>定数式の値を求める仕組みを作る。</summary>
    /// <param name="binder">名前の解決と式の型に使う。</param>
    public WgslConstants(ExpressionTypeBinder binder)
    {
        _binder = binder;

        foreach (VariableDeclarationSyntax declaration in binder.Program.Tree.Root.Declarations.OfType<VariableDeclarationSyntax>())
        {
            if (Keyword(declaration) == "const")
            {
                foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
                {
                    _globalConstants.TryAdd(declarator.Name, declarator);
                }
            }
        }
    }

    /// <summary>演算があふれた式と、そのときの説明。</summary>
    public List<(ExpressionSyntax Expression, string Message)> Overflows { get; } = [];

    /// <summary>宣言のキーワード (<c>var</c> / <c>let</c> / <c>const</c> / <c>override</c>)。</summary>
    public static string? Keyword(VariableDeclarationSyntax declaration)
        => declaration.ModifierTokens.LastOrDefault(t => t.Text is "var" or "let" or "const" or "override")?.Text;

    /// <summary>整数の型の範囲。</summary>
    public static (Int128 Min, Int128 Max)? RangeOf(string type) => type switch
    {
        "i32" => (int.MinValue, int.MaxValue),
        "u32" => (0, uint.MaxValue),
        "i64" or WgslTypeSystem.AbstractInt => (long.MinValue, long.MaxValue),
        "u64" => (0, ulong.MaxValue),
        "i16" => (short.MinValue, short.MaxValue),
        "u16" => (0, ushort.MaxValue),
        _ => null,
    };

    /// <summary>整数の型のビットの幅。</summary>
    public static int? WidthOf(string type) => type switch
    {
        "i32" or "u32" => 32,
        "i64" or "u64" or WgslTypeSystem.AbstractInt => 64,
        "i16" or "u16" => 16,
        _ => null,
    };

    /// <summary>式の値を求める。定数式でないか、求められなければ <see langword="null"/>。</summary>
    public WgslValue? Evaluate(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Evaluate(parenthesized.Expression);

            case LiteralExpressionSyntax literal:
                return ParseLiteral(literal.Token);

            case IdentifierExpressionSyntax { Name: "true" or "false" } boolean when _binder.ResolveName(boolean).Kind == DeclaredNameKind.NotFound:
                return WgslValue.OfBool(boolean.Name == "true");

            case IdentifierExpressionSyntax identifier:
                return ConstantDeclarator(identifier) is { Initializer: { } initializer } declarator ? EvaluateDeclarator(declarator, initializer) : null;

            case PrefixUnaryExpressionSyntax unary when Evaluate(unary.Operand) is { } operand:
                return unary.OperatorToken.Kind switch
                {
                    ShaderSyntaxKind.MinusToken when operand.Kind == WgslValueKind.Integer => Check(expression, WgslValue.OfInteger(-operand.Integer, operand.Type), "-"),
                    ShaderSyntaxKind.MinusToken when operand.Kind == WgslValueKind.Float => WgslValue.OfFloat(-operand.Float, operand.Type),
                    ShaderSyntaxKind.ExclamationToken when operand.Kind == WgslValueKind.Bool => WgslValue.OfBool(!operand.Bool),
                    ShaderSyntaxKind.TildeToken when operand.Kind == WgslValueKind.Integer => Complement(operand),
                    _ => null,
                };

            case BinaryExpressionSyntax binary:
                return EvaluateBinary(binary);

            // スカラーの型の値の作成 (u32(1)、i32(x))。
            case InvocationExpressionSyntax { TargetName: "i32" or "u32" or "f32" or "f16" or "bool" or "i64" or "u64" } call
                when call.ArgumentExpressions.ToArray() is [{ } argument] && Evaluate(argument) is { } value:
                return Convert(value, call.TargetName!);

            default:
                return null;
        }
    }

    /// <summary>名前が指す const の宣言子。const でなければ <see langword="null"/>。</summary>
    public VariableDeclaratorSyntax? ConstantDeclarator(IdentifierExpressionSyntax identifier)
    {
        NameResolution resolution = _binder.ResolveName(identifier);

        return resolution.Candidates is [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } declarator }]
               && Keyword(declaration) == "const"
            ? declarator
            : null;
    }

    /// <summary>
    /// 式が実行時の値を使っているか (<c>let</c>・<c>var</c>・仮引数・<c>override</c>・関数の外の <c>var</c> の名前を含む)。
    /// </summary>
    /// <returns>使っている名前の宣言の種類 (<c>let</c> など)。使っていなければ <see langword="null"/>。</returns>
    public string? FindRuntimeName(ExpressionSyntax expression)
    {
        foreach (IdentifierExpressionSyntax identifier in expression.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
        {
            // 呼び出す関数の名前は値ではない。
            if (identifier.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Target, identifier))
            {
                continue;
            }

            NameResolution resolution = _binder.ResolveName(identifier);

            if (resolution.Candidates is not [{ } candidate])
            {
                continue;
            }

            switch (candidate.Declarator)
            {
                case ParameterSyntax:
                    return "仮引数";

                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } when Keyword(declaration) is { } keyword and not "const":
                    return keyword;
            }
        }

        return null;
    }

    /// <summary>型引数に書いたトークン (<c>array&lt;f32, 1 &lt;&lt; 29&gt;</c> の <c>1 &lt;&lt; 29</c>) の値を求める。</summary>
    public WgslValue? EvaluateTokens(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        int index = 0;
        WgslValue? value = ParseTokens(tokens, ref index, 1);
        return index == tokens.Length ? value : null;
    }

    private WgslValue? EvaluateDeclarator(VariableDeclaratorSyntax declarator, ExpressionSyntax initializer)
    {
        if (!_evaluating.Add(declarator))
        {
            return null;
        }

        try
        {
            WgslValue? value = Evaluate(initializer);

            // const x: u32 = 1; の型は u32。
            if (value is { } known && declarator.Parent is VariableDeclarationSyntax { Type.Name: { } type } && WgslTypeSystem.Instance.TryDescribeNumeric(type, out _))
            {
                return Convert(known, type);
            }

            return value;
        }
        finally
        {
            _evaluating.Remove(declarator);
        }
    }

    private WgslValue? EvaluateBinary(BinaryExpressionSyntax binary)
    {
        if (Evaluate(binary.Left) is not { } left || Evaluate(binary.Right) is not { } right)
        {
            return null;
        }

        ShaderSyntaxKind op = binary.OperatorToken.Kind;

        if (op is ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken)
        {
            return left.Kind == WgslValueKind.Bool && right.Kind == WgslValueKind.Bool
                ? WgslValue.OfBool(op == ShaderSyntaxKind.AmpersandAmpersandToken ? left.Bool && right.Bool : left.Bool || right.Bool)
                : null;
        }

        if (op is ShaderSyntaxKind.LessThanLessThanToken or ShaderSyntaxKind.GreaterThanGreaterThanToken)
        {
            return Shift(binary, left, right);
        }

        if (left.Kind != right.Kind)
        {
            return null;
        }

        if (op is ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken
            or ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.GreaterThanToken
            or ShaderSyntaxKind.LessThanEqualsToken or ShaderSyntaxKind.GreaterThanEqualsToken)
        {
            int compared = left.Kind switch
            {
                WgslValueKind.Integer => left.Integer.CompareTo(right.Integer),
                WgslValueKind.Float => left.Float.CompareTo(right.Float),
                _ => left.Bool.CompareTo(right.Bool),
            };

            return WgslValue.OfBool(op switch
            {
                ShaderSyntaxKind.EqualsEqualsToken => compared == 0,
                ShaderSyntaxKind.ExclamationEqualsToken => compared != 0,
                ShaderSyntaxKind.LessThanToken => compared < 0,
                ShaderSyntaxKind.GreaterThanToken => compared > 0,
                ShaderSyntaxKind.LessThanEqualsToken => compared <= 0,
                _ => compared >= 0,
            });
        }

        if (left.Kind == WgslValueKind.Float)
        {
            string type = MixType(left.Type, right.Type);
            return op switch
            {
                ShaderSyntaxKind.PlusToken => WgslValue.OfFloat(left.Float + right.Float, type),
                ShaderSyntaxKind.MinusToken => WgslValue.OfFloat(left.Float - right.Float, type),
                ShaderSyntaxKind.AsteriskToken => WgslValue.OfFloat(left.Float * right.Float, type),
                ShaderSyntaxKind.SlashToken when right.Float != 0 => WgslValue.OfFloat(left.Float / right.Float, type),
                _ => null,
            };
        }

        if (left.Kind != WgslValueKind.Integer)
        {
            return null;
        }

        string integerType = MixType(left.Type, right.Type);

        return op switch
        {
            ShaderSyntaxKind.PlusToken => Check(binary, WgslValue.OfInteger(left.Integer + right.Integer, integerType), "+"),
            ShaderSyntaxKind.MinusToken => Check(binary, WgslValue.OfInteger(left.Integer - right.Integer, integerType), "-"),
            ShaderSyntaxKind.AsteriskToken => Check(binary, WgslValue.OfInteger(left.Integer * right.Integer, integerType), "*"),
            ShaderSyntaxKind.SlashToken when right.Integer != 0 => Check(binary, WgslValue.OfInteger(left.Integer / right.Integer, integerType), "/"),
            ShaderSyntaxKind.PercentToken when right.Integer != 0 => WgslValue.OfInteger(left.Integer % right.Integer, integerType),
            ShaderSyntaxKind.AmpersandToken => WgslValue.OfInteger(left.Integer & right.Integer, integerType),
            ShaderSyntaxKind.BarToken => WgslValue.OfInteger(left.Integer | right.Integer, integerType),
            ShaderSyntaxKind.CaretToken => WgslValue.OfInteger(left.Integer ^ right.Integer, integerType),
            _ => null,
        };
    }

    /// <summary>
    /// 定数どうしのシフト。シフトの量は型のビットの幅より小さくなければならず、左シフトは型の範囲を超えてはならない (naga の「RHS of shift operation is greater than or equal to」と「operation overflowed」)。
    /// シフトの量の検査そのものは <see cref="WgslConstantAnalyzer"/> が行うので、ここでは値を求めず、あふれだけを残す。
    /// </summary>
    private WgslValue? Shift(BinaryExpressionSyntax binary, WgslValue left, WgslValue right)
    {
        if (left.Kind != WgslValueKind.Integer || right.Kind != WgslValueKind.Integer
            || WidthOf(left.Type) is not { } width || right.Integer < 0 || right.Integer >= width)
        {
            return null;
        }

        int amount = (int)right.Integer;

        if (binary.OperatorToken.Kind == ShaderSyntaxKind.GreaterThanGreaterThanToken)
        {
            return WgslValue.OfInteger(left.Integer >> amount, left.Type);
        }

        // 符号付きの型は、捨てるビットと残る最上位のビットが、元の符号と同じでなければならない。符号無しの型は、捨てるビットが 0 でなければならない。
        Int128 shifted = left.Integer << amount;
        return Check(binary, WgslValue.OfInteger(shifted, left.Type), "<<");
    }

    private WgslValue? Complement(WgslValue operand)
        => operand.Type switch
        {
            "u32" => WgslValue.OfInteger(~operand.Integer & uint.MaxValue, "u32"),
            "u64" => WgslValue.OfInteger(~operand.Integer & ulong.MaxValue, "u64"),
            _ => WgslValue.OfInteger(~operand.Integer, operand.Type),
        };

    /// <summary>整数の結果が型の範囲に収まるか調べる。収まらなければあふれとして残し、値は分からないものにする。</summary>
    private WgslValue? Check(ExpressionSyntax expression, WgslValue value, string op)
    {
        if (value.Kind == WgslValueKind.Integer && RangeOf(value.Type) is { } range && (value.Integer < range.Min || value.Integer > range.Max))
        {
            Overflows.Add((expression, $"{op} の結果が {Describe(value.Type)} に収まりません"));
            return null;
        }

        return value;
    }

    private static string Describe(string type) => type == WgslTypeSystem.AbstractInt ? "抽象的な整数 (64 ビット)" : type;

    /// <summary>抽象的な型と具体的な型を混ぜたら、具体的な型になる。</summary>
    private static string MixType(string left, string right)
        => left is WgslTypeSystem.AbstractInt or WgslTypeSystem.AbstractFloat ? right : left;

    /// <summary>値を型に変換する (const x: u32 = 1、u32(1))。整数どうしは範囲を、型の種類が違えば値の変換をする。</summary>
    private static WgslValue? Convert(WgslValue value, string type) => (value.Kind, type) switch
    {
        (WgslValueKind.Integer, "i32" or "u32" or "i64" or "u64") => WgslValue.OfInteger(value.Integer, type),
        (WgslValueKind.Integer, "f32" or "f16" or "f64") => WgslValue.OfFloat((double)value.Integer, type),
        (WgslValueKind.Float, "f32" or "f16" or "f64") => WgslValue.OfFloat(value.Float, type),
        (WgslValueKind.Float, "i32" or "u32") => WgslValue.OfInteger((Int128)Math.Truncate(value.Float), type),
        (WgslValueKind.Bool, "bool") => value,
        (WgslValueKind.Integer, "bool") => WgslValue.OfBool(value.Integer != 0),
        _ => null,
    };

    /// <summary>数のリテラルの値。</summary>
    public static WgslValue? ParseLiteral(ShaderSyntaxToken token)
    {
        if (token.Kind != ShaderSyntaxKind.NumericLiteralToken || WgslTypeSystem.Instance.GetLiteralTypeName(token) is not { } type)
        {
            return null;
        }

        string text = token.Text;

        if (type is "f32" or "f16" or "f64" or WgslTypeSystem.AbstractFloat)
        {
            string digits = text.TrimEnd('f', 'h', 'l');
            return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? WgslValue.OfFloat(value, type) : null;
        }

        string integer = text.TrimEnd('i', 'u', 'l');
        bool hex = integer.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        return hex
            ? BigInteger.TryParse("0" + integer[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out BigInteger big) && big <= (BigInteger)Int128.MaxValue
                ? WgslValue.OfInteger((Int128)big, type)
                : null
            : Int128.TryParse(integer, NumberStyles.None, CultureInfo.InvariantCulture, out Int128 parsed) ? WgslValue.OfInteger(parsed, type) : null;
    }

    // --------------------------------------------------------------------
    // 型引数のトークンの式 (優先順位で読む)
    // --------------------------------------------------------------------

    private WgslValue? ParseTokens(ImmutableArray<ShaderSyntaxToken> tokens, ref int index, int minimum)
    {
        WgslValue? left = ParseTokenUnary(tokens, ref index);

        while (index < tokens.Length && Precedence(tokens[index].Kind) is var precedence && precedence >= minimum && precedence > 0)
        {
            ShaderSyntaxToken op = tokens[index++];
            WgslValue? right = ParseTokens(tokens, ref index, precedence + 1);

            if (left is not { } l || right is not { } r)
            {
                left = null;
                continue;
            }

            if (op.Kind is ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken)
            {
                left = l.Kind == WgslValueKind.Bool && r.Kind == WgslValueKind.Bool
                    ? WgslValue.OfBool(op.Kind == ShaderSyntaxKind.AmpersandAmpersandToken ? l.Bool && r.Bool : l.Bool || r.Bool)
                    : null;
                continue;
            }

            if (Precedence(op.Kind) is 6 or 7)
            {
                int? compared = (l.Kind, r.Kind) switch
                {
                    (WgslValueKind.Integer, WgslValueKind.Integer) => l.Integer.CompareTo(r.Integer),
                    (WgslValueKind.Float, WgslValueKind.Float) => l.Float.CompareTo(r.Float),
                    (WgslValueKind.Bool, WgslValueKind.Bool) => l.Bool.CompareTo(r.Bool),
                    _ => null,
                };

                left = compared is not { } c ? null : WgslValue.OfBool(op.Kind switch
                {
                    ShaderSyntaxKind.EqualsEqualsToken => c == 0,
                    ShaderSyntaxKind.ExclamationEqualsToken => c != 0,
                    ShaderSyntaxKind.LessThanToken => c < 0,
                    ShaderSyntaxKind.GreaterThanToken => c > 0,
                    ShaderSyntaxKind.LessThanEqualsToken => c <= 0,
                    _ => c >= 0,
                });
                continue;
            }

            if (l.Kind != WgslValueKind.Integer || r.Kind != WgslValueKind.Integer)
            {
                left = null;
                continue;
            }

            string type = MixType(l.Type, r.Type);
            left = op.Kind switch
            {
                ShaderSyntaxKind.PlusToken => WgslValue.OfInteger(l.Integer + r.Integer, type),
                ShaderSyntaxKind.MinusToken => WgslValue.OfInteger(l.Integer - r.Integer, type),
                ShaderSyntaxKind.AsteriskToken => WgslValue.OfInteger(l.Integer * r.Integer, type),
                ShaderSyntaxKind.SlashToken when r.Integer != 0 => WgslValue.OfInteger(l.Integer / r.Integer, type),
                ShaderSyntaxKind.LessThanLessThanToken when r.Integer >= 0 && r.Integer < 64 => WgslValue.OfInteger(l.Integer << (int)r.Integer, type),
                ShaderSyntaxKind.GreaterThanGreaterThanToken when r.Integer >= 0 && r.Integer < 64 => WgslValue.OfInteger(l.Integer >> (int)r.Integer, type),
                _ => null,
            };
        }

        return left;
    }

    private WgslValue? ParseTokenUnary(ImmutableArray<ShaderSyntaxToken> tokens, ref int index)
    {
        if (index >= tokens.Length)
        {
            return null;
        }

        ShaderSyntaxToken token = tokens[index++];

        switch (token.Kind)
        {
            case ShaderSyntaxKind.MinusToken:
                return ParseTokenUnary(tokens, ref index) switch
                {
                    { Kind: WgslValueKind.Integer } negated => WgslValue.OfInteger(-negated.Integer, negated.Type),
                    { Kind: WgslValueKind.Float } negated => WgslValue.OfFloat(-negated.Float, negated.Type),
                    _ => null,
                };

            case ShaderSyntaxKind.NumericLiteralToken:
                return ParseLiteral(token);

            case ShaderSyntaxKind.ExclamationToken:
                return ParseTokenUnary(tokens, ref index) is { Kind: WgslValueKind.Bool } not ? WgslValue.OfBool(!not.Bool) : null;

            case ShaderSyntaxKind.OpenParenToken:
            {
                WgslValue? inner = ParseTokens(tokens, ref index, 1);
                if (index < tokens.Length && tokens[index].Kind == ShaderSyntaxKind.CloseParenToken)
                {
                    index++;
                    return inner;
                }

                return null;
            }

            case ShaderSyntaxKind.IdentifierToken when token.Text is "true" or "false":
                return WgslValue.OfBool(token.Text == "true");

            case ShaderSyntaxKind.IdentifierToken when _globalConstants.TryGetValue(token.Text, out VariableDeclaratorSyntax? declarator) && declarator.Initializer is { } initializer:
                return EvaluateDeclarator(declarator, initializer);

            default:
                return null;
        }
    }

    private static int Precedence(ShaderSyntaxKind kind) => kind switch
    {
        ShaderSyntaxKind.BarBarToken => 1,
        ShaderSyntaxKind.AmpersandAmpersandToken => 2,
        ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken => 6,
        ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.GreaterThanToken
            or ShaderSyntaxKind.LessThanEqualsToken or ShaderSyntaxKind.GreaterThanEqualsToken => 7,
        ShaderSyntaxKind.LessThanLessThanToken or ShaderSyntaxKind.GreaterThanGreaterThanToken => 8,
        ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken => 9,
        ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.SlashToken => 10,
        _ => 0,
    };
}
