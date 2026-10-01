using System.Collections.Immutable;

namespace Shaderlyn.Hlsl.Syntax;

/// <summary>文の基底クラス。</summary>
public abstract class HlslStatementSyntax : HlslSyntaxNode;

/// <summary>波括弧で囲まれた文の並び。</summary>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="statements">中の文。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
public sealed class BlockStatementSyntax(
    HlslSyntaxToken openBrace,
    ImmutableArray<HlslStatementSyntax> statements,
    HlslSyntaxToken closeBrace) : HlslStatementSyntax
{
    /// <summary>開き波括弧。</summary>
    public HlslSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>中の文。</summary>
    public ImmutableArray<HlslStatementSyntax> Statements { get; } = statements;

    /// <summary>閉じ波括弧。</summary>
    public HlslSyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return OpenBrace;

        foreach (HlslStatementSyntax statement in Statements)
        {
            yield return statement;
        }

        yield return CloseBrace;
    }
}

/// <summary>式を評価するだけの文。</summary>
/// <param name="expression">式。</param>
/// <param name="semicolonToken">終端のセミコロン。</param>
public sealed class ExpressionStatementSyntax(
    HlslExpressionSyntax expression,
    HlslSyntaxToken semicolonToken) : HlslStatementSyntax
{
    /// <summary>式。</summary>
    public HlslExpressionSyntax Expression { get; } = expression;

    /// <summary>終端のセミコロン。</summary>
    public HlslSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Expression;
        yield return SemicolonToken;
    }
}

/// <summary>局所変数の宣言。</summary>
/// <param name="declaration">変数宣言。</param>
public sealed class LocalDeclarationStatementSyntax(HlslDeclarationSyntax declaration) : HlslStatementSyntax
{
    /// <summary>変数宣言。</summary>
    public HlslDeclarationSyntax Declaration { get; } = declaration;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Declaration;
    }
}

/// <summary><c>if</c> 文。</summary>
/// <param name="attributes">属性 (<c>[branch]</c> など)。</param>
/// <param name="ifKeyword"><c>if</c> キーワード。</param>
/// <param name="openParen">開き括弧。</param>
/// <param name="condition">条件式。</param>
/// <param name="closeParen">閉じ括弧。</param>
/// <param name="thenStatement">真のときの文。</param>
/// <param name="elseKeyword"><c>else</c> キーワード。無い場合は <see langword="null"/>。</param>
/// <param name="elseStatement">偽のときの文。無い場合は <see langword="null"/>。</param>
public sealed class IfStatementSyntax(
    ImmutableArray<HlslAttributeSyntax> attributes,
    HlslSyntaxToken ifKeyword,
    HlslSyntaxToken openParen,
    HlslExpressionSyntax condition,
    HlslSyntaxToken closeParen,
    HlslStatementSyntax thenStatement,
    HlslSyntaxToken? elseKeyword,
    HlslStatementSyntax? elseStatement) : HlslStatementSyntax
{
    /// <summary>属性。</summary>
    public ImmutableArray<HlslAttributeSyntax> Attributes { get; } = attributes;

    /// <summary><c>if</c> キーワード。</summary>
    public HlslSyntaxToken IfKeyword { get; } = ifKeyword;

    /// <summary>開き括弧。</summary>
    public HlslSyntaxToken OpenParen { get; } = openParen;

    /// <summary>条件式。</summary>
    public HlslExpressionSyntax Condition { get; } = condition;

    /// <summary>閉じ括弧。</summary>
    public HlslSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>真のときの文。</summary>
    public HlslStatementSyntax ThenStatement { get; } = thenStatement;

    /// <summary><c>else</c> キーワード。</summary>
    public HlslSyntaxToken? ElseKeyword { get; } = elseKeyword;

    /// <summary>偽のときの文。</summary>
    public HlslStatementSyntax? ElseStatement { get; } = elseStatement;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslAttributeSyntax attribute in Attributes)
        {
            yield return attribute;
        }

        yield return IfKeyword;
        yield return OpenParen;
        yield return Condition;
        yield return CloseParen;
        yield return ThenStatement;

        if (ElseKeyword is not null)
        {
            yield return ElseKeyword;
        }

        if (ElseStatement is not null)
        {
            yield return ElseStatement;
        }
    }
}

