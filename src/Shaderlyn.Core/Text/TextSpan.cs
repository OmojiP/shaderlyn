namespace Shaderlyn.Core.Text;

/// <summary>
/// ソーステキスト上の連続した文字範囲を表す。
/// </summary>
/// <remarks>
/// <para>
/// 位置は UTF-16 コードユニット単位のオフセットであり、行・列ではない。
/// 行・列への変換は <see cref="SourceText.GetLinePosition(int)"/> が担当する。
/// 構文ノード・トークン・診断がそれぞれ自分の位置を保持するための基本単位として全層で使う。
/// </para>
/// <para>
/// 行・列ではなくオフセットを保持する理由は 2 つある。
/// 1 つはトークンごとに行・列を持つと字句解析のコストが上がること、
/// もう 1 つは範囲の包含判定・重なり判定が整数比較だけで済み、
/// 「この診断は PR の変更行に含まれるか」といった判定を高速に行えることである。
/// </para>
/// </remarks>
public readonly record struct TextSpan : IComparable<TextSpan>
{
    /// <summary>
    /// 指定した開始位置と長さで範囲を生成する。
    /// </summary>
    /// <param name="start">範囲の開始オフセット。0 以上でなければならない。</param>
    /// <param name="length">範囲の長さ。0 以上でなければならない。長さ 0 は挿入位置を指す空範囲として有効。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="start"/> または <paramref name="length"/> が負の場合、
    /// あるいは終端が <see cref="int"/> の範囲を超える場合。
    /// </exception>
    public TextSpan(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)start + length, int.MaxValue);

        Start = start;
        Length = length;
    }

    /// <summary>範囲の開始オフセット (この位置を含む)。</summary>
    public int Start { get; }

    /// <summary>範囲の長さ。</summary>
    public int Length { get; }

    /// <summary>範囲の終端オフセット (この位置は含まない)。</summary>
    public int End => Start + Length;

    /// <summary>範囲が空 (長さ 0) かどうか。</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>
    /// 開始位置と終端位置から範囲を生成する。
    /// </summary>
    /// <param name="start">開始オフセット (この位置を含む)。</param>
    /// <param name="end">終端オフセット (この位置は含まない)。<paramref name="start"/> 以上でなければならない。</param>
    /// <returns>生成された範囲。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="end"/> が <paramref name="start"/> より小さい場合。</exception>
    public static TextSpan FromBounds(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        return new TextSpan(start, end - start);
    }

    /// <summary>
    /// 指定したオフセットがこの範囲に含まれるかを判定する。
    /// </summary>
    /// <param name="position">判定するオフセット。</param>
    /// <returns>含まれる場合は <see langword="true"/>。</returns>
    /// <remarks>終端位置は含まれない。したがって空範囲はいかなる位置も含まない。</remarks>
    public bool Contains(int position) => (uint)(position - Start) < (uint)Length;

    /// <summary>
    /// 指定したオフセットがこの範囲に触れているかを判定する。終端位置も含める。
    /// </summary>
    /// <param name="position">判定するオフセット。</param>
    /// <returns>範囲の中か、その終端にある場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <see cref="Contains(int)"/> との違いは終端の扱いだけである。
    /// エディタのカーソルは文字と文字の間にあり、語を打ち終えた直後のカーソルは語の終端にある。
    /// そこでも「この語の上」と答えるには、終端を含める必要がある。
    /// </para>
    /// <para>
    /// 空範囲では、その位置だけが触れていることになる。Roslyn の同名のメソッドと同じ振る舞いである。
    /// </para>
    /// </remarks>
    public bool IntersectsWith(int position) => (uint)(position - Start) <= (uint)Length;

    /// <summary>
    /// 指定した範囲がこの範囲に完全に含まれるかを判定する。
    /// </summary>
    /// <param name="other">判定する範囲。</param>
    /// <returns>完全に含まれる場合は <see langword="true"/>。</returns>
    public bool Contains(TextSpan other) => other.Start >= Start && other.End <= End;

    /// <summary>
    /// 2 つの範囲が 1 文字以上重なっているかを判定する。
    /// </summary>
    /// <param name="other">判定する範囲。</param>
    /// <returns>重なっている場合は <see langword="true"/>。接しているだけの場合は <see langword="false"/>。</returns>
    public bool OverlapsWith(TextSpan other) => Math.Max(Start, other.Start) < Math.Min(End, other.End);

    /// <summary>
    /// 2 つの範囲の両方を含む最小の範囲を返す。
    /// </summary>
    /// <param name="other">結合する範囲。</param>
    /// <returns>両者を包含する最小の範囲。間に隙間があってもそれを含んだ範囲になる。</returns>
    public TextSpan Union(TextSpan other) => FromBounds(Math.Min(Start, other.Start), Math.Max(End, other.End));

    /// <summary>
    /// 開始位置、次いで長さの順で範囲を比較する。
    /// </summary>
    /// <param name="other">比較対象。</param>
    /// <returns>順序を表す値。</returns>
    /// <remarks>診断を出力前にソースコード上の出現順へ並べ替えるために使う。</remarks>
    public int CompareTo(TextSpan other)
    {
        int byStart = Start.CompareTo(other.Start);
        return byStart != 0 ? byStart : Length.CompareTo(other.Length);
    }

    /// <summary>この範囲を <c>[開始..終端)</c> 形式の文字列で表す。</summary>
    /// <returns>デバッグ用の文字列表現。</returns>
    public override string ToString() => $"[{Start}..{End})";
}
