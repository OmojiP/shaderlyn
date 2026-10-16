using System.Collections.Immutable;

namespace Shaderlyn.CFamily.Syntax;

/// <summary>文の基底クラス。</summary>
public abstract class StatementSyntax : ShaderSyntaxNode;

/// <summary>波括弧で囲まれた文の並び。</summary>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="statements">中の文。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
public sealed class BlockStatementSyntax(
    ShaderSyntaxToken openBrace,
    ImmutableArray<StatementSyntax> statements,
    ShaderSyntaxToken closeBrace) : StatementSyntax
{
    /// <summary>開き波括弧。</summary>
    public ShaderSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>中の文。</summary>
    public ImmutableArray<StatementSyntax> Statements { get; } = statements;

    /// <summary>閉じ波括弧。</summary>
    public ShaderSyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return OpenBrace;

        foreach (StatementSyntax statement in Statements)
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
    ExpressionSyntax expression,
    ShaderSyntaxToken semicolonToken) : StatementSyntax
{
    /// <summary>式。</summary>
    public ExpressionSyntax Expression { get; } = expression;

    /// <summary>終端のセミコロン。</summary>
    public ShaderSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Expression;
        yield return SemicolonToken;
    }
}

/// <summary>局所変数の宣言。</summary>
/// <param name="declaration">変数宣言。</param>
public sealed class LocalDeclarationStatementSyntax(DeclarationSyntax declaration) : StatementSyntax
{
    /// <summary>変数宣言。</summary>
    public DeclarationSyntax Declaration { get; } = declaration;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
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
    ImmutableArray<AttributeSyntax> attributes,
    ShaderSyntaxToken ifKeyword,
    ShaderSyntaxToken openParen,
    ExpressionSyntax condition,
    ShaderSyntaxToken closeParen,
    StatementSyntax thenStatement,
    ShaderSyntaxToken? elseKeyword,
    StatementSyntax? elseStatement) : StatementSyntax
{
    /// <summary>属性。</summary>
    public ImmutableArray<AttributeSyntax> Attributes { get; } = attributes;

    /// <summary><c>if</c> キーワード。</summary>
    public ShaderSyntaxToken IfKeyword { get; } = ifKeyword;

    /// <summary>開き括弧。</summary>
    public ShaderSyntaxToken OpenParen { get; } = openParen;

    /// <summary>条件式。</summary>
    public ExpressionSyntax Condition { get; } = condition;

    /// <summary>閉じ括弧。</summary>
    public ShaderSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>真のときの文。</summary>
    public StatementSyntax ThenStatement { get; } = thenStatement;

    /// <summary><c>else</c> キーワード。</summary>
    public ShaderSyntaxToken? ElseKeyword { get; } = elseKeyword;

    /// <summary>偽のときの文。</summary>
    public StatementSyntax? ElseStatement { get; } = elseStatement;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        foreach (AttributeSyntax attribute in Attributes)
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
    ImmutableArray<AttributeSyntax> attributes,
    ShaderSyntaxToken forKeyword,
    ShaderSyntaxToken openParen,
    ShaderSyntaxNode? initializer,
    ShaderSyntaxToken? firstSemicolon,
    ExpressionSyntax? condition,
    ShaderSyntaxToken secondSemicolon,
    ImmutableArray<ShaderNodeOrTokenEntry> incrementors,
    ShaderSyntaxToken closeParen,
    StatementSyntax body) : StatementSyntax
{
    /// <summary>属性。</summary>
    public ImmutableArray<AttributeSyntax> Attributes { get; } = attributes;

    /// <summary><c>for</c> キーワード。</summary>
    public ShaderSyntaxToken ForKeyword { get; } = forKeyword;

    /// <summary>開き括弧。</summary>
    public ShaderSyntaxToken OpenParen { get; } = openParen;

    /// <summary>初期化部。</summary>
    public ShaderSyntaxNode? Initializer { get; } = initializer;

    /// <summary>1 つ目のセミコロン。初期化部が宣言の場合はそちらに含まれる。</summary>
    public ShaderSyntaxToken? FirstSemicolon { get; } = firstSemicolon;

    /// <summary>条件式。</summary>
    public ExpressionSyntax? Condition { get; } = condition;

    /// <summary>2 つ目のセミコロン。</summary>
    public ShaderSyntaxToken SecondSemicolon { get; } = secondSemicolon;

    /// <summary>更新部。</summary>
    public ImmutableArray<ShaderNodeOrTokenEntry> Incrementors { get; } = incrementors;

    /// <summary>閉じ括弧。</summary>
    public ShaderSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>本体。</summary>
    public StatementSyntax Body { get; } = body;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        foreach (AttributeSyntax attribute in Attributes)
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

        foreach (ShaderNodeOrTokenEntry incrementor in Incrementors)
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
    ImmutableArray<AttributeSyntax> attributes,
    ShaderSyntaxToken whileKeyword,
    ShaderSyntaxToken openParen,
    ExpressionSyntax condition,
    ShaderSyntaxToken closeParen,
    StatementSyntax body) : StatementSyntax
{
    /// <summary>属性。</summary>
    public ImmutableArray<AttributeSyntax> Attributes { get; } = attributes;

    /// <summary><c>while</c> キーワード。</summary>
    public ShaderSyntaxToken WhileKeyword { get; } = whileKeyword;

    /// <summary>開き括弧。</summary>
    public ShaderSyntaxToken OpenParen { get; } = openParen;

    /// <summary>条件式。</summary>
    public ExpressionSyntax Condition { get; } = condition;

    /// <summary>閉じ括弧。</summary>
    public ShaderSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>本体。</summary>
    public StatementSyntax Body { get; } = body;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        foreach (AttributeSyntax attribute in Attributes)
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
    ImmutableArray<AttributeSyntax> attributes,
    ShaderSyntaxToken doKeyword,
    StatementSyntax body,
    ShaderSyntaxToken whileKeyword,
    ShaderSyntaxToken openParen,
    ExpressionSyntax condition,
    ShaderSyntaxToken closeParen,
    ShaderSyntaxToken semicolonToken) : StatementSyntax
{
    /// <summary>属性。</summary>
    public ImmutableArray<AttributeSyntax> Attributes { get; } = attributes;

    /// <summary><c>do</c> キーワード。</summary>
    public ShaderSyntaxToken DoKeyword { get; } = doKeyword;

    /// <summary>本体。</summary>
    public StatementSyntax Body { get; } = body;

    /// <summary><c>while</c> キーワード。</summary>
    public ShaderSyntaxToken WhileKeyword { get; } = whileKeyword;

    /// <summary>開き括弧。</summary>
    public ShaderSyntaxToken OpenParen { get; } = openParen;

    /// <summary>条件式。</summary>
    public ExpressionSyntax Condition { get; } = condition;

    /// <summary>閉じ括弧。</summary>
    public ShaderSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>終端のセミコロン。</summary>
    public ShaderSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        foreach (AttributeSyntax attribute in Attributes)
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
    ImmutableArray<AttributeSyntax> attributes,
    ShaderSyntaxToken switchKeyword,
    ShaderSyntaxToken openParen,
    ExpressionSyntax expression,
    ShaderSyntaxToken closeParen,
    ShaderSyntaxToken openBrace,
    ImmutableArray<StatementSyntax> statements,
    ShaderSyntaxToken closeBrace) : StatementSyntax
{
    /// <summary>属性。</summary>
    public ImmutableArray<AttributeSyntax> Attributes { get; } = attributes;

    /// <summary><c>switch</c> キーワード。</summary>
    public ShaderSyntaxToken SwitchKeyword { get; } = switchKeyword;

    /// <summary>開き括弧。</summary>
    public ShaderSyntaxToken OpenParen { get; } = openParen;

    /// <summary>対象の式。</summary>
    public ExpressionSyntax Expression { get; } = expression;

    /// <summary>閉じ括弧。</summary>
    public ShaderSyntaxToken CloseParen { get; } = closeParen;

    /// <summary>開き波括弧。</summary>
    public ShaderSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>中の文。</summary>
    public ImmutableArray<StatementSyntax> Statements { get; } = statements;

    /// <summary>閉じ波括弧。</summary>
    public ShaderSyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        foreach (AttributeSyntax attribute in Attributes)
        {
            yield return attribute;
        }

        yield return SwitchKeyword;
        yield return OpenParen;
        yield return Expression;
        yield return CloseParen;
        yield return OpenBrace;

        foreach (StatementSyntax statement in Statements)
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
    ShaderSyntaxToken keyword,
    ExpressionSyntax? value,
    ShaderSyntaxToken colonToken) : StatementSyntax
{
    /// <summary>キーワードのトークン。</summary>
    public ShaderSyntaxToken Keyword { get; } = keyword;

    /// <summary><c>case</c> の値。</summary>
    public ExpressionSyntax? Value { get; } = value;

    /// <summary><c>:</c> のトークン。</summary>
    public ShaderSyntaxToken ColonToken { get; } = colonToken;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
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
    ShaderSyntaxToken keyword,
    ExpressionSyntax? expression,
    ShaderSyntaxToken semicolonToken) : StatementSyntax
{
    /// <summary><c>return</c> キーワード。</summary>
    public ShaderSyntaxToken Keyword { get; } = keyword;

    /// <summary>戻り値の式。</summary>
    public ExpressionSyntax? Expression { get; } = expression;

    /// <summary>終端のセミコロン。</summary>
    public ShaderSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
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
    ShaderSyntaxToken keyword,
    ShaderSyntaxToken semicolonToken) : StatementSyntax
{
    /// <summary>キーワードのトークン。</summary>
    public ShaderSyntaxToken Keyword { get; } = keyword;

    /// <summary>終端のセミコロン。</summary>
    public ShaderSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;
        yield return SemicolonToken;
    }
}

/// <summary>セミコロンだけの空文。</summary>
/// <param name="semicolonToken">セミコロン。</param>
public sealed class EmptyStatementSyntax(ShaderSyntaxToken semicolonToken) : StatementSyntax
{
    /// <summary>セミコロン。</summary>
    public ShaderSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return SemicolonToken;
    }
}

/// <summary>解釈できなかった文。</summary>
/// <param name="tokens">読み飛ばしたトークン。</param>
public sealed class IncompleteStatementSyntax(ImmutableArray<ShaderSyntaxToken> tokens) : StatementSyntax
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
