using System.Collections.Immutable;

namespace Shaderlyn.Hlsl.Syntax;

/// <summary>式の基底クラス。</summary>
public abstract class HlslExpressionSyntax : HlslSyntaxNode;

/// <summary>数値・文字列などのリテラル。</summary>
/// <param name="token">リテラルのトークン。</param>
public sealed class LiteralExpressionSyntax(HlslSyntaxToken token) : HlslExpressionSyntax
{
    /// <summary>リテラルのトークン。</summary>
    public HlslSyntaxToken Token { get; } = token;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Token;
    }
}

/// <summary>変数や関数などの名前参照。</summary>
/// <param name="identifier">識別子のトークン。</param>
public sealed class IdentifierExpressionSyntax(HlslSyntaxToken identifier) : HlslExpressionSyntax
{
    /// <summary>識別子のトークン。</summary>
    public HlslSyntaxToken Identifier { get; } = identifier;

    /// <summary>識別子の文字列。</summary>
    public string Name => Identifier.Text;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Identifier;
    }
}

/// <summary>丸括弧で囲まれた式。</summary>
/// <param name="openParen">開き括弧。</param>
/// <param name="expression">中の式。</param>
/// <param name="closeParen">閉じ括弧。</param>
public sealed class ParenthesizedExpressionSyntax(
    HlslSyntaxToken openParen,
    HlslExpressionSyntax expression,
    HlslSyntaxToken closeParen) : HlslExpressionSyntax
{
    /// <summary>開き括弧。</summary>
    public HlslSyntaxToken OpenParen { get; } = openParen;

    /// <summary>中の式。</summary>
    public HlslExpressionSyntax Expression { get; } = expression;

    /// <summary>閉じ括弧。</summary>
    public HlslSyntaxToken CloseParen { get; } = closeParen;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
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
    HlslExpressionSyntax left,
    HlslSyntaxToken operatorToken,
    HlslExpressionSyntax right) : HlslExpressionSyntax
{
    /// <summary>左辺。</summary>
    public HlslExpressionSyntax Left { get; } = left;

    /// <summary>演算子のトークン。</summary>
    public HlslSyntaxToken OperatorToken { get; } = operatorToken;

    /// <summary>右辺。</summary>
    public HlslExpressionSyntax Right { get; } = right;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
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
    HlslExpressionSyntax left,
    HlslSyntaxToken operatorToken,
    HlslExpressionSyntax right) : HlslExpressionSyntax
{
    /// <summary>代入先。</summary>
    public HlslExpressionSyntax Left { get; } = left;

    /// <summary>代入演算子のトークン。</summary>
    public HlslSyntaxToken OperatorToken { get; } = operatorToken;

    /// <summary>代入する値。</summary>
    public HlslExpressionSyntax Right { get; } = right;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
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
    HlslSyntaxToken operatorToken,
    HlslExpressionSyntax operand) : HlslExpressionSyntax
{
    /// <summary>演算子のトークン。</summary>
    public HlslSyntaxToken OperatorToken { get; } = operatorToken;

    /// <summary>被演算子。</summary>
    public HlslExpressionSyntax Operand { get; } = operand;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return OperatorToken;
        yield return Operand;
    }
}

