using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Shaderlyn.Core.Syntax;

/// <summary>
/// 値を持つシンボル (<c>QUALITY ∈ {0, 1, 2}</c>) を、論理値の原子として表す。
/// </summary>
/// <remarks>
/// <para>
/// <b>値を持つシンボルは、「その値であること」の原子に落とす。</b>
/// <c>QUALITY</c> が 0 / 1 / 2 をとるなら、<c>QUALITY==0</c> / <c>QUALITY==1</c> / <c>QUALITY==2</c> の 3 つの原子を作り、
/// 「ちょうど 1 つが成り立つ」組にする (<see cref="VariantGroup"/> の <c>RequiresOne</c>)。
/// 条件の代数 (<see cref="SymbolCondition"/>) と制約の仕組み (<see cref="SymbolConstraints"/>) は、論理値のまま使える。
/// </para>
/// <para>
/// Unreal のパーミュテーション (<c>SHADER_PERMUTATION_INT("QUALITY", 3)</c>) や、
/// 独自エンジンが <c>-D QUALITY=2</c> の組を変えてコンパイルする構成がこの形になる。
/// Unity のシンボルは値を持たない (定義されているか・いないか)。
/// </para>
/// </remarks>
public static class VariantValues
{
    /// <summary>原子の名前の中で、名前と値を区切る文字列。</summary>
    private const string Separator = "==";

    /// <summary>「シンボルがその値である」原子の名前を作る。</summary>
    /// <param name="name">シンボルの名前。</param>
    /// <param name="value">値。</param>
    /// <returns>原子の名前 (<c>QUALITY==2</c>)。</returns>
    public static string Atom(string name, int value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return string.Create(CultureInfo.InvariantCulture, $"{name}{Separator}{value}");
    }

    /// <summary>原子から、値を持つシンボルと値の範囲を集める。</summary>
    /// <param name="symbols">シンボルの名前。値を持たないシンボルは無視する。</param>
    /// <returns>名前から、とりうる値 (小さい順) への対応。</returns>
    public static ImmutableDictionary<string, ImmutableArray<int>> Collect(IEnumerable<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        Dictionary<string, SortedSet<int>> values = new(StringComparer.Ordinal);

        foreach (string symbol in symbols)
        {
            if (TryParseAtom(symbol, out string? name, out int value))
            {
                if (!values.TryGetValue(name, out SortedSet<int>? set))
                {
                    set = values[name] = [];
                }

                set.Add(value);
            }
        }

        return values.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableArray(), StringComparer.Ordinal);
    }

    /// <summary>原子の名前を、シンボルの名前と値に分ける。</summary>
    /// <param name="atom">原子の名前。</param>
    /// <param name="name">シンボルの名前。</param>
    /// <param name="value">値。</param>
    /// <returns>値を持つシンボルの原子なら <see langword="true"/>。</returns>
    public static bool TryParseAtom(string atom, [NotNullWhen(true)] out string? name, out int value)
    {
        ArgumentNullException.ThrowIfNull(atom);

        int separator = atom.IndexOf(Separator, StringComparison.Ordinal);

        if (separator > 0
            && int.TryParse(atom.AsSpan(separator + Separator.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            name = atom[..separator];
            return true;
        }

        name = null;
        value = 0;
        return false;
    }
}