/// <summary><c>for</c> 文。</summary>
/// <param name="attributes">属性 (<c>[unroll]</c> など)。</param>
/// <param name="forKeyword"><c>for</c> キーワード。</param>
/// <param name="openParen">開き括弧。</param>
/// <param name="initializer">初期化部。無い場合は <see langword="null"/>。</param>
/// <param name="firstSemicolon">1 つ目のセミコロン。</param>
/// <param name="condition">条件式。無い場合は <see langword="null"/>。</param>
/// <param name="secondSemicolon">2 つ目のセミコロン。</param>
/// <param name="incrementors">更新部。区切りのカンマを含む。</param>
/// <param name="closeParen">閉じ括弧。</param>
/// <param name="body">本体。</param>
public sealed class ForStatementSyntax(
    ImmutableArray<HlslAttributeSyntax> attributes,
    HlslSyntaxToken forKeyword,
    HlslSyntaxToken openParen,
    HlslSyntaxNode? initializer,
    HlslSyntaxToken? firstSemicolon,
    HlslExpressionSyntax? condition,
    HlslSyntaxToken secondSemicolon,
    ImmutableArray<HlslNodeOrTokenEntry> incrementors,
    HlslSyntaxToken closeParen,
    HlslStatementSyntax body) : HlslStatementSyntax
{
    /// <summary>属性。</summary>
    public ImmutableArray<HlslAttributeSyntax> Attributes { get; } = attributes;

    /// <summary><c>for</c> キーワード。</summary>
    public HlslSyntaxToken ForKeyword { get; } = forKeyword;

    /// <summary>開き括弧。</summary>
    public HlslSyntaxToken OpenParen { get; } = openParen;

    /// <summary>初期化部。</summary>
    public HlslSyntaxNode? Initializer { get; } = initializer;

    /// <summary>1 つ目のセミコロン。初期化部が宣言の場合はそちらに含まれる。</summary>
    public HlslSyntaxToken? FirstSemicolon { get; } = firstSemicolon;

    /// <summary>条件式。</summary>
    public HlslExpressionSyntax? Condition { get; } = condition;

    /// <summary>2 つ目のセミコロン。</summary>
    public HlslSyntaxToken SecondSemicolon { get; } = secondSemicolon;

    /// <summary>更新部。</summary>
    public ImmutableArray<HlslNodeOrTokenEntry> Incrementors { get; } = incrementors;

    /// <summary>閉じ括弧。</summary>
    public HlslSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>本体。</summary>
    public HlslStatementSyntax Body { get; } = body;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslAttributeSyntax attribute in Attributes)
        {
            yield return attribute;
        }

        yield return ForKeyword;
        yield return OpenParen;

        if (Initializer is not null)
        {
            yield return Initializer;
        }

        if (FirstSemicolon is not null)
        {
            yield return FirstSemicolon;
        }

        if (Condition is not null)
        {
            yield return Condition;
        }

        yield return SecondSemicolon;

        foreach (HlslNodeOrTokenEntry incrementor in Incrementors)
        {
            yield return incrementor.ToNodeOrToken();
        }

        yield return CloseParen;
        yield return Body;
    }
}

