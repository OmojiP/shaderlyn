using System.Collections.Immutable;
using Shaderlyn.Core.Text;

namespace Shaderlyn.ShaderLab.Syntax;

/// <summary>
/// ShaderLab ファイル全体を表す根ノード。
/// </summary>
/// <param name="shader">ファイル先頭の <c>Shader</c> 宣言。存在しない場合は <see langword="null"/>。</param>
/// <param name="leadingSkippedTokens">
/// <c>Shader</c> 宣言より前にあった、解釈できないトークン。
/// </param>
/// <param name="trailingSkippedTokens">
/// <c>Shader</c> 宣言より後にあった、解釈できないトークン。
/// </param>
/// <param name="endOfFileToken">ファイル終端トークン。</param>
/// <remarks>
/// 1 ファイルに <c>Shader</c> 宣言は 1 つだけ置ける。
/// 2 つ目以降は <paramref name="trailingSkippedTokens"/> へ回収され、構文エラーとして報告される。
/// </remarks>
public sealed class ShaderLabCompilationUnitSyntax(
    ShaderDeclarationSyntax? shader,
    SkippedTokensSyntax? leadingSkippedTokens,
    SkippedTokensSyntax? trailingSkippedTokens,
    SyntaxToken endOfFileToken) : ShaderLabSyntaxNode
{
    /// <summary>ファイル先頭の <c>Shader</c> 宣言。</summary>
    public ShaderDeclarationSyntax? Shader { get; } = shader;

    /// <summary><c>Shader</c> 宣言より前にあった、解釈できないトークン。</summary>
    public SkippedTokensSyntax? LeadingSkippedTokens { get; } = leadingSkippedTokens;

    /// <summary><c>Shader</c> 宣言より後にあった、解釈できないトークン。</summary>
    public SkippedTokensSyntax? TrailingSkippedTokens { get; } = trailingSkippedTokens;

    /// <summary>ファイル終端トークン。</summary>
    public SyntaxToken EndOfFileToken { get; } = endOfFileToken;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        if (LeadingSkippedTokens is not null)
        {
            yield return LeadingSkippedTokens;
        }

        if (Shader is not null)
        {
            yield return Shader;
        }

        if (TrailingSkippedTokens is not null)
        {
            yield return TrailingSkippedTokens;
        }

        yield return EndOfFileToken;
    }
}

/// <summary>
/// <c>Shader "名前" { ... }</c> の宣言。
/// </summary>
/// <param name="shaderKeyword"><c>Shader</c> キーワード。</param>
/// <param name="nameToken">シェーダー名の文字列リテラル。</param>
/// <param name="body">波括弧で囲まれた本体。</param>
public sealed class ShaderDeclarationSyntax(
    SyntaxToken shaderKeyword,
    SyntaxToken nameToken,
    BlockSyntax body) : ShaderLabSyntaxNode
{
    /// <summary><c>Shader</c> キーワード。</summary>
    public SyntaxToken ShaderKeyword { get; } = shaderKeyword;

    /// <summary>シェーダー名の文字列リテラル。</summary>
    public SyntaxToken NameToken { get; } = nameToken;

    /// <summary>
    /// シェーダー名。引用符は含まない。
    /// </summary>
    /// <remarks>
    /// Unity ではこの文字列がスラッシュ区切りのメニュー階層として解釈される
    /// (例: <c>Universal Render Pipeline/Lit</c>)。
    /// </remarks>
    public string Name => NameToken.ValueText;

    /// <summary>波括弧で囲まれた本体。</summary>
    public BlockSyntax Body { get; } = body;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return ShaderKeyword;
        yield return NameToken;
        yield return Body;
    }
}

/// <summary>
/// 波括弧で囲まれた文の並び。
/// </summary>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="statements">中に含まれる文。</param>
/// <param name="closeBrace">閉じ波括弧。欠落している場合がある。</param>
/// <remarks>
/// <c>Shader</c> / <c>SubShader</c> / <c>Pass</c> / <c>Category</c> の本体を共通で表す。
/// ShaderLab は階層ごとに書ける命令が異なるが、構文としての形は同じなので木の上では区別せず、
/// 「この階層に書けない命令」の検査はルール側で親を辿って行う。
/// </remarks>
public sealed class BlockSyntax(
    SyntaxToken openBrace,
    ImmutableArray<ShaderLabStatementSyntax> statements,
    SyntaxToken closeBrace) : ShaderLabSyntaxNode
{
    /// <summary>開き波括弧。</summary>
    public SyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>中に含まれる文。</summary>
    public ImmutableArray<ShaderLabStatementSyntax> Statements { get; } = statements;

    /// <summary>閉じ波括弧。</summary>
    public SyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return OpenBrace;

        foreach (ShaderLabStatementSyntax statement in Statements)
        {
            yield return statement;
        }

        yield return CloseBrace;
    }
}

