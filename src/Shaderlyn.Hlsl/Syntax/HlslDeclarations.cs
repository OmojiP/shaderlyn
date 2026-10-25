using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Hlsl.Syntax;

/// <summary>
/// <c>cbuffer</c> / <c>tbuffer</c> の宣言。
/// </summary>
/// <param name="keyword">キーワード。</param>
/// <param name="nameToken">バッファ名。</param>
/// <param name="semantics">レジスタ指定。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="members">メンバー。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
/// <param name="semicolonToken">終端のセミコロン。省略可能。</param>
/// <remarks>
/// URP における <c>CBUFFER_START(UnityPerMaterial)</c> はマクロであり、
/// 展開されると <c>cbuffer UnityPerMaterial {</c> になる。
/// SRP Batcher の互換性検査 (URP0001) はこのノードを対象に行う。
/// </remarks>
public sealed class ConstantBufferDeclarationSyntax(
    ShaderSyntaxToken keyword,
    ShaderSyntaxToken? nameToken,
    ImmutableArray<SemanticSyntax> semantics,
    ShaderSyntaxToken openBrace,
    ImmutableArray<DeclarationSyntax> members,
    ShaderSyntaxToken closeBrace,
    ShaderSyntaxToken? semicolonToken) : DeclarationSyntax
{
    /// <summary>キーワードのトークン。</summary>
    public ShaderSyntaxToken Keyword { get; } = keyword;

    /// <summary>バッファ名のトークン。</summary>
    public ShaderSyntaxToken? NameToken { get; } = nameToken;

    /// <summary>バッファ名。無名の場合は空文字列。</summary>
    public string Name => NameToken?.Text ?? string.Empty;

    /// <summary>レジスタ指定。</summary>
    public ImmutableArray<SemanticSyntax> Semantics { get; } = semantics;

    /// <summary>開き波括弧。</summary>
    public ShaderSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>メンバー。</summary>
    public ImmutableArray<DeclarationSyntax> Members { get; } = members;

    /// <summary>閉じ波括弧。</summary>
    public ShaderSyntaxToken CloseBrace { get; } = closeBrace;

    /// <summary>終端のセミコロン。</summary>
    public ShaderSyntaxToken? SemicolonToken { get; } = semicolonToken;

    /// <summary>メンバーのうち変数宣言であるもの。</summary>
    public IEnumerable<VariableDeclarationSyntax> Fields => Members.OfType<VariableDeclarationSyntax>();

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;

        if (NameToken is not null)
        {
            yield return NameToken;
        }

        foreach (SemanticSyntax semantic in Semantics)
        {
            yield return semantic;
        }

        yield return OpenBrace;

        foreach (DeclarationSyntax member in Members)
        {
            yield return member;
        }

        yield return CloseBrace;

        if (SemicolonToken is not null)
        {
            yield return SemicolonToken;
        }
    }
}

/// <summary>
/// <c>namespace</c> の宣言。
/// </summary>
/// <param name="keyword"><c>namespace</c> キーワード。</param>
/// <param name="nameToken">名前空間の名前。無名の場合は <see langword="null"/>。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="members">中の宣言。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
/// <remarks>
/// HLSL 2021 で追加された機能で、Unity のレイトレーシング関連のコードが使っている。
/// 対応していないと、名前空間を含むファイルとそれを include するファイルが
/// まとめて解析できなくなる。
/// </remarks>
public sealed class NamespaceDeclarationSyntax(
    ShaderSyntaxToken keyword,
    ShaderSyntaxToken? nameToken,
    ShaderSyntaxToken openBrace,
    ImmutableArray<DeclarationSyntax> members,
    ShaderSyntaxToken closeBrace) : DeclarationSyntax
{
    /// <summary><c>namespace</c> キーワード。</summary>
    public ShaderSyntaxToken Keyword { get; } = keyword;

    /// <summary>名前空間の名前のトークン。</summary>
    public ShaderSyntaxToken? NameToken { get; } = nameToken;

    /// <summary>名前空間の名前。無名の場合は空文字列。</summary>
    public string Name => NameToken?.Text ?? string.Empty;

    /// <summary>開き波括弧。</summary>
    public ShaderSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>中の宣言。</summary>
    public ImmutableArray<DeclarationSyntax> Members { get; } = members;

    /// <summary>閉じ波括弧。</summary>
    public ShaderSyntaxToken CloseBrace { get; } = closeBrace;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        yield return Keyword;

        if (NameToken is not null)
        {
            yield return NameToken;
        }

        yield return OpenBrace;

        foreach (DeclarationSyntax member in Members)
        {
            yield return member;
        }

        yield return CloseBrace;
    }
}
