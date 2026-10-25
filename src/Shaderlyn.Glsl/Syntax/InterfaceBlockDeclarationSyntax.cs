using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Glsl.Syntax;

/// <summary>
/// GLSL のインスタンスの名前を持たないインターフェースブロック (<c>layout(std140) uniform UBO { mat4 m; };</c>)。
/// </summary>
/// <param name="modifiers">修飾子 (<c>layout(...)</c> と記憶域の修飾子)。</param>
/// <param name="nameToken">ブロックの名前。</param>
/// <param name="openBrace">開き波括弧。</param>
/// <param name="members">メンバーの宣言。</param>
/// <param name="closeBrace">閉じ波括弧。</param>
/// <param name="semicolonToken">終端のセミコロン。</param>
/// <remarks>
/// <para>
/// メンバーは、そのまま外側の名前になる (<c>m</c> と書いて使う)。HLSL の <c>cbuffer</c> と同じである。
/// </para>
/// <para>
/// インスタンスの名前を持つブロック (<c>uniform UBO { mat4 m; } ubo;</c>) は、構造体の宣言 (<see cref="StructDeclarationSyntax"/>) として読む。
/// </para>
/// </remarks>
internal sealed class InterfaceBlockDeclarationSyntax(
    ImmutableArray<ShaderSyntaxToken> modifiers,
    ShaderSyntaxToken nameToken,
    ShaderSyntaxToken openBrace,
    ImmutableArray<DeclarationSyntax> members,
    ShaderSyntaxToken closeBrace,
    ShaderSyntaxToken semicolonToken) : DeclarationSyntax
{
    /// <summary>修飾子 (<c>layout(...)</c> と記憶域の修飾子)。</summary>
    public ImmutableArray<ShaderSyntaxToken> Modifiers { get; } = modifiers;

    /// <summary>ブロックの名前のトークン。</summary>
    public ShaderSyntaxToken NameToken { get; } = nameToken;

    /// <summary>ブロックの名前。</summary>
    public string Name => NameToken.Text;

    /// <summary>開き波括弧。</summary>
    public ShaderSyntaxToken OpenBrace { get; } = openBrace;

    /// <summary>メンバーの宣言。</summary>
    public ImmutableArray<DeclarationSyntax> Members { get; } = members;

    /// <summary>閉じ波括弧。</summary>
    public ShaderSyntaxToken CloseBrace { get; } = closeBrace;

    /// <summary>終端のセミコロン。</summary>
    public ShaderSyntaxToken SemicolonToken { get; } = semicolonToken;

    /// <inheritdoc/>
    public override IEnumerable<ShaderNodeOrToken> ChildNodesAndTokens()
    {
        foreach (ShaderSyntaxToken modifier in Modifiers)
        {
            yield return modifier;
        }

        yield return NameToken;
        yield return OpenBrace;

        foreach (DeclarationSyntax member in Members)
        {
            yield return member;
        }

        yield return CloseBrace;
        yield return SemicolonToken;
    }
}