/// <summary>
/// ブロックの中に書ける文の基底クラス。
/// </summary>
public abstract class ShaderLabStatementSyntax : ShaderLabSyntaxNode;

/// <summary>
/// 解釈できなかったトークンの並び。
/// </summary>
/// <param name="tokens">読み飛ばしたトークン。</param>
/// <remarks>
/// <para>
/// 構文エラーからの回復時に読み飛ばしたトークンを、捨てずにここへ集める。
/// 木の上に残すことで、壊れた入力に対してもラウンドトリップが成立する。
/// </para>
/// <para>
/// トークンを本当に捨ててしまうと、将来フォーマッタや自動修正を作る際に
/// 「元のテキストを復元できない」という形で破綻する。
/// </para>
/// </remarks>
public sealed class SkippedTokensSyntax(ImmutableArray<SyntaxToken> tokens) : ShaderLabStatementSyntax
{
    /// <summary>読み飛ばしたトークン。</summary>
    public ImmutableArray<SyntaxToken> Tokens { get; } = tokens;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        foreach (SyntaxToken token in Tokens)
        {
            yield return token;
        }
    }
}

/// <summary>
/// <c>Properties { ... }</c> ブロック。
/// </summary>
/// <param name="keyword"><c>Properties</c> キーワード。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="properties">宣言されたプロパティ。</param>
/// <param name="skippedTokens">解釈できなかったトークン。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
public sealed class PropertiesBlockSyntax(
    SyntaxToken keyword,
    SyntaxToken openBrace,
    ImmutableArray<PropertyDeclarationSyntax> properties,
    ImmutableArray<SkippedTokensSyntax> skippedTokens,
    SyntaxToken closeBrace) : ShaderLabStatementSyntax
{
    /// <summary><c>Properties</c> キーワード。</summary>
    public SyntaxToken Keyword { get; } = keyword;

    /// <summary>開き波括弧。</summary>
    public SyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>宣言されたプロパティ。</summary>
    public ImmutableArray<PropertyDeclarationSyntax> Properties { get; } = properties;

    /// <summary>解釈できなかったトークン。</summary>
    public ImmutableArray<SkippedTokensSyntax> SkippedTokens { get; } = skippedTokens;

    /// <summary>閉じ波括弧。</summary>
    public SyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;
        yield return OpenBrace;

        // プロパティと読み飛ばしトークンが混在しうるため、位置順に並べ直して返す。
        // ラウンドトリップは「元のテキストの順序どおりであること」を要求する。
        List<SyntaxNodeOrToken> children = [];
        children.AddRange(Properties.Select(p => (SyntaxNodeOrToken)p));
        children.AddRange(SkippedTokens.Select(s => (SyntaxNodeOrToken)s));

        foreach (SyntaxNodeOrToken child in children.OrderBy(c => c.FullSpan.Start))
        {
            yield return child;
        }

        yield return CloseBrace;
    }
}

