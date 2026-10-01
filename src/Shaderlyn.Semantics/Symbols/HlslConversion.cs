namespace Shaderlyn.Semantics.Symbols;

/// <summary>
/// HLSL の暗黙の変換が成り立つかを判定する。
/// </summary>
/// <remarks>
/// <para>
/// <b>1 か所に置く。</b>
/// この判定は引数の受け渡し (HL0340)、代入と初期化 (HL0350)、
/// <c>return</c> (HL0351) の 3 か所で必要になる。
/// 書き写すと、片方だけが規則の追加に追従して食い違う。
/// </para>
/// <para>
/// <b>確実に誤りと言える向きだけを false にする。</b>
/// HLSL は成分を減らす変換を警告付きで許し、スカラーからの複製も許す。
/// 許さないのは成分が増える向きだけである。
/// </para>
/// </remarks>
public static class HlslConversion
{
    /// <summary>
    /// 値の型を、受け取る側の型へ渡せるかを判定する。
    /// </summary>
    /// <param name="valueType">渡す値の型名。</param>
    /// <param name="targetType">受け取る側の型名。</param>
    /// <param name="exact">形が一致していなければならないかどうか。</param>
    /// <returns>
    /// 渡せない場合だけ <see langword="false"/>。
    /// どちらかの形が分からない場合は <see langword="true"/>。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 型が分からないことは「合っていない」ことの証拠にならない。
    /// 分からないものは通す側に倒す。
    /// </para>
    /// <para>
    /// <b><c>void</c> だけは別である。</b>
    /// 「分からない」ではなく「値が無い」と分かっている。
    /// <c>clip</c> や <c>sincos</c> の結果を値として使っている箇所がこれに当たる。
    /// </para>
    /// </remarks>
    public static bool IsConvertible(string? valueType, string? targetType, bool exact = false)
        => !string.Equals(valueType, VoidTypeName, StringComparison.Ordinal)
           && (!HlslTypeClassifier.TryDescribeNumeric(valueType, out HlslNumericShape value)
               || !HlslTypeClassifier.TryDescribeNumeric(targetType, out HlslNumericShape target)
               || IsConvertible(value, target, exact));

    /// <summary>値を返さない型の名前。</summary>
    private const string VoidTypeName = "void";

    /// <summary>
    /// 値の形を、受け取る側の形へ渡せるかを判定する。
    /// </summary>
    /// <param name="value">渡す値の形。</param>
    /// <param name="target">受け取る側の形。</param>
    /// <param name="exact">形が一致していなければならないかどうか。</param>
    /// <returns>渡せるなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 基底型の違い (<c>half</c> と <c>float</c>) は暗黙に変換されるので見ない。
    /// 見るのは形だけである。
    /// </para>
    /// <para>
    /// <b><c>out</c> / <c>inout</c> では成分を減らす向きも許されない。</b>
    /// 書き戻す先が足りないためである。その場合に <paramref name="exact"/> を立てる。
    /// </para>
    /// </remarks>
    public static bool IsConvertible(HlslNumericShape value, HlslNumericShape target, bool exact)
    {
        if (exact)
        {
            return value.Kind == target.Kind
                   && value.Rows == target.Rows
                   && value.Columns == target.Columns;
        }

        // スカラーは全成分へ複製される。行列にも渡せる。
        if (value.Kind == HlslNumericKind.Scalar)
        {
            return true;
        }

        // 行列とベクトルは互いに渡せない。受け取る側がスカラーなら切り捨てられる。
        if (value.Kind != target.Kind)
        {
            return target.Kind == HlslNumericKind.Scalar;
        }

        return value.Kind == HlslNumericKind.Matrix
            ? value.Rows >= target.Rows && value.Columns >= target.Columns
            : value.Columns >= target.Columns;
    }

    /// <summary>
    /// 渡せはするが、成分が落ちる変換かどうかを判定する。
    /// </summary>
    /// <param name="valueType">渡す値の型名。</param>
    /// <param name="targetType">受け取る側の型名。</param>
    /// <returns>成分が落ちる場合だけ <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>これはコンパイルを妨げない。</b>
    /// HLSL は成分を減らす変換を警告 (fxc の X3206) 付きで通す。
    /// <see cref="IsConvertible(string?, string?, bool)"/> が <see langword="true"/> を返す範囲の中の、
    /// 黙って値が落ちる部分を切り出したものである。
    /// </para>
    /// <para>
    /// <b>スカラーは落ちない。</b>
    /// 全成分へ複製されるので、<c>float</c> を <c>float3</c> へ渡しても失われる値は無い。
    /// </para>
    /// <para>
    /// どちらかの形が分からなければ <see langword="false"/> を返す。
    /// 分からないものを根拠に「値が落ちている」と言ってはならない。
    /// </para>
    /// </remarks>
    public static bool IsTruncating(string? valueType, string? targetType)
        => HlslTypeClassifier.TryDescribeNumeric(valueType, out HlslNumericShape value)
           && HlslTypeClassifier.TryDescribeNumeric(targetType, out HlslNumericShape target)
           && value.Kind != HlslNumericKind.Scalar
           && (value.Rows * value.Columns) > (target.Rows * target.Columns);
}
