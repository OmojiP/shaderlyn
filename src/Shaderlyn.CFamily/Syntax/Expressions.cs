using System.Collections.Immutable;

namespace Shaderlyn.CFamily.Syntax;

/// <summary>式の基底クラス。</summary>
public abstract class ExpressionSyntax : ShaderSyntaxNode;

/// <summary>数値・文字列などのリテラル。</summary>
/// <param name="token">リテラルのトークン。</param>
public sealed class LiteralExpressionSyntax(ShaderSyntaxToken token) : ExpressionSyntax
{
    /// <summary>リテラルのトークン。</summary>
    public ShaderSyntaxToken Token { get; } = token;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Token;
    }
}

/// <summary>変数や関数などの名前参照。</summary>
/// <param name="identifier">識別子のトークン。</param>
public sealed class IdentifierExpressionSyntax(ShaderSyntaxToken identifier) : ExpressionSyntax
{
    /// <summary>識別子のトークン。</summary>
    public ShaderSyntaxToken Identifier { get; } = identifier;

    /// <summary>識別子の文字列。</summary>
    public string Name => Identifier.Text;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Identifier;
    }
}

/// <summary>型引数を書いた型の名前 (<c>TLaneVector2D&lt;float, 2&gt;</c>)。静的メンバーの修飾に現れる (HLSL 2021)。</summary>
/// <param name="identifier">型の名前のトークン。</param>
/// <param name="openAngle">開き山括弧。</param>
/// <param name="typeArguments">型引数のトークン。</param>
/// <param name="closeAngle">閉じ山括弧。</param>
/// <remarks>
/// <c>TLaneVector2D&lt;float, 2&gt;::Const(1.0)</c> の <c>::</c> の左側である。
/// 式としての型は求めない (分からない型として扱う)。
/// </remarks>
public sealed class GenericNameExpressionSyntax(
    ShaderSyntaxToken identifier,
    ShaderSyntaxToken openAngle,
    ImmutableArray<ShaderSyntaxToken> typeArguments,
    ShaderSyntaxToken closeAngle) : ExpressionSyntax
{
    /// <summary>型の名前のトークン。</summary>
    public ShaderSyntaxToken Identifier { get; } = identifier;

    /// <summary>型の名前。</summary>
    public string Name => Identifier.Text;

    /// <summary>開き山括弧。</summary>
    public ShaderSyntaxToken OpenAngle { get; } = openAngle;

    /// <summary>型引数のトークン (区切りのカンマを含む)。</summary>
    public ImmutableArray<ShaderSyntaxToken> TypeArguments { get; } = typeArguments;

    /// <summary>閉じ山括弧。</summary>
    public ShaderSyntaxToken CloseAngle { get; } = closeAngle;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Identifier;
        yield return OpenAngle;

        foreach (ShaderSyntaxToken token in TypeArguments)
        {
            yield return token;
        }

        yield return CloseAngle;
    }
}

/// <summary>丸括弧で囲まれた式。</summary>
/// <param name="openParen">開き括弧。</param>
/// <param name="expression">中の式。</param>
/// <param name="closeParen">閉じ括弧。</param>
public sealed class ParenthesizedExpressionSyntax(
    ShaderSyntaxToken openParen,
    ExpressionSyntax expression,
    ShaderSyntaxToken closeParen) : ExpressionSyntax
{
    /// <summary>開き括弧。</summary>
    public ShaderSyntaxToken OpenParen { get; } = openParen;

    /// <summary>中の式。</summary>
    public ExpressionSyntax Expression { get; } = expression;

    /// <summary>閉じ括弧。</summary>
    public ShaderSyntaxToken CloseParen { get; } = closeParen;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return OpenParen;
        yield return Expression;
        yield return CloseParen;
    }
}

/// <summary>二項演算。</summary>
/// <param name="left">左辺。</param>
/// <param name="operatorToken">演算子。</param>
/// <param name="right">右辺。</param>
public sealed class BinaryExpressionSyntax(
    ExpressionSyntax left,
    ShaderSyntaxToken operatorToken,
    ExpressionSyntax right) : ExpressionSyntax
{
    /// <summary>左辺。</summary>
    public ExpressionSyntax Left { get; } = left;

    /// <summary>演算子のトークン。</summary>
    public ShaderSyntaxToken OperatorToken { get; } = operatorToken;

    /// <summary>右辺。</summary>
    public ExpressionSyntax Right { get; } = right;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Left;
        yield return OperatorToken;
        yield return Right;
    }
}

