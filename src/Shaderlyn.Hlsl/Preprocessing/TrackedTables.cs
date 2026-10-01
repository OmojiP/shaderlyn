using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// 名前で引くたびに、引いた名前を知らせる表。
/// </summary>
/// <typeparam name="TValue">値の型。</typeparam>
/// <remarks>
/// <para>
/// <b>取り込みの展開結果を使い回す鍵を、そのヘッダが実際に読んだ名前に絞るためにある</b> (<see cref="HlslIncludeCache"/>)。
/// 展開の結果は、表の中身のうち読んだ名前の状態だけで決まる。
/// 読む箇所に 1 つずつ記録を足すと、足し忘れた箇所で違う状態の結果を使い回してしまう。
/// 読む操作をこの型に限れば、読んだことは必ず知らされる。
/// </para>
/// <para>
/// 書き換えと、書き換えのための読み (ハッシュの差し引き) は <c>Raw</c> の付いたメンバーで行い、知らせない。
/// </para>
/// </remarks>
internal sealed class TrackedTable<TValue>
{
    private readonly Dictionary<string, TValue> _inner = new(StringComparer.Ordinal);

    /// <summary>名前を引いたときに呼ばれる。</summary>
    public Action<string>? OnRead { get; set; }

    /// <summary>値を引く。引いた名前を知らせる。</summary>
    /// <param name="name">名前。</param>
    /// <param name="value">値。</param>
    /// <returns>あれば <see langword="true"/>。</returns>
    public bool TryGetValue(string name, [MaybeNullWhen(false)] out TValue value)
    {
        OnRead?.Invoke(name);
        return _inner.TryGetValue(name, out value);
    }

    /// <summary>名前があるかを判定する。引いた名前を知らせる。</summary>
    /// <param name="name">名前。</param>
    /// <returns>あれば <see langword="true"/>。</returns>
    public bool ContainsKey(string name)
    {
        OnRead?.Invoke(name);
        return _inner.ContainsKey(name);
    }

    /// <summary>値を引く。無ければ既定値。引いた名前を知らせる。</summary>
    /// <param name="name">名前。</param>
    /// <returns>値。</returns>
    public TValue? GetValueOrDefault(string name)
    {
        OnRead?.Invoke(name);
        return _inner.GetValueOrDefault(name);
    }

    /// <summary>値を引く。知らせない。書き換えのためにだけ使う。</summary>
    /// <param name="name">名前。</param>
    /// <param name="value">値。</param>
    /// <returns>あれば <see langword="true"/>。</returns>
    public bool RawTryGetValue(string name, [MaybeNullWhen(false)] out TValue value) => _inner.TryGetValue(name, out value);

    /// <summary>値を書く。</summary>
    /// <param name="name">名前。</param>
    /// <param name="value">値。</param>
    public void RawSet(string name, TValue value) => _inner[name] = value;

    /// <summary>名前を消す。</summary>
    /// <param name="name">名前。</param>
    /// <returns>消したなら <see langword="true"/>。</returns>
    public bool RawRemove(string name) => _inner.Remove(name);

    /// <summary>今の中身を、変わらない表にして返す。展開を終えたあとで使う。</summary>
    /// <returns>中身の複製。</returns>
    public ImmutableDictionary<string, TValue> ToImmutableDictionary()
        => _inner.ToImmutableDictionary(StringComparer.Ordinal);
}

/// <summary>
/// 名前で引くたびに、引いた名前を知らせる名前の集合。
/// </summary>
/// <remarks>
/// <see cref="TrackedTable{TValue}"/> と同じ目的で置く。
/// 空かどうかを見る読み (<see cref="Count"/>) は、特別な名前 <see cref="CountName"/> を読んだものとして知らせる。
/// </remarks>
internal sealed class TrackedNameSet
{
    /// <summary>集合の要素数を見たことを表す名前。識別子にはならない文字を含む。</summary>
    public const string CountName = "\u0001count";

    private readonly HashSet<string> _inner = new(StringComparer.Ordinal);

    /// <summary>名前を引いたときに呼ばれる。</summary>
    public Action<string>? OnRead { get; set; }

    /// <summary>要素数。見たことを <see cref="CountName"/> として知らせる。</summary>
    public int Count
    {
        get
        {
            OnRead?.Invoke(CountName);
            return _inner.Count;
        }
    }

    /// <summary>要素数。知らせない。</summary>
    public int RawCount => _inner.Count;

    /// <summary>名前があるかを判定する。引いた名前を知らせる。</summary>
    /// <param name="name">名前。</param>
    /// <returns>あれば <see langword="true"/>。</returns>
    public bool Contains(string name)
    {
        OnRead?.Invoke(name);
        return _inner.Contains(name);
    }

    /// <summary>名前があるかを判定する。知らせない。</summary>
    /// <param name="name">名前。</param>
    /// <returns>あれば <see langword="true"/>。</returns>
    public bool RawContains(string name) => _inner.Contains(name);

    /// <summary>名前を足す。</summary>
    /// <param name="name">名前。</param>
    /// <returns>足したなら <see langword="true"/>。</returns>
    public bool RawAdd(string name) => _inner.Add(name);
}