/// <summary>
/// プロパティ 1 件の宣言。
/// </summary>
/// <param name="attributes"><c>[Toggle]</c> のような属性。</param>
/// <param name="nameToken">プロパティ名。</param>
/// <param name="openParen">開き丸括弧。</param>
/// <param name="displayNameToken">インスペクタに表示される名前。</param>
/// <param name="commaToken">表示名と型を区切るカンマ。</param>
/// <param name="type">プロパティの型。</param>
/// <param name="closeParen">閉じ丸括弧。</param>
/// <param name="equalsToken">既定値との間の等号。</param>
/// <param name="defaultValue">既定値。</param>
/// <remarks>
/// <c>[Toggle] _Name ("表示名", Range(0, 1)) = 0.5</c> という形を表す。
/// </remarks>
public sealed class PropertyDeclarationSyntax(
    ImmutableArray<PropertyAttributeSyntax> attributes,
    SyntaxToken nameToken,
    SyntaxToken openParen,
    SyntaxToken displayNameToken,
    SyntaxToken commaToken,
    PropertyTypeSyntax type,
    SyntaxToken closeParen,
    SyntaxToken equalsToken,
    PropertyDefaultValueSyntax? defaultValue) : ShaderLabSyntaxNode
{
    /// <summary>属性。</summary>
    public ImmutableArray<PropertyAttributeSyntax> Attributes { get; } = attributes;

    /// <summary>プロパティ名。</summary>
    public SyntaxToken NameToken { get; } = nameToken;

    /// <summary>プロパティ名の文字列。</summary>
    public string Name => NameToken.Text;

    /// <summary>開き丸括弧。</summary>
    public SyntaxToken OpenParen { get; } = openParen;

    /// <summary>インスペクタに表示される名前。</summary>
    public SyntaxToken DisplayNameToken { get; } = displayNameToken;

    /// <summary>表示名と型を区切るカンマ。</summary>
    public SyntaxToken CommaToken { get; } = commaToken;

    /// <summary>プロパティの型。</summary>
    public PropertyTypeSyntax Type { get; } = type;

    /// <summary>閉じ丸括弧。</summary>
    public SyntaxToken CloseParen { get; } = closeParen;

    /// <summary>既定値との間の等号。</summary>
    public SyntaxToken EqualsToken { get; } = equalsToken;

    /// <summary>既定値。解析できなかった場合は <see langword="null"/>。</summary>
    public PropertyDefaultValueSyntax? DefaultValue { get; } = defaultValue;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        foreach (PropertyAttributeSyntax attribute in Attributes)
        {
            yield return attribute;
        }

        yield return NameToken;
        yield return OpenParen;
        yield return DisplayNameToken;
        yield return CommaToken;
        yield return Type;
        yield return CloseParen;
        yield return EqualsToken;

        if (DefaultValue is not null)
        {
            yield return DefaultValue;
        }
    }
}

/// <summary>
/// プロパティに付く <c>[Toggle(_KEYWORD)]</c> のような属性。
/// </summary>
/// <param name="openBracket">開き角括弧。</param>
/// <param name="nameToken">属性名。</param>
/// <param name="openParen">開き丸括弧。引数が無い場合は <see langword="null"/>。</param>
/// <param name="argumentTokens">引数のトークン。カンマも含む。</param>
/// <param name="closeParen">閉じ丸括弧。引数が無い場合は <see langword="null"/>。</param>
/// <param name="closeBracket">閉じ角括弧。</param>
public sealed class PropertyAttributeSyntax(
    SyntaxToken openBracket,
    SyntaxToken nameToken,
    SyntaxToken? openParen,
    ImmutableArray<SyntaxToken> argumentTokens,
    SyntaxToken? closeParen,
    SyntaxToken closeBracket) : ShaderLabSyntaxNode
{
    /// <summary>開き角括弧。</summary>
    public SyntaxToken OpenBracket { get; } = openBracket;

    /// <summary>属性名。</summary>
    public SyntaxToken NameToken { get; } = nameToken;

    /// <summary>属性名の文字列。</summary>
    public string Name => NameToken.Text;

    /// <summary>開き丸括弧。</summary>
    public SyntaxToken? OpenParen { get; } = openParen;

    /// <summary>引数のトークン。カンマも含む。</summary>
    public ImmutableArray<SyntaxToken> ArgumentTokens { get; } = argumentTokens;

    /// <summary>カンマを除いた引数のトークン。</summary>
    public IEnumerable<SyntaxToken> ValueArguments
        => ArgumentTokens.Where(t => t.Kind != SyntaxKind.CommaToken);

    /// <summary>閉じ丸括弧。</summary>
    public SyntaxToken? CloseParen { get; } = closeParen;

    /// <summary>閉じ角括弧。</summary>
    public SyntaxToken CloseBracket { get; } = closeBracket;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return OpenBracket;
        yield return NameToken;

        if (OpenParen is not null)
        {
            yield return OpenParen;
        }

        foreach (SyntaxToken token in ArgumentTokens)
        {
            yield return token;
        }

        if (CloseParen is not null)
        {
            yield return CloseParen;
        }

        yield return CloseBracket;
    }
}