/// <summary>代入。複合代入 (<c>+=</c> など) も含む。</summary>
/// <param name="left">代入先。</param>
/// <param name="operatorToken">代入演算子。</param>
/// <param name="right">代入する値。</param>
public sealed class AssignmentExpressionSyntax(
    ExpressionSyntax left,
    ShaderSyntaxToken operatorToken,
    ExpressionSyntax right) : ExpressionSyntax
{
    /// <summary>代入先。</summary>
    public ExpressionSyntax Left { get; } = left;

    /// <summary>代入演算子のトークン。</summary>
    public ShaderSyntaxToken OperatorToken { get; } = operatorToken;

    /// <summary>代入する値。</summary>
    public ExpressionSyntax Right { get; } = right;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Left;
        yield return OperatorToken;
        yield return Right;
    }
}

/// <summary>前置単項演算。</summary>
/// <param name="operatorToken">演算子。</param>
/// <param name="operand">被演算子。</param>
public sealed class PrefixUnaryExpressionSyntax(
    ShaderSyntaxToken operatorToken,
    ExpressionSyntax operand) : ExpressionSyntax
{
    /// <summary>演算子のトークン。</summary>
    public ShaderSyntaxToken OperatorToken { get; } = operatorToken;

    /// <summary>被演算子。</summary>
    public ExpressionSyntax Operand { get; } = operand;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return OperatorToken;
        yield return Operand;
    }
}

/// <summary>後置単項演算 (<c>++</c> / <c>--</c>)。</summary>
/// <param name="operand">被演算子。</param>
/// <param name="operatorToken">演算子。</param>
public sealed class PostfixUnaryExpressionSyntax(
    ExpressionSyntax operand,
    ShaderSyntaxToken operatorToken) : ExpressionSyntax
{
    /// <summary>被演算子。</summary>
    public ExpressionSyntax Operand { get; } = operand;

    /// <summary>演算子のトークン。</summary>
    public ShaderSyntaxToken OperatorToken { get; } = operatorToken;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Operand;
        yield return OperatorToken;
    }
}

/// <summary>三項条件演算。</summary>
/// <param name="condition">条件。</param>
/// <param name="questionToken"><c>?</c>。</param>
/// <param name="whenTrue">真のときの値。</param>
/// <param name="colonToken"><c>:</c>。</param>
/// <param name="whenFalse">偽のときの値。</param>
public sealed class ConditionalExpressionSyntax(
    ExpressionSyntax condition,
    ShaderSyntaxToken questionToken,
    ExpressionSyntax whenTrue,
    ShaderSyntaxToken colonToken,
    ExpressionSyntax whenFalse) : ExpressionSyntax
{
    /// <summary>条件。</summary>
    public ExpressionSyntax Condition { get; } = condition;

    /// <summary><c>?</c> のトークン。</summary>
    public ShaderSyntaxToken QuestionToken { get; } = questionToken;

    /// <summary>真のときの値。</summary>
    public ExpressionSyntax WhenTrue { get; } = whenTrue;

    /// <summary><c>:</c> のトークン。</summary>
    public ShaderSyntaxToken ColonToken { get; } = colonToken;

    /// <summary>偽のときの値。</summary>
    public ExpressionSyntax WhenFalse { get; } = whenFalse;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Condition;
        yield return QuestionToken;
        yield return WhenTrue;
        yield return ColonToken;
        yield return WhenFalse;
    }
}

