using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Shaderlyn.Core.Syntax;

/// <summary>
/// シンボルの宣言から分かる、構成の制約。
/// </summary>
/// <remarks>
/// <para>
/// Unity がコンパイルするのは、宣言の行から作った組み合わせだけである。
/// 条件の代数 (<see cref="SymbolCondition"/>) はこの事実を知らないので、
/// 実在しない構成を「成り立つ」と扱い、決して通らない分岐を検査して誤りを報告する。
/// 条件を「成り立つか」で判断する箇所は、ここを通して判断しなければならない。
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>同じ行のシンボルは、同時には定義されない。</b>
///     <c>#pragma multi_compile _ _X _Y</c> は「無し・<c>_X</c>・<c>_Y</c>」の 3 通りである
///   </description></item>
///   <item><description>
///     <b><c>_</c> の無い <c>multi_compile</c> の行は、どれか 1 つが必ず定義される。</b>
///     <c>#pragma multi_compile MODE_A MODE_B</c> に「どちらも無い」構成は無い
///   </description></item>
/// </list>
/// <para>
/// 実行時には同じ行のキーワードを 2 つ有効にすることも、全部を無効にすることもできるが、
/// そのとき使われるのはコンパイル済みのどれかのバリアントである。
/// 実在しない組み合わせのコードがコンパイルされることは無い。
/// </para>
/// </remarks>
public sealed class SymbolConstraints
{
    /// <summary>制約を 1 つも持たない。</summary>
    public static SymbolConstraints Empty { get; } = new([], []);

    private readonly FrozenSet<(string First, string Second)> _exclusivePairs;
    private readonly ImmutableArray<ImmutableArray<string>> _requiredGroups;

    /// <summary>制約から作る。</summary>
    /// <param name="exclusivePairs">同時には定義されない 2 つのシンボル。向きは問わない。</param>
    /// <param name="requiredGroups">どれか 1 つが必ず定義されるシンボルの集まり。</param>
    public SymbolConstraints(
        IEnumerable<(string First, string Second)> exclusivePairs,
        IEnumerable<IReadOnlyList<string>> requiredGroups)
    {
        ArgumentNullException.ThrowIfNull(exclusivePairs);
        ArgumentNullException.ThrowIfNull(requiredGroups);

        _exclusivePairs = exclusivePairs
            .Where(p => !string.Equals(p.First, p.Second, StringComparison.Ordinal))
            .Select(p => Order(p.First, p.Second))
            .ToFrozenSet();

        _requiredGroups = [.. requiredGroups.Where(g => g.Count > 0).Select(g => g.ToImmutableArray())];
    }

    /// <summary>宣言の行から作る。</summary>
    /// <param name="sets">1 つの宣言に並んだシンボル。集まりの中のどの 2 つも同時には定義されない。</param>
    /// <param name="requiredSets"><paramref name="sets"/> のうち、どれか 1 つが必ず定義されるもの。</param>
    /// <returns>作った制約。</returns>
    public static SymbolConstraints FromSets(
        IEnumerable<IReadOnlyList<string>> sets,
        IEnumerable<IReadOnlyList<string>> requiredSets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(requiredSets);

        List<(string, string)> pairs = [];

        foreach (IReadOnlyList<string> set in sets)
        {
            for (int i = 0; i < set.Count; i++)
            {
                for (int j = i + 1; j < set.Count; j++)
                {
                    pairs.Add((set[i], set[j]));
                }
            }
        }

        List<IReadOnlyList<string>> required = [.. requiredSets.Where(s => s.Count > 0)];

        return pairs.Count == 0 && required.Count == 0 ? Empty : new SymbolConstraints(pairs, required);
    }

    /// <summary>制約を 1 つも持たないかどうか。</summary>
    public bool IsEmpty => _exclusivePairs.Count == 0 && _requiredGroups.IsEmpty;

    /// <summary>同時には定義されない 2 つ組。名前順に並べた向きで持つ。</summary>
    public IEnumerable<(string First, string Second)> ExclusivePairs => _exclusivePairs;

    /// <summary>どれか 1 つが必ず定義されるシンボルの集まり。宣言に書かれた順に並ぶ。</summary>
    /// <remarks>
    /// 先頭のシンボルを、既定の構成で定義するものとして使う。どれを選んでも実在する構成の 1 つである。
    /// </remarks>
    public ImmutableArray<ImmutableArray<string>> RequiredGroups => _requiredGroups;

