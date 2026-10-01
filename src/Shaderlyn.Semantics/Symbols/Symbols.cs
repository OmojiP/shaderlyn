using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Semantics.Symbols;

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

/// <summary>
/// HLSL 側で宣言された uniform 1 件。
/// </summary>
/// <remarks>
/// ここでいう uniform とは「関数の外で宣言された、<c>static</c> でない変数」である。
/// マテリアルやエンジンから値が与えられる対象であり、
/// <c>static const</c> な定数やローカル変数は含まない。
/// </remarks>
public sealed class UniformSymbol
{
    /// <summary>
    /// uniform を表すシンボルを生成する。
    /// </summary>
    /// <param name="name">変数名。</param>
    /// <param name="declaration">対応する宣言。</param>
    /// <param name="declarator">対応する宣言子。</param>
    /// <param name="containingBufferName">
    /// 含まれている定数バッファの名前。バッファの外で宣言されている場合は <see langword="null"/>。
    /// </param>
    public UniformSymbol(
        string name,
        VariableDeclarationSyntax declaration,
        VariableDeclaratorSyntax declarator,
        string? containingBufferName)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(declarator);

        Name = name;
        Declaration = declaration;
        Declarator = declarator;
        ContainingBufferName = containingBufferName;
        TypeName = declaration.Type.Name;
        TypeClass = HlslTypeClassifier.Classify(TypeName);
    }

    /// <summary>変数名。</summary>
    public string Name { get; }

    /// <summary>型名。</summary>
    public string TypeName { get; }

    /// <summary>型の分類。</summary>
    public HlslTypeClass TypeClass { get; }

    /// <summary>対応する宣言。</summary>
    public VariableDeclarationSyntax Declaration { get; }

    /// <summary>対応する宣言子。</summary>
    public VariableDeclaratorSyntax Declarator { get; }

    /// <summary>含まれている定数バッファの名前。バッファの外なら <see langword="null"/>。</summary>
    public string? ContainingBufferName { get; }

    /// <summary>配列として宣言されているかどうか。</summary>
    public bool IsArray => Declarator.IsArray;

    /// <summary>
    /// この宣言の位置。
    /// </summary>
    /// <remarks>
    /// <b><see cref="HlslSyntaxNode.GetLocation"/> を使うこと。</b>
    /// uniform は include されたファイルの中で宣言されていることが多く、
    /// 範囲だけではどのファイルの位置なのかが決まらない。
    /// </remarks>
    public Location? GetLocation() => Declarator.GetLocation() ?? Declaration.GetLocation();

    /// <summary>この uniform が指定した名前の定数バッファに含まれるかを判定する。</summary>
    /// <param name="bufferName">定数バッファの名前。</param>
    /// <returns>含まれる場合は <see langword="true"/>。</returns>
    public bool IsInBuffer(string bufferName)
        => ContainingBufferName is not null
           && string.Equals(ContainingBufferName, bufferName, StringComparison.Ordinal);
}

/// <summary>
/// <c>cbuffer</c> 1 つ分。
/// </summary>
/// <param name="Name">バッファ名。</param>
/// <param name="Declaration">対応する構文ノード。</param>
/// <param name="Members">含まれる uniform。</param>
public readonly record struct ConstantBufferSymbol(
    string Name,
    ConstantBufferDeclarationSyntax Declaration,
    ImmutableArray<UniformSymbol> Members);