/// <summary>
/// 関数呼び出し、および型のコンストラクタ呼び出し。
/// </summary>
/// <param name="target">呼び出す対象。</param>
/// <param name="openParen">開き括弧。</param>
/// <param name="arguments">引数。区切りのカンマを含む。</param>
/// <param name="closeParen">閉じ括弧。</param>
/// <param name="openAngle">型引数 (HLSL 2021 の <c>Twice&lt;float4&gt;(x)</c>) の開き山括弧。無ければ <see langword="null"/>。</param>
/// <param name="typeArgumentTokens">型引数のトークン。カンマを含む。無ければ空。</param>
/// <param name="closeAngle">型引数の閉じ山括弧。無ければ <see langword="null"/>。</param>
/// <remarks>
/// <c>float4(1, 0, 0, 1)</c> のような型のコンストラクタも同じ形で表す。
/// 構文の上では関数呼び出しと区別がつかず、
/// 区別するには型の情報が必要なため意味解析層の仕事になる。
/// </remarks>
public sealed class InvocationExpressionSyntax(
    ExpressionSyntax target,
    ShaderSyntaxToken openParen,
    ImmutableArray<ShaderNodeOrTokenEntry> arguments,
    ShaderSyntaxToken closeParen,
    ShaderSyntaxToken? openAngle = null,
    ImmutableArray<ShaderSyntaxToken> typeArgumentTokens = default,
    ShaderSyntaxToken? closeAngle = null) : ExpressionSyntax
{
    /// <summary>呼び出す対象。</summary>
    public ExpressionSyntax Target { get; } = target;

    /// <summary>型引数の開き山括弧。型引数を書いていなければ <see langword="null"/>。</summary>
    public ShaderSyntaxToken? OpenAngle { get; } = openAngle;

    /// <summary>型引数のトークン (<c>Twice&lt;float4&gt;(x)</c> の <c>float4</c>)。カンマを含む。書いていなければ空。</summary>
    public ImmutableArray<ShaderSyntaxToken> TypeArgumentTokens { get; } = typeArgumentTokens.IsDefault ? [] : typeArgumentTokens;

    /// <summary>型引数の閉じ山括弧。型引数を書いていなければ <see langword="null"/>。</summary>
    public ShaderSyntaxToken? CloseAngle { get; } = closeAngle;

    /// <summary>型引数を書いた呼び出しかどうか。</summary>
    public bool HasTypeArguments => OpenAngle is not null;

    /// <summary>開き括弧。</summary>
    public ShaderSyntaxToken OpenParen { get; } = openParen;

    /// <summary>引数。区切りのカンマを含む。</summary>
    public ImmutableArray<ShaderNodeOrTokenEntry> Arguments { get; } = arguments;

    /// <summary>閉じ括弧。</summary>
    public ShaderSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>区切りのカンマを除いた引数の式。</summary>
    public IEnumerable<ExpressionSyntax> ArgumentExpressions
        => Arguments.Select(a => a.Node).OfType<ExpressionSyntax>();

    /// <summary>呼び出し対象が単純な名前の場合、その名前。それ以外は <see langword="null"/>。</summary>
    /// <remarks>「この関数は使わない」というルールが対象を特定するために使う。</remarks>
    public string? TargetName => (Target as IdentifierExpressionSyntax)?.Name;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Target;

        if (OpenAngle is not null)
        {
            yield return OpenAngle;
        }

        foreach (ShaderSyntaxToken token in TypeArgumentTokens)
        {
            yield return token;
        }

        if (CloseAngle is not null)
        {
            yield return CloseAngle;
        }

        yield return OpenParen;

        foreach (ShaderNodeOrTokenEntry argument in Arguments)
        {
            yield return argument.ToNodeOrToken();
        }

        yield return CloseParen;
    }
}

/// <summary>メンバーアクセスおよびスウィズル。</summary>
/// <param name="target">対象の式。</param>
/// <param name="dotToken"><c>.</c>。</param>
/// <param name="nameToken">メンバー名。</param>
/// <remarks>
/// <c>color.rgb</c> のようなスウィズルも構文上はメンバーアクセスと同じ形である。
/// 区別には型の情報が必要なため意味解析層で行う。
/// </remarks>
public sealed class MemberAccessExpressionSyntax(
    ExpressionSyntax target,
    ShaderSyntaxToken dotToken,
    ShaderSyntaxToken nameToken) : ExpressionSyntax
{
    /// <summary>対象の式。</summary>
    public ExpressionSyntax Target { get; } = target;

    /// <summary><c>.</c> のトークン。</summary>
    public ShaderSyntaxToken DotToken { get; } = dotToken;

    /// <summary>メンバー名のトークン。</summary>
    public ShaderSyntaxToken NameToken { get; } = nameToken;

    /// <summary>メンバー名。</summary>
    public string Name => NameToken.Text;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Target;
        yield return DotToken;
        yield return NameToken;
    }
}

/// <summary>添字アクセス。</summary>
/// <param name="target">対象の式。</param>
/// <param name="openBracket">開き角括弧。</param>
/// <param name="index">添字の式。</param>
/// <param name="closeBracket">閉じ角括弧。</param>
public sealed class ElementAccessExpressionSyntax(
    ExpressionSyntax target,
    ShaderSyntaxToken openBracket,
    ExpressionSyntax index,
    ShaderSyntaxToken closeBracket) : ExpressionSyntax
{
    /// <summary>対象の式。</summary>
    public ExpressionSyntax Target { get; } = target;

    /// <summary>開き角括弧。</summary>
    public ShaderSyntaxToken OpenBracket { get; } = openBracket;

    /// <summary>添字の式。</summary>
    public ExpressionSyntax Index { get; } = index;

    /// <summary>閉じ角括弧。</summary>
    public ShaderSyntaxToken CloseBracket { get; } = closeBracket;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Target;
        yield return OpenBracket;
        yield return Index;
        yield return CloseBracket;
    }
}