/// <summary>
/// プロパティの型。<c>Range(0, 1)</c> のように引数を伴う場合がある。
/// </summary>
/// <param name="typeToken">型名。</param>
/// <param name="openParen">開き丸括弧。引数が無い場合は <see langword="null"/>。</param>
/// <param name="argumentTokens">引数のトークン。カンマも含む。</param>
/// <param name="closeParen">閉じ丸括弧。引数が無い場合は <see langword="null"/>。</param>
public sealed class PropertyTypeSyntax(
    SyntaxToken typeToken,
    SyntaxToken? openParen,
    ImmutableArray<SyntaxToken> argumentTokens,
    SyntaxToken? closeParen) : ShaderLabSyntaxNode
{
    /// <summary>型名。</summary>
    public SyntaxToken TypeToken { get; } = typeToken;

    /// <summary>型名の文字列。</summary>
    public string TypeName => TypeToken.Text;

    /// <summary>開き丸括弧。</summary>
    public SyntaxToken? OpenParen { get; } = openParen;

    /// <summary>引数のトークン。カンマも含む。</summary>
    public ImmutableArray<SyntaxToken> ArgumentTokens { get; } = argumentTokens;

    /// <summary>カンマを除いた引数のトークン。</summary>
    public IEnumerable<SyntaxToken> ValueArguments
        => ArgumentTokens.Where(t => t.Kind != SyntaxKind.CommaToken);

    /// <summary>閉じ丸括弧。</summary>
    public SyntaxToken? CloseParen { get; } = closeParen;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return TypeToken;

        if (OpenParen is not null)
        {
            yield return OpenParen;
        }

        foreach (SyntaxToken token in ArgumentTokens)
        {
            yield return token;
        }

        if (CloseParen is not null)
        {
            yield return CloseParen;
        }
    }
}

/// <summary>
/// プロパティの既定値の基底クラス。
/// </summary>
public abstract class PropertyDefaultValueSyntax : ShaderLabSyntaxNode;

/// <summary>
/// 数値の既定値。<c>= 0.5</c> の形。
/// </summary>
/// <param name="valueToken">数値トークン。</param>
public sealed class ScalarDefaultValueSyntax(SyntaxToken valueToken) : PropertyDefaultValueSyntax
{
    /// <summary>数値トークン。</summary>
    public SyntaxToken ValueToken { get; } = valueToken;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return ValueToken;
    }
}

/// <summary>
/// ベクトルまたは色の既定値。<c>= (1, 1, 1, 1)</c> の形。
/// </summary>
/// <param name="openParen">開き丸括弧。</param>
/// <param name="componentTokens">成分のトークン。カンマも含む。</param>
/// <param name="closeParen">閉じ丸括弧。</param>
public sealed class VectorDefaultValueSyntax(
    SyntaxToken openParen,
    ImmutableArray<SyntaxToken> componentTokens,
    SyntaxToken closeParen) : PropertyDefaultValueSyntax
{
    /// <summary>開き丸括弧。</summary>
    public SyntaxToken OpenParen { get; } = openParen;

    /// <summary>成分のトークン。カンマも含む。</summary>
    public ImmutableArray<SyntaxToken> ComponentTokens { get; } = componentTokens;

    /// <summary>カンマを除いた成分のトークン。</summary>
    public IEnumerable<SyntaxToken> Components
        => ComponentTokens.Where(t => t.Kind != SyntaxKind.CommaToken);

    /// <summary>閉じ丸括弧。</summary>
    public SyntaxToken CloseParen { get; } = closeParen;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return OpenParen;

        foreach (SyntaxToken token in ComponentTokens)
        {
            yield return token;
        }

        yield return CloseParen;
    }
}

