using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Core.Syntax;

/// <summary>構成の軸になるシンボルの種類。</summary>
public enum VariantSymbolKind
{
    /// <summary>構成ごとに別々にコンパイルされる (Unity の <c>multi_compile</c> / <c>shader_feature</c>、Unreal のパーミュテーション)。</summary>
    Compiled,

    /// <summary>構成を作らず、実行時の分岐になる (Unity の <c>dynamic_branch</c>)。</summary>
    Runtime,
}

/// <summary>
/// 構成の軸になるシンボルの宣言 1 つ。
/// </summary>
/// <param name="Name">シンボルの名前。</param>
/// <param name="Kind">シンボルの種類。</param>
/// <param name="Location">
/// 宣言の位置。名前が書かれていれば名前の位置、書かれていなければ (<paramref name="IsImplicit"/>) 宣言の位置。
/// 設定ファイルなど、ソースの外で宣言されたものは <see langword="null"/>。
/// </param>
/// <param name="IsImplicit">
/// 名前がどこにも書かれていない宣言かどうか。
/// Unity の <c>#pragma multi_compile_fog</c> は <c>FOG_LINEAR</c> などを、名前を書かずに宣言する。
/// </param>
/// <remarks>
/// 同じ名前が何度も宣言されることがある。宣言の数だけ並ぶ (重複の検査に要る)。
/// </remarks>
public sealed record VariantDeclaration(
    string Name,
    VariantSymbolKind Kind,
    Location? Location,
    bool IsImplicit = false);

/// <summary>
/// 1 つの宣言に並んだシンボルの組。
/// </summary>
/// <param name="Symbols">並んでいるシンボル。</param>
/// <param name="RequiresOne">どれか 1 つが必ず有効かどうか。</param>
/// <remarks>
/// <b>組のシンボルは、同時には有効にならない。</b>
/// <c>#pragma multi_compile _ _X _Y</c> の <c>_X</c> と <c>_Y</c> を両方有効にした構成は無い。
/// <paramref name="RequiresOne"/> が立っていれば、さらに「どれも無効」も無い。
/// </remarks>
public sealed record VariantGroup(ImmutableArray<string> Symbols, bool RequiresOne)
{
    /// <inheritdoc/>
    public bool Equals(VariantGroup? other)
        => other is not null && RequiresOne == other.RequiresOne && Symbols.SequenceEqual(other.Symbols, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(RequiresOne);

        foreach (string symbol in Symbols)
        {
            hash.Add(symbol, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// 構成の空間。どのシンボルが構成の軸になり、どの組み合わせが実在するか。
/// </summary>
/// <remarks>
/// <para>
/// <b>言語にも環境にも依らない形で持つ。</b>
/// Unity は <c>#pragma multi_compile</c> から、Unreal はパーミュテーションの宣言から、
/// 独自エンジンは設定から、それぞれこの形を作る。解析と規則はこの形だけを見る。
/// </para>
/// <para>
/// 実在する組み合わせの制約は <see cref="Constraints"/> で引く。組から作る。
/// </para>
/// </remarks>
public sealed class VariantSpace
{
    /// <summary>シンボルを 1 つも持たない空間。</summary>
    public static VariantSpace Empty { get; } = new([], []);

    private SymbolConstraints? _constraints;

    /// <summary>宣言と組から作る。</summary>
    /// <param name="declarations">宣言。宣言された順に並べる。</param>
    /// <param name="groups">組。宣言された順に並べる。</param>
    public VariantSpace(IEnumerable<VariantDeclaration> declarations, IEnumerable<VariantGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(groups);

        Declarations = [.. declarations];
        Groups = [.. groups.Where(g => !g.Symbols.IsDefaultOrEmpty)];
        Symbols = Declarations.Select(d => d.Name).ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <summary>宣言。宣言された順に、宣言の数だけ並ぶ。</summary>
    public ImmutableArray<VariantDeclaration> Declarations { get; }

    /// <summary>組。宣言された順に並ぶ。</summary>
    public ImmutableArray<VariantGroup> Groups { get; }

    /// <summary>宣言されたシンボルの名前。</summary>
    public ImmutableHashSet<string> Symbols { get; }

    /// <summary>シンボルが 1 つも無いかどうか。</summary>
    public bool IsEmpty => Declarations.IsEmpty;

    /// <summary>
    /// 値を持つシンボルと、その値の範囲。名前から、とりうる値 (小さい順) への対応。
    /// </summary>
    /// <remarks>
    /// 宣言の原子 (<see cref="VariantValues.Atom"/>) から集める。値を持つシンボルが無ければ空。
    /// </remarks>
    public ImmutableDictionary<string, ImmutableArray<int>> ValueSymbols => _valueSymbols ??= VariantValues.Collect(Symbols);

    private ImmutableDictionary<string, ImmutableArray<int>>? _valueSymbols;

    /// <summary>
    /// 値を持つシンボルを宣言した空間を作る。
    /// </summary>
    /// <param name="name">シンボルの名前。</param>
    /// <param name="values">とりうる値。先頭が既定の値になる。</param>
    /// <param name="location">宣言の位置。設定ファイルなどソースの外なら <see langword="null"/>。</param>
    /// <returns>値ごとの原子と、「ちょうど 1 つ」の組を持つ空間。</returns>
    public static VariantSpace ForValues(string name, IReadOnlyList<int> values, Location? location = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(values);

        ImmutableArray<string> atoms = [.. values.Distinct().Select(value => VariantValues.Atom(name, value))];

        return new VariantSpace(
            atoms.Select(atom => new VariantDeclaration(atom, VariantSymbolKind.Compiled, location)),
            [new VariantGroup(atoms, RequiresOne: true)]);
    }

    /// <summary>空間を重ねる。</summary>
    /// <param name="other">重ねる空間。</param>
    /// <returns>両方の宣言と組を持つ空間。</returns>
    public VariantSpace Union(VariantSpace other)
    {
        ArgumentNullException.ThrowIfNull(other);

        // 組だけの空間 (宣言は別の空間にある値の、同時には有効にならない組) も足す。
        return other.IsEmpty && other.Groups.IsEmpty ? this
            : IsEmpty && Groups.IsEmpty ? other
            : new VariantSpace([.. Declarations, .. other.Declarations], [.. Groups, .. other.Groups]);
    }

    /// <summary>組から分かる、実在する組み合わせの制約。</summary>
    public SymbolConstraints Constraints => _constraints ??= SymbolConstraints.FromSets(
        Groups.Select(g => (IReadOnlyList<string>)g.Symbols),
        Groups.Where(g => g.RequiresOne).Select(g => (IReadOnlyList<string>)g.Symbols));

    /// <summary>シンボルが宣言されているかを判定する。</summary>
    /// <param name="name">シンボルの名前。</param>
    /// <returns>宣言されていれば <see langword="true"/>。</returns>
    public bool IsDeclared(string name) => Symbols.Contains(name);
}
