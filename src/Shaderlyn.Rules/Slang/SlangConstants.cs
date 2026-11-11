using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の整数の定数式を、値が確実に決まるときだけ計算する。
/// </summary>
/// <remarks>
/// <para>
/// 読むのは、整数のリテラル、列挙子 (<c>E.A</c>・<c>E::A</c>、<c>[UnscopedEnum]</c> の <c>A</c>)、ファイルの直下と構造体の <c>static const</c> の整数の変数、
/// 括弧、整数の型への型変換、単項の <c>-</c> / <c>~</c> / <c>!</c> / <c>+</c>、二項の算術・ビット・シフト・比較・論理の演算子、<c>?:</c> である。
/// それ以外 (関数の呼び出し・ジェネリックの仮引数・浮動小数) は決まらないものとして <see langword="null"/> を返す。
/// </para>
/// <para>
/// 同じ名前の列挙子や定数が 2 つ以上あるときは、どれを指すか決めないので値を返さない。0 での割り算も値を返さない (別のルールが報告する)。
/// </para>
/// </remarks>
internal sealed class SlangConstants
{
    private readonly Dictionary<(string Enum, string Member), long?> _members = [];
    private readonly Dictionary<string, long?> _unscoped = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExpressionSyntax?> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long?> _constantValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _evaluating = new(StringComparer.Ordinal);

    private SlangConstants()
    {
    }