/// <summary><c>while</c> 文。</summary>
/// <param name="attributes">属性。</param>
/// <param name="whileKeyword"><c>while</c> キーワード。</param>
/// <param name="openParen">開き括弧。</param>
/// <param name="condition">条件式。</param>
/// <param name="closeParen">閉じ括弧。</param>
/// <param name="body">本体。</param>
public sealed class WhileStatementSyntax(
    ImmutableArray<HlslAttributeSyntax> attributes,
    HlslSyntaxToken whileKeyword,
    HlslSyntaxToken openParen,
    HlslExpressionSyntax condition,
    HlslSyntaxToken closeParen,
    HlslStatementSyntax body) : HlslStatementSyntax
{
    /// <summary>属性。</summary>
    public ImmutableArray<HlslAttributeSyntax> Attributes { get; } = attributes;

    /// <summary><c>while</c> キーワード。</summary>
    public HlslSyntaxToken WhileKeyword { get; } = whileKeyword;

    /// <summary>開き括弧。</summary>
    public HlslSyntaxToken OpenParen { get; } = openParen;

    /// <summary>条件式。</summary>
    public HlslExpressionSyntax Condition { get; } = condition;

    /// <summary>閉じ括弧。</summary>
    public HlslSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>本体。</summary>
    public HlslStatementSyntax Body { get; } = body;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslAttributeSyntax attribute in Attributes)
        {
            yield return attribute;
        }

        yield return WhileKeyword;
        yield return OpenParen;
        yield return Condition;
        yield return CloseParen;
        yield return Body;
    }
}

/// <summary><c>do while</c> 文。</summary>
/// <param name="attributes">属性。</param>
/// <param name="doKeyword"><c>do</c> キーワード。</param>
/// <param name="body">本体。</param>
/// <param name="whileKeyword"><c>while</c> キーワード。</param>
/// <param name="openParen">開き括弧。</param>
/// <param name="condition">条件式。</param>
/// <param name="closeParen">閉じ括弧。</param>
/// <param name="semicolonToken">終端のセミコロン。</param>
public sealed class DoWhileStatementSyntax(
    ImmutableArray<HlslAttributeSyntax> attributes,
    HlslSyntaxToken doKeyword,
    HlslStatementSyntax body,
    HlslSyntaxToken whileKeyword,
    HlslSyntaxToken openParen,
    HlslExpressionSyntax condition,
    HlslSyntaxToken closeParen,
    HlslSyntaxToken semicolonToken) : HlslStatementSyntax
{
    /// <summary>属性。</summary>
    public ImmutableArray<HlslAttributeSyntax> Attributes { get; } = attributes;

    /// <summary><c>do</c> キーワード。</summary>
    public HlslSyntaxToken DoKeyword { get; } = doKeyword;

    /// <summary>本体。</summary>
    public HlslStatementSyntax Body { get; } = body;

    /// <summary><c>while</c> キーワード。</summary>
    public HlslSyntaxToken WhileKeyword { get; } = whileKeyword;

    /// <summary>開き括弧。</summary>
    public HlslSyntaxToken OpenParen { get; } = openParen;

    /// <summary>条件式。</summary>
    public HlslExpressionSyntax Condition { get; } = condition;

    /// <summary>閉じ括弧。</summary>
    public HlslSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>終端のセミコロン。</summary>
    public HlslSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslAttributeSyntax attribute in Attributes)
        {
            yield return attribute;
        }

        yield return DoKeyword;
        yield return Body;
        yield return WhileKeyword;
        yield return OpenParen;
        yield return Condition;
        yield return CloseParen;
        yield return SemicolonToken;
    }
}

/// <summary><c>switch</c> 文。</summary>
/// <param name="attributes">属性。</param>
/// <param name="switchKeyword"><c>switch</c> キーワード。</param>
/// <param name="openParen">開き括弧。</param>
/// <param name="expression">対象の式。</param>
/// <param name="closeParen">閉じ括弧。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="statements">中の文。<c>case</c> ラベルを含む。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
public sealed class SwitchStatementSyntax(
    ImmutableArray<HlslAttributeSyntax> attributes,
    HlslSyntaxToken switchKeyword,
    HlslSyntaxToken openParen,
    HlslExpressionSyntax expression,
    HlslSyntaxToken closeParen,
    HlslSyntaxToken openBrace,
    ImmutableArray<HlslStatementSyntax> statements,
    HlslSyntaxToken closeBrace) : HlslStatementSyntax
{
    /// <summary>属性。</summary>
    public ImmutableArray<HlslAttributeSyntax> Attributes { get; } = attributes;

    /// <summary><c>switch</c> キーワード。</summary>
    public HlslSyntaxToken SwitchKeyword { get; } = switchKeyword;

    /// <summary>開き括弧。</summary>
    public HlslSyntaxToken OpenParen { get; } = openParen;

    /// <summary>対象の式。</summary>
    public HlslExpressionSyntax Expression { get; } = expression;

    /// <summary>閉じ括弧。</summary>
    public HlslSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>開き波括弧。</summary>
    public HlslSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>中の文。</summary>
    public ImmutableArray<HlslStatementSyntax> Statements { get; } = statements;

    /// <summary>閉じ波括弧。</summary>
    public HlslSyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        foreach (HlslAttributeSyntax attribute in Attributes)
        {
            yield return attribute;
        }

        yield return SwitchKeyword;
        yield return OpenParen;
        yield return Expression;
        yield return CloseParen;
        yield return OpenBrace;

        foreach (HlslStatementSyntax statement in Statements)
        {
            yield return statement;
        }

        yield return CloseBrace;
    }
}

