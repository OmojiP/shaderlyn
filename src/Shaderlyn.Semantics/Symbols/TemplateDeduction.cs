using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// template の関数と同じ名前を持つ関数 1 つの、戻り値の型の決まり方 (HLSL 2021)。
/// </summary>
/// <param name="Declaration">関数の宣言。</param>
/// <param name="TypeParameters">型引数の名前。template でない関数では空。</param>
/// <param name="ReturnType">戻り値の型名。型引数を含む型 (<c>vector&lt;T, 3&gt;</c>) で決められなければ <see langword="null"/>。</param>
internal readonly record struct TemplateFunctionCandidate(
    FunctionDeclarationSyntax Declaration,
    ImmutableArray<string> TypeParameters,
    string? ReturnType)
{
    /// <summary>template の関数かどうか。</summary>
    public bool IsTemplate => !TypeParameters.IsEmpty;
}

/// <summary>同じ型引数が、2 つの実引数から違う型に決まったこと。</summary>
/// <param name="TypeParameter">型引数の名前。</param>
/// <param name="FirstIndex">先に型を決めた実引数の位置 (0 から)。</param>
/// <param name="FirstType">先に決まった型。</param>
/// <param name="SecondIndex">食い違った実引数の位置 (0 から)。</param>
/// <param name="SecondType">食い違った型。</param>
internal readonly record struct TemplateDeductionConflict(
    string TypeParameter,
    int FirstIndex,
    string FirstType,
    int SecondIndex,
    string SecondType);

/// <summary>実引数の形が、仮引数の形 (<c>vector&lt;T, 3&gt;</c>) に合わないこと。</summary>
/// <param name="ArgumentIndex">実引数の位置 (0 から)。</param>
/// <param name="ArgumentType">実引数の型。</param>
/// <param name="ParameterType">仮引数の型の書き方 (<c>vector&lt;T, 3&gt;</c>)。</param>
internal readonly record struct TemplateDeductionMismatch(int ArgumentIndex, string ArgumentType, string ParameterType);

/// <summary>推論の結果。</summary>
/// <param name="Bindings">型引数の名前から、決まった型への対応。決められなかった型引数は <see langword="null"/>。</param>
/// <param name="Conflict">同じ型引数が違う型に決まった場合、その中身。</param>
/// <param name="Mismatch">実引数の形が仮引数の形に合わない場合、その中身。</param>
internal readonly record struct TemplateDeductionResult(
    FrozenDictionary<string, string?> Bindings,
    TemplateDeductionConflict? Conflict,
    TemplateDeductionMismatch? Mismatch = null)
{
    /// <summary>その関数を呼べないことが確かかどうか。</summary>
    public bool Fails => Conflict is not null || Mismatch is not null;
}

/// <summary>
/// template の関数の呼び出しで、型引数を実引数から決める (HLSL 2021)。
/// </summary>
/// <remarks>
/// <para>
/// 規則は DXC (<c>-HV 2021</c>) で確かめた。
/// 仮引数の型が型引数そのもの (<c>T a</c>) なら、実引数の型がそのまま型引数になる。
/// 同じ型引数に当たる実引数の型は<b>完全に一致</b>しなければならない。
/// <c>float</c> と <c>half</c>、<c>float3</c> と <c>float</c>、<c>int</c> と <c>uint</c> はどれも呼び出せない。
/// typedef で同じ型 (<c>dword</c> と <c>uint</c>、<c>float32_t</c> と <c>float</c>) は一致する。
/// </para>
/// <para>
/// <b>接尾辞の無い数は、<c>float</c> / <c>int</c> とは別の型として推論される。</b>
/// <c>MaxOf(1.0, k)</c> (<c>k</c> は <c>float</c>) も <c>MaxOf(1, 2.0)</c> も呼び出せない。
/// 括弧や符号 (<c>-1.0</c>) を付けてもリテラルのままである。式 (<c>k * 2.0</c>) は式の型になる。
/// </para>
/// <para>
/// 仮引数が <c>vector&lt;T, N&gt;</c> なら、実引数のベクトルの基本型が <c>T</c>、成分の数が <c>N</c> になる。
/// ベクトルでない実引数 (スカラー・行列) や、成分の数が書かれた数と違う実引数は呼び出せない。
/// </para>
/// <para>
/// <b>型を比べてよいときだけ食い違いと言う。</b>
/// 数値型の基本型が確かめた範囲 (<see cref="ComparableBaseNames"/>) にあるものと、リテラルだけを比べる。
/// 構造体や 16 ビットの型の別名は、同じ型かどうかを決めきれないので比べない。
/// </para>
/// </remarks>
internal static class TemplateDeduction
{
    /// <summary>接尾辞の無い浮動小数点数のリテラルの型を表す名前。</summary>
    internal const string LiteralFloat = "literal float";

