using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// 言語の型システム (型と組み込み関数。計画の D3)。意味解析・ルール・言語サーバーは、型の名前や組み込み関数をここから尋ねる。
/// </summary>
/// <remarks>
/// <para>
/// <b>言語に依らないのは、型の分類 (<see cref="ShaderTypeClass"/>) と数値の形 (<see cref="ShaderNumericShape"/>) である。</b>
/// HLSL の <c>float3</c> も GLSL の <c>vec3</c> も、要素が <c>float</c> で 3 成分のベクトルである。
/// 言語が答えるのは、名前と形の対応、暗黙の変換、組み込み関数と定数、成分の名前である。
/// </para>
/// <para>
/// 解析の対象の言語の型システムは、環境 (<c>IShaderEnvironment.TypeSystem</c>) が与え、<c>compilation.TypeSystem</c> で引ける。
/// </para>
/// </remarks>
public abstract class ShaderTypeSystem
{
    // --------------------------------------------------------------------
    // 型の名前と形
    // --------------------------------------------------------------------

    /// <summary>型の名前を分類する。</summary>
    /// <param name="typeName">型の名前。</param>
    /// <returns>分類。知らない名前は <see cref="ShaderTypeClass.Unknown"/>。</returns>
    public abstract ShaderTypeClass Classify(string? typeName);

    /// <summary>数値の型の名前を、形 (要素の型・行・列) に分ける。</summary>
    /// <param name="typeName">型の名前。</param>
    /// <param name="shape">形。</param>
    /// <returns>数値の型なら <see langword="true"/>。</returns>
    public abstract bool TryDescribeNumeric(string? typeName, out ShaderNumericShape shape);

    /// <summary>形から、その言語での型の名前を作る。</summary>
    /// <param name="shape">形。</param>
    /// <returns>型の名前。その言語に無い形なら <see langword="null"/>。</returns>
    public abstract string? Compose(ShaderNumericShape shape);

    /// <summary>要素の型と成分の数から、スカラーかベクトルの型の名前を作る。</summary>
    /// <param name="baseName">要素の型 (<c>float</c> など)。</param>
    /// <param name="components">成分の数。1 ならスカラー。</param>
    /// <returns>型の名前。その言語に無い組み合わせなら <see langword="null"/>。</returns>
    public abstract string? ComposeNumeric(string baseName, int components);

    /// <summary>スカラーかベクトルの型の名前を、要素の型と成分の数に分ける。</summary>
    /// <param name="typeName">型の名前。</param>
    /// <param name="baseName">要素の型。</param>
    /// <param name="components">成分の数。</param>
    /// <returns>スカラーかベクトルなら <see langword="true"/>。</returns>
    public abstract bool TryDecomposeNumeric(string? typeName, out string baseName, out int components);

    /// <summary>2 つの要素の型を混ぜた演算で、結果になる要素の型を返す。</summary>
    /// <param name="left">左の要素の型。</param>
    /// <param name="right">右の要素の型。</param>
    /// <returns>結果の要素の型。決められなければ <see langword="null"/>。</returns>
    public abstract string? GetWiderBaseName(string left, string right);

    /// <summary>ベクトルの成分の名前の組 (HLSL なら <c>xyzw</c> と <c>rgba</c>)。1 つの参照では 1 つの組の文字だけを使える。</summary>
    public abstract ImmutableArray<string> ComponentNameSets { get; }

    /// <summary>
    /// その木の言語のバージョンで使う型システムを返す。既定は自分自身。
    /// </summary>
    /// <param name="tree">木。</param>
    /// <returns>型システム。</returns>
    /// <remarks>
    /// GLSL は暗黙の変換が <c>#version</c> とプロファイルで変わる (ES には無い)。
    /// 式の型を求める束縛器は、木ごとにこれで引き直す。
    /// </remarks>
    internal virtual ShaderTypeSystem ForTree(ShaderSyntaxTree tree) => this;

    /// <summary>数値のリテラルの型の名前を求める。</summary>
    /// <param name="token">リテラルのトークン。</param>
    /// <returns>型の名前。分からなければ <see langword="null"/>。</returns>
    /// <remarks>接尾辞の意味が言語で違う (GLSL の <c>lf</c> は double、HLSL の <c>h</c> は half)。</remarks>
    internal virtual string? GetLiteralTypeName(ShaderSyntaxToken token) => ShaderLiteral.GetTypeName(token);

    /// <summary>
    /// 行列を含む <c>*</c> が行列の積か。<see langword="false"/> なら成分ごとの積 (HLSL。行列の積は <c>mul</c>)。
    /// </summary>
    /// <remarks>
    /// GLSL は行列の積である。行列の形 (<see cref="ShaderNumericShape"/>) は、GLSL の <c>matCxR</c> (C 列 R 行) を
    /// <c>Rows = C</c>、<c>Columns = R</c> として持つ (HLSL の <c>floatCxR</c> と同じ並び)。
    /// この持ち方では、GLSL の <c>a * b</c> は HLSL の <c>mul(b, a)</c> と同じ形の結果になり、<c>m[i]</c> (列) は <c>Columns</c> 成分のベクトルになる。
    /// </remarks>
    internal virtual bool MultipliesMatricesAlgebraically => false;

