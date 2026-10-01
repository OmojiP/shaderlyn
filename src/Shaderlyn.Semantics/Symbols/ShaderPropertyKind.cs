namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// ShaderLab の <c>Properties</c> に書けるプロパティの型。
/// </summary>
/// <remarks>
/// ShaderLab の型名をそのまま列挙にしている。
/// HLSL 側の型との対応は <see cref="PropertyTypeCompatibility"/> が持つ。
/// </remarks>
public enum ShaderPropertyKind
{
    /// <summary>このツールが知らない型名。検査の対象外とする。</summary>
    Unknown,

    /// <summary><c>Color</c>。カラーピッカーで編集する 4 成分の値。</summary>
    Color,

    /// <summary><c>Vector</c>。4 成分の値。</summary>
    Vector,

    /// <summary><c>Float</c>。</summary>
    Float,

    /// <summary><c>Range(min, max)</c>。スライダーで編集する実数。</summary>
    Range,

    /// <summary><c>Int</c>。</summary>
    Int,

    /// <summary>
    /// <c>Integer</c>。
    /// </summary>
    /// <remarks>
    /// <c>Int</c> とは別物である。<c>Int</c> は内部的に浮動小数点として保持されるのに対し、
    /// <c>Integer</c> は Unity 2021.1 で追加された真の整数プロパティである。
    /// HLSL 側の型に対する要求は同じなので、このツールでは同じ扱いをする。
    /// </remarks>
    Integer,

    /// <summary><c>2D</c>。2 次元テクスチャ。</summary>
    Texture2D,

    /// <summary><c>3D</c>。3 次元テクスチャ。</summary>
    Texture3D,

    /// <summary><c>Cube</c>。キューブマップ。</summary>
    TextureCube,

    /// <summary><c>2DArray</c>。2 次元テクスチャ配列。</summary>
    Texture2DArray,

    /// <summary><c>CubeArray</c>。キューブマップ配列。</summary>
    TextureCubeArray,

    /// <summary>
    /// <c>Any</c>。任意の型。
    /// </summary>
    /// <remarks>
    /// どの型としても解釈されうるため、HLSL 側の型との整合性は検査しない。
    /// </remarks>
    Any,
}

/// <summary>
/// ShaderLab のプロパティ型名を <see cref="ShaderPropertyKind"/> へ変換する。
/// </summary>
internal static class ShaderPropertyKinds
{
    /// <summary>
    /// 型名から種別を求める。
    /// </summary>
    /// <param name="typeName">ShaderLab に書かれた型名。</param>
    /// <returns>対応する種別。知らない型名の場合は <see cref="ShaderPropertyKind.Unknown"/>。</returns>
    /// <remarks>
    /// ShaderLab は大文字小文字を区別しないため、比較も区別せずに行う。
    /// </remarks>
    public static ShaderPropertyKind FromTypeName(string? typeName) => typeName switch
    {
        null => ShaderPropertyKind.Unknown,
        _ when Is(typeName, "Color") => ShaderPropertyKind.Color,
        _ when Is(typeName, "Vector") => ShaderPropertyKind.Vector,
        _ when Is(typeName, "Float") => ShaderPropertyKind.Float,
        _ when Is(typeName, "Range") => ShaderPropertyKind.Range,
        _ when Is(typeName, "Int") => ShaderPropertyKind.Int,
        _ when Is(typeName, "Integer") => ShaderPropertyKind.Integer,
        _ when Is(typeName, "2D") => ShaderPropertyKind.Texture2D,
        _ when Is(typeName, "Rect") => ShaderPropertyKind.Texture2D,
        _ when Is(typeName, "3D") => ShaderPropertyKind.Texture3D,
        _ when Is(typeName, "Cube") => ShaderPropertyKind.TextureCube,
        _ when Is(typeName, "2DArray") => ShaderPropertyKind.Texture2DArray,
        _ when Is(typeName, "CubeArray") => ShaderPropertyKind.TextureCubeArray,
        _ when Is(typeName, "Any") => ShaderPropertyKind.Any,
        _ => ShaderPropertyKind.Unknown,
    };

    /// <summary>種別がテクスチャを表すかどうかを判定する。</summary>
    /// <param name="kind">判定する種別。</param>
    /// <returns>テクスチャの場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// テクスチャは定数バッファに入れられないため、SRP Batcher の検査で区別が必要になる。
    /// </remarks>
    public static bool IsTexture(this ShaderPropertyKind kind) => kind is
        ShaderPropertyKind.Texture2D
        or ShaderPropertyKind.Texture3D
        or ShaderPropertyKind.TextureCube
        or ShaderPropertyKind.Texture2DArray
        or ShaderPropertyKind.TextureCubeArray;

    /// <summary>種別を ShaderLab の表記で表す。</summary>
    /// <param name="kind">対象の種別。</param>
    /// <returns>診断メッセージに使う表記。</returns>
    public static string ToDisplayName(this ShaderPropertyKind kind) => kind switch
    {
        ShaderPropertyKind.Color => "Color",
        ShaderPropertyKind.Vector => "Vector",
        ShaderPropertyKind.Float => "Float",
        ShaderPropertyKind.Range => "Range",
        ShaderPropertyKind.Int => "Int",
        ShaderPropertyKind.Integer => "Integer",
        ShaderPropertyKind.Texture2D => "2D",
        ShaderPropertyKind.Texture3D => "3D",
        ShaderPropertyKind.TextureCube => "Cube",
        ShaderPropertyKind.Texture2DArray => "2DArray",
        ShaderPropertyKind.TextureCubeArray => "CubeArray",
        ShaderPropertyKind.Any => "Any",
        _ => "不明",
    };

    private static bool Is(string value, string name) => string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
}
