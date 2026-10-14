using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Unity;

/// <summary>
/// <c>Properties</c> に宣言されたプロパティ 1 件。
/// </summary>
/// <remarks>
/// 構文ノードをそのまま持たせているのは、診断の位置を後から自由に選べるようにするためである。
/// 「プロパティ名だけを指す」「型の部分だけを指す」といった指定はルールごとに異なる。
/// </remarks>
public sealed class PropertySymbol
{
    /// <summary>
    /// プロパティを表すシンボルを生成する。
    /// </summary>
    /// <param name="declaration">対応する構文ノード。</param>
    /// <param name="text">プロパティが書かれているソーステキスト。</param>
    public PropertySymbol(PropertyDeclarationSyntax declaration, SourceText text)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(text);

        Declaration = declaration;
        Text = text;
        Name = declaration.NameToken.Text;
        Kind = ShaderPropertyKinds.FromTypeName(declaration.Type.TypeToken.Text);
    }

    /// <summary>プロパティ名。</summary>
    public string Name { get; }

    /// <summary>プロパティの型。</summary>
    public ShaderPropertyKind Kind { get; }

    /// <summary>対応する構文ノード。</summary>
    public PropertyDeclarationSyntax Declaration { get; }

    /// <summary>プロパティが書かれているソーステキスト。</summary>
    public SourceText Text { get; }

    /// <summary>プロパティ名の位置。</summary>
    public Location NameLocation => Location.Create(Text, Declaration.NameToken.Span);

    /// <summary>
    /// 指定した名前の属性が付いているかどうかを判定する。
    /// </summary>
    /// <param name="attributeName">属性名。</param>
    /// <returns>付いている場合は <see langword="true"/>。</returns>
    /// <remarks>ShaderLab の属性名は大文字小文字を区別しない。</remarks>
    public bool HasAttribute(string attributeName)
        => Declaration.Attributes.Any(a => a.NameToken.TextIs(attributeName));

    /// <summary>
    /// マテリアルではなく描画側から値が与えられるプロパティかどうかを判定する。
    /// </summary>
    /// <returns>描画側から与えられる場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <c>[PerRendererData]</c> が付いたプロパティは <c>MaterialPropertyBlock</c> 経由で
    /// 描画のたびに差し替えられる。マテリアルの定数バッファに入れてはならず、
    /// SRP Batcher の検査から外す必要がある。
    /// </remarks>
    public bool IsPerRendererData() => HasAttribute("PerRendererData");
}