/// <summary>
/// テクスチャの既定値。<c>= "white" { }</c> の形。
/// </summary>
/// <param name="valueToken">既定テクスチャ名の文字列。</param>
/// <param name="openBrace">開き波括弧。省略されている場合は <see langword="null"/>。</param>
/// <param name="bodyTokens">波括弧内のトークン。</param>
/// <param name="closeBrace">閉じ波括弧。省略されている場合は <see langword="null"/>。</param>
/// <remarks>
/// 波括弧の中には旧来 <c>TexGen</c> などの指定が書けたが、現在は空にするのが通例である。
/// 中身の解釈は行わず、トークン列としてそのまま保持する。
/// </remarks>
public sealed class TextureDefaultValueSyntax(
    SyntaxToken valueToken,
    SyntaxToken? openBrace,
    ImmutableArray<SyntaxToken> bodyTokens,
    SyntaxToken? closeBrace) : PropertyDefaultValueSyntax
{
    /// <summary>既定テクスチャ名の文字列。</summary>
    public SyntaxToken ValueToken { get; } = valueToken;

    /// <summary>既定テクスチャ名。引用符は含まない。</summary>
    public string Value => ValueToken.ValueText;

    /// <summary>開き波括弧。</summary>
    public SyntaxToken? OpenBrace { get; } = openBrace;

    /// <summary>波括弧内のトークン。</summary>
    public ImmutableArray<SyntaxToken> BodyTokens { get; } = bodyTokens;

    /// <summary>閉じ波括弧。</summary>
    public SyntaxToken? CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return ValueToken;

        if (OpenBrace is not null)
        {
            yield return OpenBrace;
        }

        foreach (SyntaxToken token in BodyTokens)
        {
            yield return token;
        }

        if (CloseBrace is not null)
        {
            yield return CloseBrace;
        }
    }
}

/// <summary>
/// <c>SubShader { ... }</c> ブロック。
/// </summary>
/// <param name="keyword"><c>SubShader</c> キーワード。</param>
/// <param name="body">波括弧で囲まれた本体。</param>
public sealed class SubShaderSyntax(SyntaxToken keyword, BlockSyntax body) : ShaderLabStatementSyntax
{
    /// <summary><c>SubShader</c> キーワード。</summary>
    public SyntaxToken Keyword { get; } = keyword;

    /// <summary>波括弧で囲まれた本体。</summary>
    public BlockSyntax Body { get; } = body;

    /// <summary>この SubShader に含まれる Pass。</summary>
    public IEnumerable<PassSyntax> Passes => Body.Statements.OfType<PassSyntax>();

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;
        yield return Body;
    }
}

/// <summary>
/// <c>Pass { ... }</c> ブロック。
/// </summary>
/// <param name="keyword"><c>Pass</c> キーワード。</param>
/// <param name="body">波括弧で囲まれた本体。</param>
public sealed class PassSyntax(SyntaxToken keyword, BlockSyntax body) : ShaderLabStatementSyntax
{
    /// <summary><c>Pass</c> キーワード。</summary>
    public SyntaxToken Keyword { get; } = keyword;

    /// <summary>波括弧で囲まれた本体。</summary>
    public BlockSyntax Body { get; } = body;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;
        yield return Body;
    }
}

/// <summary>
/// <c>Category { ... }</c> ブロック。
/// </summary>
/// <param name="keyword"><c>Category</c> キーワード。</param>
/// <param name="body">波括弧で囲まれた本体。</param>
/// <remarks>
/// 複数の SubShader に共通の状態をまとめるための旧来の構文。
/// 現在は使われないが、既存プロジェクトには残っているため解析できる必要がある。
/// </remarks>
public sealed class CategorySyntax(SyntaxToken keyword, BlockSyntax body) : ShaderLabStatementSyntax
{
    /// <summary><c>Category</c> キーワード。</summary>
    public SyntaxToken Keyword { get; } = keyword;

    /// <summary>波括弧で囲まれた本体。</summary>
    public BlockSyntax Body { get; } = body;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;
        yield return Body;
    }
}

/// <summary>
/// <c>Tags { "Key" = "Value" }</c> ブロック。
/// </summary>
/// <param name="keyword"><c>Tags</c> キーワード。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="tags">タグの並び。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
public sealed class TagsBlockSyntax(
    SyntaxToken keyword,
    SyntaxToken openBrace,
    ImmutableArray<TagSyntax> tags,
    SyntaxToken closeBrace) : ShaderLabStatementSyntax
{
    /// <summary><c>Tags</c> キーワード。</summary>
    public SyntaxToken Keyword { get; } = keyword;

    /// <summary>開き波括弧。</summary>
    public SyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>タグの並び。</summary>
    public ImmutableArray<TagSyntax> Tags { get; } = tags;

    /// <summary>閉じ波括弧。</summary>
    public SyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;
        yield return OpenBrace;

        foreach (TagSyntax tag in Tags)
        {
            yield return tag;
        }

        yield return CloseBrace;
    }
}