/// <summary>後置単項演算 (<c>++</c> / <c>--</c>)。</summary>
/// <param name="operand">被演算子。</param>
/// <param name="operatorToken">演算子。</param>
public sealed class PostfixUnaryExpressionSyntax(
    HlslExpressionSyntax operand,
    HlslSyntaxToken operatorToken) : HlslExpressionSyntax
{
    /// <summary>被演算子。</summary>
    public HlslExpressionSyntax Operand { get; } = operand;

    /// <summary>演算子のトークン。</summary>
    public HlslSyntaxToken OperatorToken { get; } = operatorToken;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
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
    HlslExpressionSyntax condition,
    HlslSyntaxToken questionToken,
    HlslExpressionSyntax whenTrue,
    HlslSyntaxToken colonToken,
    HlslExpressionSyntax whenFalse) : HlslExpressionSyntax
{
    /// <summary>条件。</summary>
    public HlslExpressionSyntax Condition { get; } = condition;

    /// <summary><c>?</c> のトークン。</summary>
    public HlslSyntaxToken QuestionToken { get; } = questionToken;

    /// <summary>真のときの値。</summary>
    public HlslExpressionSyntax WhenTrue { get; } = whenTrue;

    /// <summary><c>:</c> のトークン。</summary>
    public HlslSyntaxToken ColonToken { get; } = colonToken;

    /// <summary>偽のときの値。</summary>
    public HlslExpressionSyntax WhenFalse { get; } = whenFalse;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
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
/// <remarks>
/// <c>float4(1, 0, 0, 1)</c> のような型のコンストラクタも同じ形で表す。
/// 構文の上では関数呼び出しと区別がつかず、
/// 区別するには型の情報が必要なため意味解析層の仕事になる。
/// </remarks>
public sealed class InvocationExpressionSyntax(
    HlslExpressionSyntax target,
    HlslSyntaxToken openParen,
    ImmutableArray<HlslNodeOrTokenEntry> arguments,
    HlslSyntaxToken closeParen) : HlslExpressionSyntax
{
    /// <summary>呼び出す対象。</summary>
    public HlslExpressionSyntax Target { get; } = target;

    /// <summary>開き括弧。</summary>
    public HlslSyntaxToken OpenParen { get; } = openParen;

    /// <summary>引数。区切りのカンマを含む。</summary>
    public ImmutableArray<HlslNodeOrTokenEntry> Arguments { get; } = arguments;

    /// <summary>閉じ括弧。</summary>
    public HlslSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>区切りのカンマを除いた引数の式。</summary>
    public IEnumerable<HlslExpressionSyntax> ArgumentExpressions
        => Arguments.Select(a => a.Node).OfType<HlslExpressionSyntax>();

    /// <summary>呼び出し対象が単純な名前の場合、その名前。それ以外は <see langword="null"/>。</summary>
    /// <remarks>「この関数は使わない」というルールが対象を特定するために使う。</remarks>
    public string? TargetName => (Target as IdentifierExpressionSyntax)?.Name;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Target;
        yield return OpenParen;

        foreach (HlslNodeOrTokenEntry argument in Arguments)
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
    HlslExpressionSyntax target,
    HlslSyntaxToken dotToken,
    HlslSyntaxToken nameToken) : HlslExpressionSyntax
{
    /// <summary>対象の式。</summary>
    public HlslExpressionSyntax Target { get; } = target;

    /// <summary><c>.</c> のトークン。</summary>
    public HlslSyntaxToken DotToken { get; } = dotToken;

    /// <summary>メンバー名のトークン。</summary>
    public HlslSyntaxToken NameToken { get; } = nameToken;

    /// <summary>メンバー名。</summary>
    public string Name => NameToken.Text;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
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
    HlslExpressionSyntax target,
    HlslSyntaxToken openBracket,
    HlslExpressionSyntax index,
    HlslSyntaxToken closeBracket) : HlslExpressionSyntax
{
    /// <summary>対象の式。</summary>
    public HlslExpressionSyntax Target { get; } = target;

    /// <summary>開き角括弧。</summary>
    public HlslSyntaxToken OpenBracket { get; } = openBracket;

    /// <summary>添字の式。</summary>
    public HlslExpressionSyntax Index { get; } = index;

    /// <summary>閉じ角括弧。</summary>
    public HlslSyntaxToken CloseBracket { get; } = closeBracket;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
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
    HlslSyntaxToken openParen,
    HlslTypeSyntax type,
    HlslSyntaxToken closeParen,
    HlslExpressionSyntax operand) : HlslExpressionSyntax
{
    /// <summary>開き括弧。</summary>
    public HlslSyntaxToken OpenParen { get; } = openParen;

    /// <summary>変換先の型。</summary>
    public HlslTypeSyntax Type { get; } = type;

    /// <summary>閉じ括弧。</summary>
    public HlslSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>変換される式。</summary>
    public HlslExpressionSyntax Operand { get; } = operand;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
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
    HlslSyntaxToken openBrace,
    ImmutableArray<HlslNodeOrTokenEntry> elements,
    HlslSyntaxToken closeBrace) : HlslExpressionSyntax
{
    /// <summary>開き波括弧。</summary>
    public HlslSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>要素。区切りのカンマを含む。</summary>
    public ImmutableArray<HlslNodeOrTokenEntry> Elements { get; } = elements;

    /// <summary>閉じ波括弧。</summary>
    public HlslSyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return OpenBrace;

        foreach (HlslNodeOrTokenEntry element in Elements)
        {
            yield return element.ToNodeOrToken();
        }

        yield return CloseBrace;
    }
}

/// <summary>解釈できなかった式。</summary>
/// <param name="tokens">読み飛ばしたトークン。</param>
/// <remarks>
/// 構文エラーからの回復で使う。式が必要な位置に何も置かないと
/// 木の形が崩れて後続のルールが場合分けを強いられるため、
/// 「解釈できなかった」ことを表すノードを置く。
/// </remarks>
public sealed class IncompleteExpressionSyntax(ImmutableArray<HlslSyntaxToken> tokens) : HlslExpressionSyntax
{
    /// <summary>読み飛ばしたトークン。</summary>
    public ImmutableArray<HlslSyntaxToken> Tokens { get; } = tokens;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslSyntaxToken token in Tokens)
        {
            yield return token;
        }
    }
}

/// <summary>
/// ノードとトークンのどちらかを保持する、配列に格納できる要素。
/// </summary>
/// <remarks>
/// <see cref="HlslNodeOrToken"/> は構造体で暗黙変換を持つため
/// 配列の要素としては扱いにくい。区切り記号を含む一覧を保持するために用意している。
/// </remarks>
public sealed class HlslNodeOrTokenEntry
{
    private HlslNodeOrTokenEntry(HlslSyntaxNode? node, HlslSyntaxToken? token)
    {
        Node = node;
        Token = token;
    }

    /// <summary>ノードの場合はその参照。</summary>
    public HlslSyntaxNode? Node { get; }

    /// <summary>トークンの場合はその参照。</summary>
    public HlslSyntaxToken? Token { get; }

    /// <summary>ノードを保持する要素を作る。</summary>
    /// <param name="node">保持するノード。</param>
    /// <returns>作成した要素。</returns>
    public static HlslNodeOrTokenEntry FromNode(HlslSyntaxNode node) => new(node, null);

    /// <summary>トークンを保持する要素を作る。</summary>
    /// <param name="token">保持するトークン。</param>
    /// <returns>作成した要素。</returns>
    public static HlslNodeOrTokenEntry FromToken(HlslSyntaxToken token) => new(null, token);

    /// <summary>木の走査で使う形式へ変換する。</summary>
    /// <returns>変換した値。</returns>
    public HlslNodeOrToken ToNodeOrToken() => Node is not null ? Node : Token!;
}