    /// <summary>2 つのシンボルが同時には定義されないかを判定する。</summary>
    /// <param name="first">1 つ目のシンボル。</param>
    /// <param name="second">2 つ目のシンボル。</param>
    /// <returns>同時には定義されないなら <see langword="true"/>。</returns>
    public bool AreExclusive(string first, string second)
        => _exclusivePairs.Contains(Order(first, second));

    /// <summary>決して成り立たない項を落とした条件を返す。</summary>
    /// <param name="condition">対象の条件。</param>
    /// <returns>落とした条件。</returns>
    public SymbolCondition Apply(SymbolCondition condition)
        => IsEmpty ? condition : condition.WithoutTerms(IsImpossible);

    /// <summary>
    /// 制約から決まる部分を省いて、短く書き直した条件を返す。
    /// </summary>
    /// <param name="condition">対象の条件。</param>
    /// <returns>実在する構成について同じ意味になる、短い条件。</returns>
    /// <remarks>
    /// <para>
    /// <b>利用者に条件を示すときに使う。</b>
    /// <c>#pragma multi_compile _A _B</c> と <c>#pragma multi_compile _C _D</c> のもとでは、
    /// <c>(_A &amp;&amp; !_C &amp;&amp; _D) || (!_B &amp;&amp; !_C &amp;&amp; _D)</c> は <c>_A &amp;&amp; _D</c> と同じである。
    /// <c>!_B</c> なら <c>_A</c> が、<c>_D</c> なら <c>!_C</c> が決まる。
    /// 長いまま示すと、読み手は自分で制約を当てはめて読み解くことになる。
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     同時には定義されないシンボルがあれば、その否定は省く (<c>_D &amp;&amp; !_C</c> → <c>_D</c>)
    ///   </description></item>
    ///   <item><description>
    ///     どれか 1 つが必ず定義される集まりで、1 つを除いて否定していれば、残りの 1 つにする (<c>!_B</c> → <c>_A</c>)
    ///   </description></item>
    ///   <item><description>
    ///     ちょうど 1 つが定義される 2 つ組は、いったん片方の真偽に揃えてから項をまとめる。
    ///     <c>(_A &amp;&amp; _D) || (_B &amp;&amp; _D)</c> は <c>_D</c> になる
    ///   </description></item>
    /// </list>
    /// <para>
    /// 実在しない構成についての意味は変わりうるので、構成が実在するかの判断には使わない (<see cref="IsPossible"/>)。
    /// </para>
    /// </remarks>
    public SymbolCondition Reduce(SymbolCondition condition)
    {
        if (IsEmpty)
        {
            return condition;
        }

        ImmutableArray<ImmutableArray<string>> exact = [.. _requiredGroups.Where(IsExactlyOne)];

        // 2 つ組を片方の真偽に揃えると、(_A ∧ X) ∨ (_B ∧ X) が (_A ∧ X) ∨ (¬_A ∧ X) になり、整え直すときにまとまる。
        SymbolCondition merged = Apply(condition).RewriteTerms(literals =>
        {
            DropImplied(literals);

            foreach (ImmutableArray<string> pair in exact.Where(g => g.Length == 2))
            {
                if (literals.TryGetValue(pair[1], out bool second) && second)
                {
                    literals.Remove(pair[1]);
                    literals[pair[0]] = false;
                }
            }
        });

        // 示すときは、否定ではなく定義されている側の名前で書く。
        return merged.RewriteTerms(literals =>
        {
            foreach (ImmutableArray<string> pair in exact.Where(g => g.Length == 2))
            {
                if (literals.TryGetValue(pair[0], out bool first) && !first)
                {
                    literals.Remove(pair[0]);
                    literals[pair[1]] = true;
                }
            }
        });
    }

    /// <summary>項のうち、制約から決まるシンボルを省く。</summary>
    /// <param name="literals">項のシンボルと真偽。その場で書き換える。</param>
    private void DropImplied(Dictionary<string, bool> literals)
    {
        // どれか 1 つが必ず定義される集まりで、1 つを除いて否定していれば、残りの 1 つが定義されている。
        foreach (ImmutableArray<string> group in _requiredGroups)
        {
            string[] open = [.. group.Where(s => !literals.TryGetValue(s, out bool defined) || defined)];

            if (open.Length == 1 && !literals.ContainsKey(open[0]))
            {
                literals[open[0]] = true;
            }
        }

        // 同時には定義されないシンボルが定義されていれば、その否定は何も足さない。
        string[] definedSymbols = [.. literals.Where(p => p.Value).Select(p => p.Key)];

        foreach (string symbol in literals.Where(p => !p.Value).Select(p => p.Key).ToArray())
        {
            if (definedSymbols.Any(d => AreExclusive(d, symbol)))
            {
                literals.Remove(symbol);
            }
        }
    }