/// <summary>
/// タグ 1 件。<c>"Key" = "Value"</c> の形。
/// </summary>
/// <param name="keyToken">タグ名の文字列。</param>
/// <param name="equalsToken">等号。</param>
/// <param name="valueToken">タグ値の文字列。</param>
public sealed class TagSyntax(
    SyntaxToken keyToken,
    SyntaxToken equalsToken,
    SyntaxToken valueToken) : ShaderLabSyntaxNode
{
    /// <summary>タグ名の文字列トークン。</summary>
    public SyntaxToken KeyToken { get; } = keyToken;

    /// <summary>タグ名。引用符は含まない。</summary>
    public string Key => KeyToken.ValueText;

    /// <summary>等号。</summary>
    public SyntaxToken EqualsToken { get; } = equalsToken;

    /// <summary>タグ値の文字列トークン。</summary>
    public SyntaxToken ValueToken { get; } = valueToken;

    /// <summary>タグ値。引用符は含まない。</summary>
    public string Value => ValueToken.ValueText;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return KeyToken;
        yield return EqualsToken;
        yield return ValueToken;
    }
}

/// <summary>
/// <c>CGPROGRAM ... ENDCG</c> のような埋め込みコードブロック。
/// </summary>
/// <param name="token">ブロック全体を表すトークン。</param>
/// <remarks>
/// <para>
/// 中身のコードは ShaderLab の構文木の上ではこの 1 トークンのままである。
/// HLSL としての解析は意味解析層が <see cref="BodySpan"/> を起点に別途行う。
/// </para>
/// <para>
/// ブロックの種別 (開始・終了キーワード、言語、共通コード片かどうか) は
/// <see cref="ProgramBlockDelimiters"/> の表から引く。字句解析ツールと同じ表を使うことで、
/// 「どこからどこまでが中身か」の判定が両者でずれないようにしている。
/// </para>
/// </remarks>
public sealed class ProgramBlockSyntax(SyntaxToken token) : ShaderLabStatementSyntax
{
    private ProgramBlockDelimiter? _delimiter;
    private bool _delimiterComputed;

    /// <summary>ブロック全体を表すトークン。</summary>
    public SyntaxToken Token { get; } = token;

    /// <summary>
    /// このブロックの種別。判別できない場合は <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 字句解析ツールは既知の開始キーワードを見つけた場合にのみこのトークンを作るため、
    /// 通常は必ず値が入る。<see langword="null"/> になるのは、
    /// 構文木を手で組み立てたときのような例外的な場合に限られる。
    /// </remarks>
    public ProgramBlockDelimiter? Delimiter
    {
        get
        {
            if (!_delimiterComputed)
            {
                _delimiter = ProgramBlockDelimiters.TryGetByBlockText(Token.Text, out ProgramBlockDelimiter found)
                    ? found
                    : null;
                _delimiterComputed = true;
            }

            return _delimiter;
        }
    }

    /// <summary>中に書かれている言語。判別できない場合は <see langword="null"/>。</summary>
    public ProgramBlockLanguage? Language => Delimiter?.Language;

    /// <summary>
    /// 各 Pass へ差し込まれる共通コード片 (<c>CGINCLUDE</c> / <c>HLSLINCLUDE</c>) かどうか。
    /// </summary>
    public bool IsIncludeBlock => Delimiter?.IsIncludeBlock ?? false;

    /// <summary>
    /// 終了キーワードで正しく閉じられているかどうか。
    /// </summary>
    /// <remarks>
    /// 閉じられないままファイル末尾に達した場合は <see langword="false"/> になる。
    /// この場合ブロックの中身はファイル末尾までを含む。
    /// </remarks>
    public bool IsTerminated
        => Delimiter is { } delimiter
           && Token.Text.Length > delimiter.StartKeyword.Length + delimiter.EndKeyword.Length
           && Token.Text.EndsWith(delimiter.EndKeyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 開始・終了キーワードを除いた、中身のコードの範囲。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>この範囲はブロックのトークンではなくファイル全体を基準としたオフセットである。</b>
    /// 意味解析層はこの範囲を使って、ファイル上の位置を保ったまま
    /// HLSL としての解析を行う。オフセットを付け替えると診断が別の行を指す。
    /// </para>
    /// <para>
    /// 種別が判別できない場合は、ブロック全体の範囲を返す。
    /// 中身を取りこぼすより、キーワードを含んだまま解析させるほうが害が小さい。
    /// </para>
    /// </remarks>
    public TextSpan BodySpan
    {
        get
        {
            if (Delimiter is not { } delimiter)
            {
                return Token.Span;
            }

            int start = Token.Span.Start + delimiter.StartKeyword.Length;
            int end = IsTerminated ? Token.Span.End - delimiter.EndKeyword.Length : Token.Span.End;

            return TextSpan.FromBounds(start, Math.Max(start, end));
        }
    }

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return Token;
    }
}

