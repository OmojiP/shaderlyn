namespace Shaderlyn.Core.Text;

/// <summary>
/// ソーステキスト上の行・桁による位置を表す。
/// </summary>
/// <remarks>
/// <see cref="Line"/> と <see cref="Character"/> はいずれも 0 始まりで保持する。
/// エディタや SARIF が期待する 1 始まりの値へは出力層で変換する。
/// 内部表現を 0 始まりに統一しているのは、行頭からのオフセット計算に補正が要らず、
/// 変換漏れによる 1 行ずれを混入させにくいためである。
/// </remarks>
/// <param name="Line">0 始まりの行番号。</param>
/// <param name="Character">0 始まりの桁番号 (UTF-16 コードユニット単位)。</param>
public readonly record struct LinePosition(int Line, int Character) : IComparable<LinePosition>
{
    /// <summary>行・桁の順で位置を比較する。</summary>
    /// <param name="other">比較対象。</param>
    /// <returns>順序を表す値。</returns>
    public int CompareTo(LinePosition other)
    {
        int byLine = Line.CompareTo(other.Line);
        return byLine != 0 ? byLine : Character.CompareTo(other.Character);
    }

    /// <summary>1 始まりに直した <c>行:桁</c> 形式の文字列を返す。</summary>
    /// <returns>人間が読む前提の文字列表現。</returns>
    /// <remarks>
    /// 内部表現は 0 始まりだが、この文字列は利用者に提示されるものなので
    /// エディタの表示に合わせて 1 始まりへ変換する。
    /// </remarks>
    public override string ToString() => $"{Line + 1}:{Character + 1}";
}

/// <summary>
/// ソーステキスト上の行・桁による範囲を表す。
/// </summary>
/// <param name="Start">範囲の開始位置 (この位置を含む)。</param>
/// <param name="End">範囲の終端位置 (この位置は含まない)。</param>
public readonly record struct LinePositionSpan(LinePosition Start, LinePosition End)
{
    /// <summary>範囲が単一行に収まっているかどうか。</summary>
    public bool IsSingleLine => Start.Line == End.Line;

    /// <summary>この範囲を <c>開始-終端</c> 形式の文字列で表す。</summary>
    /// <returns>人間が読む前提の文字列表現。</returns>
    public override string ToString() => $"{Start}-{End}";
}