    /// <summary>接尾辞の無い整数のリテラルの型を表す名前。</summary>
    internal const string LiteralInt = "literal int";

    /// <summary>typedef で同じ型になる名前。</summary>
    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["dword"] = "uint",
        ["float32_t"] = "float",
        ["int32_t"] = "int",
        ["uint32_t"] = "uint",
        ["float64_t"] = "double",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>食い違いを判断してよい基本型。どれも DXC で互いに別の型であることを確かめた。</summary>
    private static readonly FrozenSet<string> ComparableBaseNames = new[]
    {
        "bool", "int", "uint", "float", "double", "half", "min16float", "min16int", "min16uint",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>推論に使う実引数の型を求める。</summary>
    /// <param name="evaluator">実引数の位置の評価器。</param>
    /// <param name="argument">実引数。</param>
    /// <returns>型名。接尾辞の無い数なら <see cref="LiteralFloat"/> / <see cref="LiteralInt"/>。分からなければ <see langword="null"/>。</returns>
    internal static string? ArgumentType(ExpressionTypeEvaluator evaluator, HlslExpressionSyntax argument)
    {
        ArgumentNullException.ThrowIfNull(evaluator);

        if (AsLiteral(argument) is { } token)
        {
            // 接尾辞の無い数だけがリテラルの型になる。1.0f や 1u はふつうの型である。
            if (HlslLiteral.GetDigits(token).Length == token.Text.Length)
            {
                return HlslLiteral.GetTypeName(token) switch
                {
                    "float" => LiteralFloat,
                    "int" => LiteralInt,
                    _ => null,
                };
            }

            // 整数に付く l の型は決めきれない (幅の指定)。
            if (HlslLiteral.GetTypeName(token) is "int")
            {
                return null;
            }
        }

        return evaluator.Evaluate(argument) is { } type ? Canonicalize(type) : null;
    }

    /// <summary>実引数が、括弧と符号を除けば数のリテラルかを調べる。</summary>
    /// <param name="argument">実引数。</param>
    /// <returns>リテラルのトークン。リテラルでなければ <see langword="null"/>。</returns>
    internal static ShaderSyntaxToken? AsLiteral(HlslExpressionSyntax argument)
    {
        HlslExpressionSyntax current = argument;

        while (true)
        {
            switch (current)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken } unary:
                    current = unary.Operand;
                    continue;

                case LiteralExpressionSyntax { Token.Kind: ShaderSyntaxKind.NumericLiteralToken } literal:
                    return literal.Token;

                default:
                    return null;
            }
        }
    }

    /// <summary>型引数を実引数から決める。</summary>
    /// <param name="candidate">template の関数。</param>
    /// <param name="invocation">呼び出し。</param>
    /// <param name="argumentType">実引数の型を求める関数 (<see cref="ArgumentType"/>)。</param>
    /// <returns>決まった型引数と、食い違い。</returns>
    /// <remarks>
    /// 書いた型引数 (<c>MaxOf&lt;float&gt;</c>) は推論しない。実引数はその型へ変換される。
    /// 仮引数の型が型引数そのものか <c>vector&lt;T, N&gt;</c> のときだけ推論する。ほかの形 (行列・配列・構造体) からは推論しない。
    /// </remarks>
    internal static TemplateDeductionResult Deduce(
        TemplateFunctionCandidate candidate,
        InvocationExpressionSyntax invocation,
        Func<HlslExpressionSyntax, string?> argumentType)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(argumentType);

        Dictionary<string, string?> bindings = new(StringComparer.Ordinal);
        Dictionary<string, int> boundAt = new(StringComparer.Ordinal);
        ImmutableArray<string?> written = SplitTypeArguments(invocation.TypeArgumentTokens);

        for (int i = 0; i < written.Length && i < candidate.TypeParameters.Length; i++)
        {
            bindings[candidate.TypeParameters[i]] = written[i];
        }

        HashSet<string> explicitlyWritten = [.. bindings.Keys];
        HashSet<string> undecided = new(StringComparer.Ordinal);
        ParameterSyntax[] parameters = [.. candidate.Declaration.ParameterList];
        HlslExpressionSyntax[] arguments = [.. invocation.ArgumentExpressions];
        TemplateDeductionConflict? conflict = null;
        TemplateDeductionMismatch? mismatch = null;

        bool IsDeducible(string name)
            => candidate.TypeParameters.Contains(name) && !explicitlyWritten.Contains(name);

        // 実引数 1 つから、型引数 1 つの値が分かった。
        void Observe(string name, string? value, int index)
        {
            if (undecided.Contains(name))
            {
                return;
            }

            if (value is null)
            {
                undecided.Add(name);
                return;
            }

            if (!boundAt.TryGetValue(name, out int first))
            {
                bindings[name] = value;
                boundAt[name] = index;
                return;
            }

            string previous = bindings[name]!;

            if (previous == value)
            {
                return;
            }

            if (IsComparable(previous) && IsComparable(value))
            {
                conflict ??= new TemplateDeductionConflict(name, first, previous, index, value);
            }
            else
            {
                undecided.Add(name);
            }
        }

        for (int i = 0; i < parameters.Length && i < arguments.Length && conflict is null && mismatch is null; i++)
        {
            ParameterSyntax parameter = parameters[i];
            HlslTypeSyntax type = parameter.Type;

            if (parameter.IsArray || !type.QualifierTokens.IsEmpty)
            {
                continue;
            }

            // 型引数そのもの (T a)。
            if (type.OpenAngle is null)
            {
                if (IsDeducible(type.NameToken.Text))
                {
                    Observe(type.NameToken.Text, argumentType(arguments[i]), i);
                }

                continue;
            }

            // vector<T, N> a。
            if (type.NameToken.Text != "vector"
                || type.TemplateArguments is not [var element, { Kind: ShaderSyntaxKind.CommaToken }, var count])
            {
                continue;
            }

            bool deducesElement = IsDeducible(element.Text);
            bool deducesCount = IsDeducible(count.Text);

            if (!deducesElement && !deducesCount)
            {
                continue;
            }

            if (argumentType(arguments[i]) is not { } argument
                || IsLiteral(argument)
                || !HlslTypeClassifier.TryDescribeNumeric(argument, out HlslNumericShape shape)
                || !ComparableBaseNames.Contains(shape.BaseName))
            {
                if (deducesElement)
                {
                    undecided.Add(element.Text);
                }

                if (deducesCount)
                {
                    undecided.Add(count.Text);
                }

                continue;
            }

            string parameterType = $"vector<{element.Text}, {count.Text}>";

            // ベクトルでない値、書かれた基本型や成分の数と違う値は、この仮引数に合わない。
            if (shape.Kind != HlslNumericKind.Vector
                || (!deducesElement && IsComparable(element.Text) && Canonicalize(element.Text) != shape.BaseName)
                || (!deducesCount && int.TryParse(count.Text, out int columns) && columns != shape.Columns))
            {
                mismatch = new TemplateDeductionMismatch(i, argument, parameterType);
                break;
            }

            if (deducesElement)
            {
                Observe(element.Text, shape.BaseName, i);
            }

            if (deducesCount)
            {
                Observe(count.Text, shape.Columns.ToString(System.Globalization.CultureInfo.InvariantCulture), i);
            }
        }

        foreach (string name in candidate.TypeParameters)
        {
            if (undecided.Contains(name) || !bindings.ContainsKey(name))
            {
                bindings[name] = null;
            }
        }

        return new TemplateDeductionResult(bindings.ToFrozenDictionary(StringComparer.Ordinal), conflict, mismatch);
    }

    /// <summary>決まった型引数で、戻り値の型を置き換える。</summary>
    /// <param name="candidate">template の関数。</param>
    /// <param name="result">推論の結果。</param>
    /// <returns>戻り値の型。決まらない、またはリテラルの型なら <see langword="null"/>。</returns>
    internal static string? ReturnTypeOf(TemplateFunctionCandidate candidate, TemplateDeductionResult result)
    {
        if (candidate.ReturnType is not { } returnType)
        {
            return ComposedReturnType(candidate, result);
        }

        if (!candidate.TypeParameters.Contains(returnType))
        {
            return returnType;
        }

        // リテラルから決まった型 (One(1.0)) は、使う場所で float などになる。ここでは主張しない。
        return result.Bindings.GetValueOrDefault(returnType) is { } bound && !IsLiteral(bound) ? bound : null;
    }

    /// <summary>戻り値の <c>vector&lt;T, N&gt;</c> を、決まった型引数で組み立てる。</summary>
    /// <param name="candidate">template の関数。</param>
    /// <param name="result">推論の結果。</param>
    /// <returns>組み立てた型 (<c>float3</c>)。決まらなければ <see langword="null"/>。</returns>
    private static string? ComposedReturnType(TemplateFunctionCandidate candidate, TemplateDeductionResult result)
    {
        HlslTypeSyntax type = candidate.Declaration.ReturnType;

        if (type.NameToken.Text != "vector"
            || type.TemplateArguments is not [var element, { Kind: ShaderSyntaxKind.CommaToken }, var count])
        {
            return null;
        }

        string? baseName = candidate.TypeParameters.Contains(element.Text)
            ? result.Bindings.GetValueOrDefault(element.Text)
            : element.Text;
        string? columns = candidate.TypeParameters.Contains(count.Text)
            ? result.Bindings.GetValueOrDefault(count.Text)
            : count.Text;

        return baseName is not null
               && !IsLiteral(baseName)
               && int.TryParse(columns, out int n)
               && n is >= 1 and <= 4
            ? HlslTypeClassifier.ComposeNumeric(baseName, n)
            : null;
    }

    /// <summary>型名が、リテラルの型を表すかを調べる。</summary>
    /// <param name="type">型名。</param>
    /// <returns>リテラルの型なら <see langword="true"/>。</returns>
    internal static bool IsLiteral(string type) => type is LiteralFloat or LiteralInt;

    /// <summary>型引数のトークンを、カンマで区切った 1 つずつの型名にする。</summary>
    /// <param name="tokens">型引数のトークン。</param>
    /// <returns>型名。1 つの名前で書かれていないもの (<c>vector&lt;float, 3&gt;</c> や数) は <see langword="null"/>。</returns>
    internal static ImmutableArray<string?> SplitTypeArguments(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        if (tokens.IsEmpty)
        {
            return [];
        }

        ImmutableArray<string?>.Builder arguments = ImmutableArray.CreateBuilder<string?>();
        List<ShaderSyntaxToken> current = [];
        int depth = 0;

        foreach (ShaderSyntaxToken token in tokens)
        {
            if (token.Kind == ShaderSyntaxKind.CommaToken && depth == 0)
            {
                arguments.Add(AsTypeName(current));
                current.Clear();
                continue;
            }

            depth += token.Kind switch
            {
                ShaderSyntaxKind.LessThanToken => 1,
                ShaderSyntaxKind.GreaterThanToken => -1,
                _ => 0,
            };
            current.Add(token);
        }

        arguments.Add(AsTypeName(current));
        return arguments.ToImmutable();

        static string? AsTypeName(List<ShaderSyntaxToken> argument)
            => argument is [{ Kind: ShaderSyntaxKind.IdentifierToken } single] ? single.Text : null;
    }

    /// <summary>typedef で同じになる型を、1 つの名前に揃える。</summary>
    /// <param name="type">型名。</param>
    /// <returns>揃えた型名。</returns>
    private static string Canonicalize(string type)
        => HlslTypeClassifier.TryDescribeNumeric(type, out HlslNumericShape shape)
           && Aliases.TryGetValue(shape.BaseName, out string? baseName)
            ? HlslTypeClassifier.Compose(shape with { BaseName = baseName }) ?? type
            : type;

    /// <summary>型が違うことを根拠にしてよい型かを調べる。</summary>
    /// <param name="type">型名。</param>
    /// <returns>比べてよければ <see langword="true"/>。</returns>
    private static bool IsComparable(string type)
        => IsLiteral(type)
           || int.TryParse(type, out _)
           || (HlslTypeClassifier.TryDescribeNumeric(type, out HlslNumericShape shape) && ComparableBaseNames.Contains(shape.BaseName));
}