/// <summary>
/// レンダーステートやその他の命令。<c>Cull Back</c> や <c>Blend SrcAlpha OneMinusSrcAlpha</c> の形。
/// </summary>
/// <param name="nameToken">命令名。</param>
/// <param name="arguments">引数。</param>
/// <param name="openBrace">波括弧の本体の開き括弧。本体を持たない命令では <see langword="null"/>。</param>
/// <param name="bodyTokens">波括弧の本体に含まれるトークン。</param>
/// <param name="closeBrace">波括弧の本体の閉じ括弧。本体を持たない命令では <see langword="null"/>。</param>
/// <remarks>
/// <para>
/// 命令ごとに専用のノード型を作らず、名前と引数の並びという共通の形で表す。
/// ShaderLab の命令は数十種類あるうえ Unity のバージョンで増減するため、
/// 型を作り分けると新しい命令が出るたびにパーサの変更が必要になる。
/// </para>
/// <para>
/// 命令名や引数の妥当性はルール側で検査する。未知の命令をパーサが構文エラーにしないことで、
/// ツールが知らない新しい命令を使っているだけのシェーダーに対して
/// 誤ったエラーを出さずに済む。
/// </para>
/// </remarks>
public sealed class CommandSyntax(
    SyntaxToken nameToken,
    ImmutableArray<CommandArgumentSyntax> arguments,
    SyntaxToken? openBrace,
    ImmutableArray<SyntaxToken> bodyTokens,
    SyntaxToken? closeBrace) : ShaderLabStatementSyntax
{
    /// <summary>命令名のトークン。</summary>
    public SyntaxToken NameToken { get; } = nameToken;

    /// <summary>命令名の文字列。</summary>
    public string Name => NameToken.Text;

    /// <summary>引数。区切りのカンマも要素として含む。</summary>
    public ImmutableArray<CommandArgumentSyntax> Arguments { get; } = arguments;

    /// <summary>
    /// 波括弧の本体の開き括弧。本体を持たない命令の場合は <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <c>Fog { Mode Off }</c> や <c>SetTexture [_MainTex] { combine primary }</c> のように、
    /// 波括弧の本体を取る命令がある。いずれも固定機能パイプライン時代の記法だが、
    /// Unity 同梱のシェーダーに現在も残っており、扱えないと構文エラーになる。
    /// </remarks>
    public SyntaxToken? OpenBrace { get; } = openBrace;

    /// <summary>
    /// 波括弧の本体に含まれるトークン。
    /// </summary>
    /// <remarks>
    /// 中身は解釈しない。固定機能パイプラインの記法であり、
    /// 現時点でこれを対象とするルールが存在しないためである。
    /// </remarks>
    public ImmutableArray<SyntaxToken> BodyTokens { get; } = bodyTokens;

    /// <summary>波括弧の本体の閉じ括弧。</summary>
    public SyntaxToken? CloseBrace { get; } = closeBrace;

    /// <summary>波括弧の本体を持つかどうか。</summary>
    public bool HasBody => OpenBrace is not null;

    /// <summary>区切りのカンマを除いた引数。</summary>
    public IEnumerable<CommandArgumentSyntax> ValueArguments
        => Arguments.Where(a => a is not ArgumentSeparatorSyntax);

    /// <summary>
    /// 命令名が指定した文字列と一致するかを大文字小文字を無視して判定する。
    /// </summary>
    /// <param name="name">比較する命令名。</param>
    /// <returns>一致する場合は <see langword="true"/>。</returns>
    /// <remarks>ShaderLab の命令名は大文字小文字を区別しない。</remarks>
    public bool NameIs(string name) => NameToken.TextIs(name);

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return NameToken;

        foreach (CommandArgumentSyntax argument in Arguments)
        {
            yield return argument;
        }

        if (OpenBrace is not null)
        {
            yield return OpenBrace;
        }

        foreach (SyntaxToken token in BodyTokens)
        {
            yield return token;
        }

        if (CloseBrace is not null)
        {
            yield return CloseBrace;
        }
    }
}