/// <summary>型変換。</summary>
/// <param name="openParen">開き括弧。</param>
/// <param name="type">変換先の型。</param>
/// <param name="closeParen">閉じ括弧。</param>
/// <param name="operand">変換される式。</param>
public sealed class CastExpressionSyntax(
    ShaderSyntaxToken openParen,
    TypeSyntax type,
    ShaderSyntaxToken closeParen,
    ExpressionSyntax operand) : ExpressionSyntax
{
    /// <summary>開き括弧。</summary>
    public ShaderSyntaxToken OpenParen { get; } = openParen;

    /// <summary>変換先の型。</summary>
    public TypeSyntax Type { get; } = type;

    /// <summary>閉じ括弧。</summary>
    public ShaderSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>変換される式。</summary>
    public ExpressionSyntax Operand { get; } = operand;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return OpenParen;
        yield return Type;
        yield return CloseParen;
        yield return Operand;
    }
}

/// <summary>波括弧による初期化子。</summary>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="elements">要素。区切りのカンマを含む。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
public sealed class InitializerListExpressionSyntax(
    ShaderSyntaxToken openBrace,
    ImmutableArray<ShaderNodeOrTokenEntry> elements,
    ShaderSyntaxToken closeBrace) : ExpressionSyntax
{
    /// <summary>開き波括弧。</summary>
    public ShaderSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>要素。区切りのカンマを含む。</summary>
    public ImmutableArray<ShaderNodeOrTokenEntry> Elements { get; } = elements;

    /// <summary>閉じ波括弧。</summary>
    public ShaderSyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return OpenBrace;

        foreach (ShaderNodeOrTokenEntry element in Elements)
        {
            yield return element.ToNodeOrToken();
        }

        yield return CloseBrace;
    }
}

/// <summary>
/// 配列の型を名前として書いた式。GLSL の配列のコンストラクタ (<c>float[](0.1, 0.2)</c>) の呼び出し先になる。
/// </summary>
/// <param name="elementType">要素の型の名前 (<c>float</c>)。</param>
/// <param name="arrayRanks">配列の指定。角括弧と大きさの式を含む (<c>[]</c> / <c>[3]</c>)。</param>
/// <remarks>HLSL では書けないので、HLSL の木には現れない。</remarks>
public sealed class ArrayTypeExpressionSyntax(
    IdentifierExpressionSyntax elementType,
    ImmutableArray<ShaderSyntaxToken> arrayRanks) : ExpressionSyntax
{
    /// <summary>要素の型の名前。</summary>
    public IdentifierExpressionSyntax ElementType { get; } = elementType;

    /// <summary>配列の指定。</summary>
    public ImmutableArray<ShaderSyntaxToken> ArrayRanks { get; } = arrayRanks;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return ElementType;

        foreach (ShaderSyntaxToken token in ArrayRanks)
        {
            yield return token;
        }
    }
}

/// <summary>解釈できなかった式。</summary>
/// <param name="tokens">読み飛ばしたトークン。</param>
/// <remarks>
/// 構文エラーからの回復で使う。式が必要な位置に何も置かないと
/// 木の形が崩れて後続のルールが場合分けを強いられるため、
/// 「解釈できなかった」ことを表すノードを置く。
/// </remarks>
public sealed class IncompleteExpressionSyntax(ImmutableArray<ShaderSyntaxToken> tokens) : ExpressionSyntax
{
    /// <summary>読み飛ばしたトークン。</summary>
    public ImmutableArray<ShaderSyntaxToken> Tokens { get; } = tokens;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        foreach (ShaderSyntaxToken token in Tokens)
        {
            yield return token;
        }
    }
}

/// <summary>
/// ノードとトークンのどちらかを保持する、配列に格納できる要素。
/// </summary>
/// <remarks>
/// <see cref="ShaderNodeOrToken"/> は構造体で暗黙変換を持つため
/// 配列の要素としては扱いにくい。区切り記号を含む一覧を保持するために用意している。
/// </remarks>
public sealed class ShaderNodeOrTokenEntry
{
    private ShaderNodeOrTokenEntry(ShaderSyntaxNode? node, ShaderSyntaxToken? token)
    {
        Node = node;
        Token = token;
    }

    /// <summary>ノードの場合はその参照。</summary>
    public ShaderSyntaxNode? Node { get; }

    /// <summary>トークンの場合はその参照。</summary>
    public ShaderSyntaxToken? Token { get; }

    /// <summary>ノードを保持する要素を作る。</summary>
    /// <param name="node">保持するノード。</param>
    /// <returns>作成した要素。</returns>
    public static ShaderNodeOrTokenEntry FromNode(ShaderSyntaxNode node) => new(node, null);

    /// <summary>トークンを保持する要素を作る。</summary>
    /// <param name="token">保持するトークン。</param>
    /// <returns>作成した要素。</returns>
    public static ShaderNodeOrTokenEntry FromToken(ShaderSyntaxToken token) => new(null, token);

    /// <summary>木の走査で使う形式へ変換する。</summary>
    /// <returns>変換した値。</returns>
    public ShaderNodeOrToken ToNodeOrToken() => Node is not null ? Node : Token!;
}