    /// <summary>集まりのどの 2 つも同時には定義されないか (どれか 1 つが必ず定義される集まりなら、ちょうど 1 つになる)。</summary>
    /// <param name="group">対象の集まり。</param>
    /// <returns>どの 2 つも同時には定義されないなら <see langword="true"/>。</returns>
    private bool IsExactlyOne(ImmutableArray<string> group)
    {
        for (int i = 0; i < group.Length; i++)
        {
            for (int j = i + 1; j < group.Length; j++)
            {
                if (!AreExclusive(group[i], group[j]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>その条件が成り立つ構成があるかを判定する。</summary>
    /// <param name="condition">対象の条件。</param>
    /// <returns>あれば <see langword="true"/>。<see cref="SymbolCondition.Unknown"/> もあるものとする。</returns>
    public bool IsPossible(SymbolCondition condition) => !Apply(condition).IsNever;

    /// <summary>
    /// 条件が成り立つために、有効にする必要があるシンボルの組を列挙する。
    /// </summary>
    /// <param name="condition">対象の条件。</param>
    /// <returns>項ごとの、有効にするシンボル。名前順に並ぶ。</returns>
    /// <remarks>
    /// <para>
    /// <see cref="SymbolCondition.EnumerateRequiredCombinations"/> と同じく、定義されている側のシンボルを組にする。
    /// 1 つだけの組は 1 つずつの構成で足りるので返さない。
    /// </para>
    /// <para>
    /// <b>どれか 1 つが必ず定義される集まりでは、先頭が無いことも有効にするものを求める。</b>
    /// 既定の構成は先頭を定義しているので、<c>#ifdef MODE_A ... #else</c> の <c>#else</c> は、
    /// 同じ集まりの別のシンボルを有効にしないと通らない。
    /// その場合は、項が否定していない最初のシンボルを足す。足したものは 1 つだけの組でも返す。
    /// </para>
    /// </remarks>
    public IEnumerable<ImmutableArray<string>> EnumerateRequiredCombinations(SymbolCondition condition)
    {
        foreach (ImmutableArray<SymbolTerm> term in Apply(condition).EnumerateTerms())
        {
            SortedSet<string> required = new(
                term.Where(t => t.IsDefined).Select(t => t.Symbol),
                StringComparer.Ordinal);

            bool substituted = false;

            foreach (ImmutableArray<string> group in _requiredGroups)
            {
                if (group.Any(required.Contains) || !term.Contains(new SymbolTerm(group[0], IsDefined: false)))
                {
                    continue;
                }

                // 成り立たない項は Apply で落としてあるので、否定されていないシンボルが必ずある。
                string? other = group.FirstOrDefault(s => !term.Contains(new SymbolTerm(s, IsDefined: false)));

                if (other is not null)
                {
                    required.Add(other);
                    substituted = true;
                }
            }

            if (required.Count > 1 || (substituted && required.Count == 1))
            {
                yield return [.. required];
            }
        }
    }

    /// <summary>項が、実在しない構成を求めているかを判定する。</summary>
    /// <param name="term">対象の項。</param>
    /// <returns>求めているなら <see langword="true"/>。</returns>
    /// <remarks>
    /// 同時には定義されない 2 つを両方とも「定義されている」とする項と、
    /// どれか 1 つが必ず定義される集まりの全部を「定義されていない」とする項が、それにあたる。
    /// </remarks>
    private bool IsImpossible(ImmutableArray<SymbolTerm> term)
    {
        for (int i = 0; i < term.Length; i++)
        {
            if (!term[i].IsDefined)
            {
                continue;
            }

            for (int j = i + 1; j < term.Length; j++)
            {
                if (term[j].IsDefined && AreExclusive(term[i].Symbol, term[j].Symbol))
                {
                    return true;
                }
            }
        }

        foreach (ImmutableArray<string> group in _requiredGroups)
        {
            if (group.All(symbol => term.Contains(new SymbolTerm(symbol, IsDefined: false))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>2 つを名前順に並べる。</summary>
    /// <param name="first">1 つ目。</param>
    /// <param name="second">2 つ目。</param>
    /// <returns>並べた組。</returns>
    private static (string, string) Order(string first, string second)
        => string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);
}