/// <summary>
/// 命令の引数の基底クラス。
/// </summary>
public abstract class CommandArgumentSyntax : ShaderLabSyntaxNode;

/// <summary>
/// 識別子や数値そのままの引数。
/// </summary>
/// <param name="token">引数のトークン。</param>
public sealed class LiteralArgumentSyntax(SyntaxToken token) : CommandArgumentSyntax
{
    /// <summary>引数のトークン。</summary>
    public SyntaxToken Token { get; } = token;

    /// <summary>引数の文字列。</summary>
    public string Text => Token.Text;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return Token;
    }
}

/// <summary>
/// プロパティを参照する引数。<c>Cull [_Cull]</c> の <c>[_Cull]</c> の部分。
/// </summary>
/// <param name="openBracket">開き角括弧。</param>
/// <param name="nameToken">参照するプロパティ名。</param>
/// <param name="closeBracket">閉じ角括弧。</param>
/// <remarks>
/// レンダーステートの値をマテリアルのプロパティから動的に決める記法である。
/// 値の妥当性検査は、参照先のプロパティが何を取りうるか分からない以上その場ではできないため、
/// この形の引数はレンダーステートの値検査 (SL1021) の対象から外す。
/// </remarks>
public sealed class PropertyReferenceArgumentSyntax(
    SyntaxToken openBracket,
    SyntaxToken nameToken,
    SyntaxToken closeBracket) : CommandArgumentSyntax
{
    /// <summary>開き角括弧。</summary>
    public SyntaxToken OpenBracket { get; } = openBracket;

    /// <summary>参照するプロパティ名のトークン。</summary>
    public SyntaxToken NameToken { get; } = nameToken;

    /// <summary>参照するプロパティ名。</summary>
    public string Name => NameToken.Text;

    /// <summary>閉じ角括弧。</summary>
    public SyntaxToken CloseBracket { get; } = closeBracket;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return OpenBracket;
        yield return NameToken;
        yield return CloseBracket;
    }
}

/// <summary>
/// 引数の区切りのカンマ。<c>Blend One Zero, One One</c> の形で現れる。
/// </summary>
/// <param name="token">カンマのトークン。</param>
public sealed class ArgumentSeparatorSyntax(SyntaxToken token) : CommandArgumentSyntax
{
    /// <summary>カンマのトークン。</summary>
    public SyntaxToken Token { get; } = token;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return Token;
    }
}

/// <summary>
/// <c>Stencil { ... }</c> ブロック。
/// </summary>
/// <param name="keyword"><c>Stencil</c> キーワード。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="commands">ステンシルの設定命令。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
public sealed class StencilBlockSyntax(
    SyntaxToken keyword,
    SyntaxToken openBrace,
    ImmutableArray<CommandSyntax> commands,
    SyntaxToken closeBrace) : ShaderLabStatementSyntax
{
    /// <summary><c>Stencil</c> キーワード。</summary>
    public SyntaxToken Keyword { get; } = keyword;

    /// <summary>開き波括弧。</summary>
    public SyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>ステンシルの設定命令。</summary>
    public ImmutableArray<CommandSyntax> Commands { get; } = commands;

    /// <summary>閉じ波括弧。</summary>
    public SyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;
        yield return OpenBrace;

        foreach (CommandSyntax command in Commands)
        {
            yield return command;
        }

        yield return CloseBrace;
    }
}

/// <summary>
/// <c>GrabPass { }</c> ブロック。
/// </summary>
/// <param name="keyword"><c>GrabPass</c> キーワード。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="bodyTokens">波括弧内のトークン。テクスチャ名の文字列が入ることがある。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
public sealed class GrabPassSyntax(
    SyntaxToken keyword,
    SyntaxToken openBrace,
    ImmutableArray<SyntaxToken> bodyTokens,
    SyntaxToken closeBrace) : ShaderLabStatementSyntax
{
    /// <summary><c>GrabPass</c> キーワード。</summary>
    public SyntaxToken Keyword { get; } = keyword;

    /// <summary>開き波括弧。</summary>
    public SyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>波括弧内のトークン。</summary>
    public ImmutableArray<SyntaxToken> BodyTokens { get; } = bodyTokens;

    /// <summary>閉じ波括弧。</summary>
    public SyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;
        yield return OpenBrace;

        foreach (SyntaxToken token in BodyTokens)
        {
            yield return token;
        }

        yield return CloseBrace;
    }
}
