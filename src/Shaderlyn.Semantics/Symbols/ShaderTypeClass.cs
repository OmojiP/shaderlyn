namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// 型を、プロパティとの対応を判定できる粒度まで丸めた分類。言語に依らない (GLSL の <c>vec3</c> も HLSL の <c>float3</c> も <see cref="Vector"/>)。
/// </summary>
/// <remarks>
/// <para>
/// <b>意図的に粗い。</b>
/// <c>float4</c> と <c>half3</c> を区別しても、プロパティとの対応の観点では意味が無い。
/// <c>Color</c> のプロパティを <c>half3</c> で受けるのは Unity のシェーダーでも普通に行われており、
/// 成分数の違いまで報告すると誤検出になる。
/// </para>
/// <para>
/// 逆にスカラーとベクトル、ベクトルとテクスチャの取り違えは、
/// 「マテリアルで設定した値が届かない」という形で必ず不具合になる。
/// 報告する価値があるのはこの粒度の食い違いだけである。
/// </para>
/// </remarks>
public enum ShaderTypeClass
{
    /// <summary>
    /// このツールが判別できない型。ユーザー定義の構造体や、未解決のマクロなど。
    /// </summary>
    /// <remarks>
    /// <b>この分類に対して型の不一致を報告してはならない。</b>
    /// 判別できないことは「合っていない」ことの証拠にならない。
    /// </remarks>
    Unknown,

    /// <summary><c>float</c> / <c>half</c> / <c>int</c> など 1 成分の数値型。</summary>
    Scalar,

    /// <summary><c>float4</c> / <c>half3</c> など 2〜4 成分のベクトル型。</summary>
    Vector,

    /// <summary><c>float4x4</c> などの行列型。</summary>
    Matrix,

    /// <summary>2 次元テクスチャ。<c>Texture2D</c> と Cg の <c>sampler2D</c> の両方を含む。</summary>
    Texture2D,

    /// <summary>3 次元テクスチャ。</summary>
    Texture3D,

    /// <summary>キューブマップ。</summary>
    TextureCube,

    /// <summary>2 次元テクスチャ配列。</summary>
    Texture2DArray,

    /// <summary>キューブマップ配列。</summary>
    TextureCubeArray,

    /// <summary><c>SamplerState</c> などのサンプラ。</summary>
    Sampler,

    /// <summary><c>StructuredBuffer</c> などのバッファ。</summary>
    Buffer,
}

/// <summary>
/// 数値型の形。
/// </summary>
public enum ShaderNumericKind
{
    /// <summary>数値型ではない。</summary>
    None,

    /// <summary>1 成分。</summary>
    Scalar,

    /// <summary>2 から 4 成分のベクトル。</summary>
    Vector,

    /// <summary>行列。</summary>
    Matrix,
}

/// <summary>
/// 数値型を、基底名と行数・列数に分けて表したもの。
/// </summary>
/// <param name="Kind">形。</param>
/// <param name="BaseName">基底名 (<c>float4x4</c> なら <c>float</c>)。</param>
/// <param name="Rows">行数。スカラーとベクトルは 1。</param>
/// <param name="Columns">列数。スカラーは 1、ベクトルは成分数。</param>
/// <remarks>
/// ベクトルを 1 行として表すことで、<c>mul</c> の規則を
/// スカラー・ベクトル・行列で場合分けせずに書ける。
/// </remarks>
public readonly record struct ShaderNumericShape(ShaderNumericKind Kind, string BaseName, int Rows, int Columns);

/// <summary>型の分類 (<see cref="ShaderTypeClass"/>) についての判定。</summary>
public static class ShaderTypeClassExtensions
{
    /// <summary>
    /// 分類が値型 (テクスチャやサンプラではない) かどうかを判定する。
    /// </summary>
    /// <param name="typeClass">判定する分類。</param>
    /// <returns>スカラー・ベクトル・行列のいずれかであれば <see langword="true"/>。</returns>
    /// <remarks>定数バッファへ入れられるかどうかの判定に使う。</remarks>
    public static bool IsNumeric(this ShaderTypeClass typeClass) => typeClass is
        ShaderTypeClass.Scalar or ShaderTypeClass.Vector or ShaderTypeClass.Matrix;

    /// <summary>
    /// 分類がテクスチャまたはサンプラかどうかを判定する。
    /// </summary>
    /// <param name="typeClass">判定する分類。</param>
    /// <returns>テクスチャかサンプラであれば <see langword="true"/>。</returns>
    public static bool IsTextureOrSampler(this ShaderTypeClass typeClass)
        => typeClass.IsTexture() || typeClass == ShaderTypeClass.Sampler;

    /// <summary>
    /// 分類がテクスチャかどうかを判定する。
    /// </summary>
    /// <param name="typeClass">判定する分類。</param>
    /// <returns>テクスチャであれば <see langword="true"/>。</returns>
    /// <remarks>サンプラは含まない。サンプリングのメソッドを持つのはテクスチャの側である。</remarks>
    public static bool IsTexture(this ShaderTypeClass typeClass) => typeClass is
        ShaderTypeClass.Texture2D
        or ShaderTypeClass.Texture3D
        or ShaderTypeClass.TextureCube
        or ShaderTypeClass.Texture2DArray
        or ShaderTypeClass.TextureCubeArray;
}