/// <summary><c>case</c> または <c>default</c> のラベル。</summary>
/// <param name="keyword">キーワード。</param>
/// <param name="value"><c>case</c> の値。<c>default</c> の場合は <see langword="null"/>。</param>
/// <param name="colonToken"><c>:</c>。</param>
public sealed class SwitchLabelStatementSyntax(
    HlslSyntaxToken keyword,
    HlslExpressionSyntax? value,
    HlslSyntaxToken colonToken) : HlslStatementSyntax
{
    /// <summary>キーワードのトークン。</summary>
    public HlslSyntaxToken Keyword { get; } = keyword;

    /// <summary><c>case</c> の値。</summary>
    public HlslExpressionSyntax? Value { get; } = value;

    /// <summary><c>:</c> のトークン。</summary>
    public HlslSyntaxToken ColonToken { get; } = colonToken;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;

        if (Value is not null)
        {
            yield return Value;
        }

        yield return ColonToken;
    }
}

/// <summary><c>return</c> 文。</summary>
/// <param name="keyword"><c>return</c> キーワード。</param>
/// <param name="expression">戻り値の式。無い場合は <see langword="null"/>。</param>
/// <param name="semicolonToken">終端のセミコロン。</param>
public sealed class ReturnStatementSyntax(
    HlslSyntaxToken keyword,
    HlslExpressionSyntax? expression,
    HlslSyntaxToken semicolonToken) : HlslStatementSyntax
{
    /// <summary><c>return</c> キーワード。</summary>
    public HlslSyntaxToken Keyword { get; } = keyword;

    /// <summary>戻り値の式。</summary>
    public HlslExpressionSyntax? Expression { get; } = expression;

    /// <summary>終端のセミコロン。</summary>
    public HlslSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;

        if (Expression is not null)
        {
            yield return Expression;
        }

        yield return SemicolonToken;
    }
}

/// <summary><c>break</c> / <c>continue</c> / <c>discard</c> 文。</summary>
/// <param name="keyword">キーワード。</param>
/// <param name="semicolonToken">終端のセミコロン。</param>
public sealed class JumpStatementSyntax(
    HlslSyntaxToken keyword,
    HlslSyntaxToken semicolonToken) : HlslStatementSyntax
{
    /// <summary>キーワードのトークン。</summary>
    public HlslSyntaxToken Keyword { get; } = keyword;

    /// <summary>終端のセミコロン。</summary>
    public HlslSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;
        yield return SemicolonToken;
    }
}

/// <summary>セミコロンだけの空文。</summary>
/// <param name="semicolonToken">セミコロン。</param>
public sealed class EmptyStatementSyntax(HlslSyntaxToken semicolonToken) : HlslStatementSyntax
{
    /// <summary>セミコロン。</summary>
    public HlslSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<HlslNodeOrToken> ChildNodesAndTokens()
    {
        yield return SemicolonToken;
    }
}

/// <summary>解釈できなかった文。</summary>
/// <param name="tokens">読み飛ばしたトークン。</param>
public sealed class IncompleteStatementSyntax(ImmutableArray<HlslSyntaxToken> tokens) : HlslStatementSyntax
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