    /// <summary>ファイルの宣言から作る。</summary>
    public static SlangConstants Of(CompilationUnitSyntax root)
    {
        SlangConstants constants = new();

        foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case EnumDeclarationSyntax enumeration:
                    constants.AddEnum(enumeration);
                    break;

                case VariableDeclarationSyntax declaration
                    when declaration.Parent is CompilationUnitSyntax or StructDeclarationSyntax or NamespaceDeclarationSyntax
                        && declaration.ModifierTokens.Any(t => t.Text == "const")
                        && IsIntegerType(declaration.Type.Name):
                    foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
                    {
                        constants._constants[declarator.Name] = constants._constants.ContainsKey(declarator.Name) || declarator.IsArray
                            ? null
                            : declarator.Initializer;
                    }

                    break;
            }
        }

        return constants;
    }

    /// <summary>整数の型の名前か (スカラー)。</summary>
    public static bool IsIntegerType(string name) => name is "int" or "uint" or "int8_t" or "uint8_t" or "int16_t" or "uint16_t" or "int32_t" or "uint32_t"
        or "int64_t" or "uint64_t" or "dword" or "min16int" or "min16uint" or "min12int" or "intptr_t" or "uintptr_t" or "bool";

    private void AddEnum(EnumDeclarationSyntax enumeration)
    {
        long? next = 0;
        string name = enumeration.Name;

        foreach (EnumMemberSyntax member in enumeration.Members)
        {
            long? value = member.Value is null ? next : Evaluate(member.Value, enumeration);
            _members[(name, member.Name)] = _members.ContainsKey((name, member.Name)) ? null : value;

            if (!enumeration.MembersNeedQualification || name.Length == 0)
            {
                _unscoped[member.Name] = _unscoped.ContainsKey(member.Name) ? null : value;
            }

            next = value + 1;
        }
    }

    /// <summary>式の値。決まらなければ <see langword="null"/>。</summary>
    public long? Evaluate(ExpressionSyntax expression) => Evaluate(expression, null);

    private long? Evaluate(ExpressionSyntax expression, EnumDeclarationSyntax? inside)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                if (literal.Token.Text is "true" or "false")
                {
                    return literal.Token.Text == "true" ? 1 : 0;
                }

                return ShaderLiteral.TryGetInt64(literal.Token, out long number) ? number : null;

            case ParenthesizedExpressionSyntax parenthesized:
                return Evaluate(parenthesized.Expression, inside);

            case CastExpressionSyntax cast when IsIntegerType(cast.Type.Name) && !cast.Type.IsPointer:
                return Truncate(Evaluate(cast.Operand, inside), cast.Type.Name);

            case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax { Name: var typeName } } call when IsIntegerType(typeName) && call.ArgumentExpressions.Count() == 1 && call.ArgumentExpressions.First() is var argument:
                return Truncate(Evaluate(argument, inside), typeName);

            case MemberAccessExpressionSyntax { Target: IdentifierExpressionSyntax target } access:
                return _members.TryGetValue((target.Name, access.NameToken.Text), out long? member) ? member : null;

            case IdentifierExpressionSyntax identifier:
                // 列挙型の中では、その列挙型の前の列挙子を修飾せずに書ける。
                if (inside is not null && _members.TryGetValue((inside.Name, identifier.Name), out long? own))
                {
                    return own;
                }

                if (_unscoped.TryGetValue(identifier.Name, out long? unscoped))
                {
                    return _constants.ContainsKey(identifier.Name) ? null : unscoped;
                }

                return Constant(identifier.Name);

            case PrefixUnaryExpressionSyntax unary:
                long? operand = Evaluate(unary.Operand, inside);
                return unary.OperatorToken.Kind switch
                {
                    ShaderSyntaxKind.MinusToken => -operand,
                    ShaderSyntaxKind.PlusToken => operand,
                    ShaderSyntaxKind.TildeToken => ~operand,
                    ShaderSyntaxKind.ExclamationToken => operand is { } o ? (o == 0 ? 1 : 0) : null,
                    _ => null,
                };

            case ConditionalExpressionSyntax conditional:
                return Evaluate(conditional.Condition, inside) is { } condition
                    ? Evaluate(condition != 0 ? conditional.WhenTrue : conditional.WhenFalse, inside)
                    : null;

            case BinaryExpressionSyntax binary:
                if (Evaluate(binary.Left, inside) is not { } left || Evaluate(binary.Right, inside) is not { } right)
                {
                    return null;
                }

                return binary.OperatorToken.Kind switch
                {
                    ShaderSyntaxKind.PlusToken => left + right,
                    ShaderSyntaxKind.MinusToken => left - right,
                    ShaderSyntaxKind.AsteriskToken => left * right,
                    ShaderSyntaxKind.SlashToken => right == 0 ? null : left / right,
                    ShaderSyntaxKind.PercentToken => right == 0 ? null : left % right,
                    ShaderSyntaxKind.LessThanLessThanToken => right is >= 0 and < 64 ? left << (int)right : null,
                    ShaderSyntaxKind.GreaterThanGreaterThanToken => right is >= 0 and < 64 ? left >> (int)right : null,
                    ShaderSyntaxKind.AmpersandToken => left & right,
                    ShaderSyntaxKind.BarToken => left | right,
                    ShaderSyntaxKind.CaretToken => left ^ right,
                    ShaderSyntaxKind.EqualsEqualsToken => left == right ? 1 : 0,
                    ShaderSyntaxKind.ExclamationEqualsToken => left != right ? 1 : 0,
                    ShaderSyntaxKind.LessThanToken => left < right ? 1 : 0,
                    ShaderSyntaxKind.LessThanEqualsToken => left <= right ? 1 : 0,
                    ShaderSyntaxKind.GreaterThanToken => left > right ? 1 : 0,
                    ShaderSyntaxKind.GreaterThanEqualsToken => left >= right ? 1 : 0,
                    ShaderSyntaxKind.AmpersandAmpersandToken => left != 0 && right != 0 ? 1 : 0,
                    ShaderSyntaxKind.BarBarToken => left != 0 || right != 0 ? 1 : 0,
                    _ => null,
                };

            default:
                return null;
        }
    }

    private long? Constant(string name)
    {
        if (_constantValues.TryGetValue(name, out long? known))
        {
            return known;
        }

        if (!_constants.TryGetValue(name, out ExpressionSyntax? initializer) || initializer is null || !_evaluating.Add(name))
        {
            return null;
        }

        long? value = Evaluate(initializer, null);
        _evaluating.Remove(name);
        _constantValues[name] = value;
        return value;
    }

    /// <summary>型変換で切り詰める。値の決まらない型 (bool 以外の 64 ビット) はそのまま。</summary>
    private static long? Truncate(long? value, string type) => value is not { } v ? null : type switch
    {
        "bool" => v != 0 ? 1 : 0,
        "int8_t" => (sbyte)v,
        "uint8_t" => (byte)v,
        "int16_t" => (short)v,
        "uint16_t" => (ushort)v,
        "int" or "int32_t" or "min16int" or "min12int" => (int)v,
        "uint" or "uint32_t" or "dword" or "min16uint" => (uint)v,
        _ => v,
    };
}