    /// <summary>
    /// 比較と論理の演算の結果が、いつも 1 つの <c>bool</c> か (GLSL)。<see langword="false"/> なら成分ごとの <c>bool</c> (HLSL)。
    /// </summary>
    /// <remarks>GLSL の <c>==</c> はベクトルどうしでも <c>bool</c> を返す。成分ごとの比較は <c>equal</c> などの組み込み関数で書く。</remarks>
    internal virtual bool ComparesWholeValues => false;

    /// <summary>配列とベクトルが <c>.length()</c> (結果は <c>int</c>) を持つか (GLSL)。</summary>
    internal virtual bool HasLengthMethod => false;

    // --------------------------------------------------------------------
    // 変換
    // --------------------------------------------------------------------

    /// <summary>値の型を、別の型へ暗黙に変換できるかを判定する。</summary>
    /// <param name="valueType">値の型。</param>
    /// <param name="targetType">変換先の型。</param>
    /// <param name="exact">型が同じであることを求めるなら <see langword="true"/>。</param>
    /// <returns>変換できる、またはどちらかの型が分からなければ <see langword="true"/>。</returns>
    public abstract bool IsConvertible(string? valueType, string? targetType, bool exact = false);

    /// <summary>値の形を、別の形へ暗黙に変換できるかを判定する。</summary>
    /// <param name="value">値の形。</param>
    /// <param name="target">変換先の形。</param>
    /// <param name="exact">形が同じであることを求めるなら <see langword="true"/>。</param>
    /// <returns>変換できれば <see langword="true"/>。</returns>
    public abstract bool IsConvertible(ShaderNumericShape value, ShaderNumericShape target, bool exact);

    /// <summary>変換で成分が切り捨てられるかを判定する。</summary>
    /// <param name="valueType">値の型。</param>
    /// <param name="targetType">変換先の型。</param>
    /// <returns>切り捨てられると分かれば <see langword="true"/>。</returns>
    public abstract bool IsTruncating(string? valueType, string? targetType);

    // --------------------------------------------------------------------
    // 組み込み
    // --------------------------------------------------------------------

    /// <summary>その名前が、あらかじめ定義されている定数かを判定する。</summary>
    /// <param name="name">名前。</param>
    /// <returns>定義済みの定数なら <see langword="true"/>。</returns>
    public abstract bool IsKnownConstant(string? name);

    /// <summary>その名前が、その木の言語のバージョンで使える組み込み関数かを判定する。</summary>
    /// <param name="name">名前。</param>
    /// <param name="tree">名前を使っている木。言語のバージョン (HLSL ならコンパイラと HLSL のバージョン) はここから読む。</param>
    /// <returns>組み込み関数なら <see langword="true"/>。</returns>
    public abstract bool IsKnownFunction(string? name, ShaderSyntaxTree tree);

    /// <summary>組み込み関数の名前。補完の候補に出すために使う。</summary>
    public abstract IReadOnlyCollection<string> FunctionNames { get; }

    /// <summary>組み込み関数が受け付ける引数の数を引く。</summary>
    /// <param name="name">関数の名前。</param>
    /// <param name="arity">受け付ける引数の数。</param>
    /// <returns>表にあれば <see langword="true"/>。</returns>
    /// <remarks>表を持たない言語は、引数の数を確かめない (既定)。</remarks>
    internal virtual bool TryGetIntrinsicArity(string name, out IntrinsicArity arity)
    {
        arity = default;
        return false;
    }

    /// <summary>テクスチャのメソッドが何を返すかを求める。</summary>
    /// <param name="name">メソッドの名前。</param>
    /// <returns>返すもの。表に無ければ <see cref="TextureMethodReturnType.Unknown"/>。</returns>
    public abstract TextureMethodReturnType GetTextureMethodReturnType(string? name);

    /// <summary>テクスチャを読む関数またはメソッドかを判定する。</summary>
    /// <param name="name">関数またはメソッドの名前。</param>
    /// <returns>テクスチャを読むものなら <see langword="true"/>。</returns>
    public abstract bool IsTextureSamplingFunction(string? name);

    /// <summary>組み込み関数の結果の型の決め方を返す。</summary>
    /// <param name="name">関数の名前。</param>
    /// <returns>決め方。表に無ければ <see cref="IntrinsicResultRule.Unknown"/>。</returns>
    public abstract IntrinsicResultRule GetResultShape(string? name);

    /// <summary>引数と同じ形で返す組み込み関数の、結果の要素の型を返す。</summary>
    /// <param name="name">関数の名前。</param>
    /// <returns>要素の型。表に無ければ <see langword="null"/>。</returns>
    public abstract string? GetFixedBaseType(string? name);

    /// <summary>引数によらず決まっている組み込み関数の結果の型を返す。</summary>
    /// <param name="name">関数の名前。</param>
    /// <returns>型。表に無ければ <see langword="null"/>。</returns>
    public abstract string? GetFixedType(string? name);
}
