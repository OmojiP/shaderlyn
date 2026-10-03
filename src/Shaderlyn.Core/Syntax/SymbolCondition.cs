using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Shaderlyn.Core.Syntax;

/// <summary>
/// 条件式に現れるシンボル 1 つと、その真偽。
/// </summary>
/// <param name="Symbol">シンボルの名前。</param>
/// <param name="IsDefined">定義されている側を指すなら <see langword="true"/>。</param>
/// <remarks>
/// <c>#ifdef _NORMALMAP</c> は <c>(_NORMALMAP, true)</c>、
/// その <c>#else</c> 側は <c>(_NORMALMAP, false)</c> にあたる。
/// </remarks>
internal readonly record struct SymbolTerm(string Symbol, bool IsDefined)
{
    /// <summary>否定した組を返す。</summary>
    /// <returns>真偽を反転した組。</returns>
    public SymbolTerm Negate() => this with { IsDefined = !IsDefined };

    /// <summary>条件式としての表記を返す。</summary>
    /// <returns>表示用の文字列。</returns>
    public override string ToString() => IsDefined ? Symbol : $"!{Symbol}";
}

/// <summary>
/// 構文木の要素が「どのシンボルの組み合わせのもとで存在するか」。
/// </summary>
/// <remarks>
/// <para>
/// <b>積和形 (論理積の論理和) で持つ。</b>
/// <c>(A かつ B でない) または (C)</c> のような形である。
/// <c>#if</c> の入れ子が論理積を、<c>#else</c> と併合が論理和を作る。
/// </para>
/// <para>
/// <b>既定値が「常に存在する」になるようにしてある。</b>
/// 構文木のほとんどのノードは無条件であり、
/// そこに何も持たせずに済むかどうかが、この型を使えるかどうかを分ける。
/// <c>default</c> は何も確保しない。
/// </para>
/// <para>
/// <b>表しきれない条件は <see cref="Unknown"/> にする。</b>
/// 項が増えすぎた場合に近い条件で代用すると、
/// 「この構成では存在しない」という誤った判断を生む。
/// 表せないことは表せないと持つ。
/// </para>
/// </remarks>
public readonly struct SymbolCondition : IEquatable<SymbolCondition>
{
    /// <summary>
    /// 保持する項の上限。
    /// </summary>
    /// <remarks>
    /// 否定は積和形を展開するため、項の数が掛け算で増えうる。
    /// 利用者が書くコードの <c>#if</c> の入れ子は深さ 1 までで
    /// (Unity 同梱の 209 件で実測)、この上限に触れることはまず無い。
    /// 触れた場合は <see cref="Unknown"/> になり、条件を根拠にする判断が止まる。
    /// </remarks>
    public const int MaxTerms = 64;

    /// <summary>「表しきれない」ことを表す目印。</summary>
    /// <remarks>
    /// 中身ではなく<b>配列の同一性</b>で見分ける。
    /// <see cref="ImmutableArray{T}"/> の等値比較は下にある配列の参照を見るため、
    /// この目印は他のどの値とも一致しない。
    /// </remarks>
    private static readonly ImmutableArray<ImmutableArray<SymbolTerm>> UnknownMarker =
        [[new SymbolTerm("<不明>", true)]];

    /// <summary>
    /// 積和形の項。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>既定値 (<c>IsDefault</c>) は「常に存在する」</description></item>
    ///   <item><description>空の並びは「決して存在しない」</description></item>
    ///   <item><description><see cref="UnknownMarker"/> と同一なら「表しきれない」</description></item>
    /// </list>
    /// </remarks>
    private readonly ImmutableArray<ImmutableArray<SymbolTerm>> _terms;

    private SymbolCondition(ImmutableArray<ImmutableArray<SymbolTerm>> terms) => _terms = terms;

    /// <summary>常に存在する。</summary>
    public static SymbolCondition Always => default;

    /// <summary>決して存在しない。</summary>
    public static SymbolCondition Never { get; } = new([]);

    /// <summary>条件を表しきれなかった。</summary>
    public static SymbolCondition Unknown { get; } = new(UnknownMarker);

    /// <summary>常に存在するかどうか。</summary>
    public bool IsAlways => _terms.IsDefault;

    /// <summary>決して存在しないかどうか。</summary>
    public bool IsNever => !_terms.IsDefault && _terms.IsEmpty;

    /// <summary>条件を表しきれなかったかどうか。</summary>
    public bool IsUnknown => _terms == UnknownMarker;

    /// <summary>
    /// シンボル 1 つを条件にする。
    /// </summary>
    /// <param name="symbol">シンボルの名前。</param>
    /// <param name="isDefined">定義されている側を指すなら <see langword="true"/>。</param>
    /// <returns>組み立てた条件。</returns>
    public static SymbolCondition Symbol(string symbol, bool isDefined = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);
        return new([[new SymbolTerm(symbol, isDefined)]]);
    }

    /// <summary>
    /// 両方が成り立つ条件を返す。
    /// </summary>
    /// <param name="other">重ねる条件。</param>
    /// <returns>組み立てた条件。</returns>
    /// <remarks><c>#if</c> の入れ子がこの形になる。</remarks>
    public SymbolCondition And(SymbolCondition other)
    {
        if (IsUnknown || other.IsUnknown) { return Unknown; }
        if (IsAlways) { return other; }
        if (other.IsAlways) { return this; }
        if (IsNever || other.IsNever) { return Never; }

        List<ImmutableArray<SymbolTerm>> terms = [];

        foreach (ImmutableArray<SymbolTerm> left in _terms)
        {
            foreach (ImmutableArray<SymbolTerm> right in other._terms)
            {
                if (TryMergeTerm(left, right, out ImmutableArray<SymbolTerm> merged))
                {
                    terms.Add(merged);
                }
            }
        }

        return Normalize(terms);
    }

    /// <summary>
    /// どちらかが成り立つ条件を返す。
    /// </summary>
    /// <param name="other">重ねる条件。</param>
    /// <returns>組み立てた条件。</returns>
    /// <remarks>
    /// 別々の構成で同じ形が見つかったときに、この形で 1 つにまとめる。
    /// </remarks>
    public SymbolCondition Or(SymbolCondition other)
    {
        if (IsUnknown || other.IsUnknown) { return Unknown; }
        if (IsAlways || other.IsAlways) { return Always; }
        if (IsNever) { return other; }
        if (other.IsNever) { return this; }

        // 併合で最も多く通る形を、何も確保せずに片付ける。
        // 「既定の構成にも、シンボルを有効にした構成にも同じ形がある」場合が
        // ちょうど K ∨ ¬K になる。ノードの数だけ通る道なので、ここを速くする価値がある。
        if (IsSingleLiteral(out SymbolTerm left)
            && other.IsSingleLiteral(out SymbolTerm right)
            && left.IsDefined != right.IsDefined
            && string.Equals(left.Symbol, right.Symbol, StringComparison.Ordinal))
        {
            return Always;
        }

        if (Equals(other)) { return this; }

        return Normalize([.. _terms, .. other._terms]);
    }

    /// <summary>
    /// 決して成り立たない項を落とした条件を返す。
    /// </summary>
    /// <param name="isImpossible">項が成り立たないかどうかを判定する処理。</param>
    /// <returns>落とした条件。すべて落ちれば <see cref="Never"/>。</returns>
    /// <remarks>
    /// 条件の代数だけでは分からない事実 (同じ <c>#pragma</c> 行のシンボルは同時に有効にならない) を
    /// 当てはめるためにある (<see cref="SymbolConstraints"/>)。
    /// </remarks>
    internal SymbolCondition WithoutTerms(Func<ImmutableArray<SymbolTerm>, bool> isImpossible)
    {
        if (IsAlways || IsNever || IsUnknown)
        {
            return this;
        }

        List<ImmutableArray<SymbolTerm>> kept = [.. _terms.Where(term => !isImpossible(term))];

        return kept.Count == _terms.Length ? this : Normalize(kept);
    }

    /// <summary>
    /// シンボルの値をすべて決めたときに、この条件が成り立つかを判定する。
    /// </summary>
    /// <param name="isDefined">シンボルが定義されているかを答える処理。</param>
    /// <returns>成り立てば <see langword="true"/>。表しきれない条件では <see langword="false"/>。</returns>
    public bool IsSatisfiedBy(Func<string, bool> isDefined)
    {
        ArgumentNullException.ThrowIfNull(isDefined);

        if (IsAlways) { return true; }
        if (IsNever || IsUnknown) { return false; }

        return _terms.Any(term => term.All(literal => isDefined(literal.Symbol) == literal.IsDefined));
    }

    /// <summary>
    /// 値が決まっているシンボルを、その値で置き換えた条件を返す。
    /// </summary>
    /// <param name="valueOf">シンボルの値。決まっていなければ <see langword="null"/>。</param>
    /// <returns>置き換えた条件。</returns>
    /// <remarks>
    /// 1 つの構成の中では、並べずに展開したシンボルの値は決まっている。
    /// 条件にそのシンボルが残っていると、その構成では通らない分岐まで並べることになる。
    /// </remarks>
    public SymbolCondition Assume(Func<string, bool?> valueOf)
    {
        ArgumentNullException.ThrowIfNull(valueOf);

        if (IsAlways || IsNever || IsUnknown)
        {
            return this;
        }

        List<ImmutableArray<SymbolTerm>> terms = [];
        bool changed = false;

        foreach (ImmutableArray<SymbolTerm> term in _terms)
        {
            ImmutableArray<SymbolTerm>.Builder kept = ImmutableArray.CreateBuilder<SymbolTerm>(term.Length);
            bool possible = true;

            foreach (SymbolTerm literal in term)
            {
                if (valueOf(literal.Symbol) is not { } value)
                {
                    kept.Add(literal);
                    continue;
                }

                changed = true;

                if (value != literal.IsDefined)
                {
                    possible = false;
                    break;
                }
            }

            if (possible)
            {
                terms.Add(kept.ToImmutable());
            }
        }

        return changed ? Normalize(terms) : this;
    }

    /// <summary>積和形の項を列挙する。</summary>
    /// <returns>項。常に・決して成り立たない・表しきれない条件では空。</returns>
    internal IEnumerable<ImmutableArray<SymbolTerm>> EnumerateTerms()
        => IsAlways || IsNever || IsUnknown ? [] : _terms;

    /// <summary>項ごとに書き換えて、整え直した条件を返す。</summary>
    /// <param name="rewrite">項 1 つを書き換える処理。シンボルと真偽の対応を受け取り、その場で書き換える。</param>
    /// <returns>書き換えた条件。</returns>
    /// <remarks>
    /// 書き換えで同じ形になった項や、相補になった項は、整え直すときにまとまる。
    /// </remarks>
    internal SymbolCondition RewriteTerms(Action<Dictionary<string, bool>> rewrite)
    {
        if (IsAlways || IsNever || IsUnknown)
        {
            return this;
        }

        List<ImmutableArray<SymbolTerm>> terms = [];

        foreach (ImmutableArray<SymbolTerm> term in _terms)
        {
            Dictionary<string, bool> literals = term.ToDictionary(t => t.Symbol, t => t.IsDefined, StringComparer.Ordinal);
            rewrite(literals);
            terms.Add(SortTerm(literals));
        }

        return Normalize(terms);
    }

    /// <summary>シンボル 1 つだけの条件かどうかを判定する。</summary>
    /// <param name="literal">その 1 つ。</param>
    /// <returns>シンボル 1 つだけであれば <see langword="true"/>。</returns>
    private bool IsSingleLiteral(out SymbolTerm literal)
    {
        if (!IsAlways && !IsUnknown && _terms.Length == 1 && _terms[0].Length == 1)
        {
            literal = _terms[0][0];
            return true;
        }

        literal = default;
        return false;
    }

    /// <summary>
    /// 成り立たない条件を返す。
    /// </summary>
    /// <returns>否定した条件。</returns>
    /// <remarks>
    /// <c>#else</c> がこの形になる。
    /// 積和形の否定は和積形になるため、積和形へ戻すのに項を掛け合わせる。
    /// ここで項が増えすぎた場合は <see cref="Unknown"/> になる。
    /// </remarks>
    public SymbolCondition Negate()
    {
        if (IsUnknown) { return Unknown; }
        if (IsAlways) { return Never; }
        if (IsNever) { return Always; }

        // #else はほとんどがこの形になる。真偽を入れ替えるだけで済む。
        if (IsSingleLiteral(out SymbolTerm single))
        {
            return new([[single.Negate()]]);
        }

        // ¬(t1 ∨ t2) = ¬t1 ∧ ¬t2 であり、
        // ¬(a ∧ b) = ¬a ∨ ¬b なので、各項の否定を順に掛け合わせる。
        SymbolCondition result = Always;

        foreach (ImmutableArray<SymbolTerm> term in _terms)
        {
            List<ImmutableArray<SymbolTerm>> negatedTerm = [];

            foreach (SymbolTerm literal in term)
            {
                negatedTerm.Add([literal.Negate()]);
            }

            result = result.And(Normalize(negatedTerm));

            if (result.IsUnknown) { return Unknown; }
        }

        return result;
    }

    /// <summary>
    /// この条件に現れるシンボルの名前を列挙する。
    /// </summary>
    /// <returns>シンボルの名前。重複は除く。</returns>
    /// <remarks>「どのシンボルに依存しているか」を利用者へ示すのに使う。</remarks>
    public IEnumerable<string> EnumerateSymbols()
    {
        if (IsAlways || IsNever || IsUnknown)
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (ImmutableArray<SymbolTerm> term in _terms)
        {
            foreach (SymbolTerm literal in term)
            {
                if (seen.Add(literal.Symbol))
                {
                    yield return literal.Symbol;
                }
            }
        }
    }

    /// <summary>
    /// この条件が成り立つために、同時に定義されている必要があるシンボルの組を列挙する。
    /// </summary>
    /// <returns>論理積ごとの、定義されている側のシンボルの名前。</returns>
    /// <remarks>
    /// <para>
    /// <b>「1 つずつ有効にする」では通らない条件を知るために要る。</b>
    /// <c>#if defined(_A) &amp;&amp; defined(_B)</c> の中は、
    /// どちらか一方だけを有効にした構成では現れない。
    /// </para>
    /// <para>
    /// 否定の項 (<c>!_A</c>) は、定義しないことで満たせるので返さない。
    /// 論理和 (<c>||</c>) は論理積ごとに分かれて返るため、
    /// 呼び出し側はそのうちのどれか 1 つを作れば足りる。
    /// </para>
    /// </remarks>
    public IEnumerable<ImmutableArray<string>> EnumerateRequiredCombinations()
    {
        if (IsAlways || IsNever || IsUnknown)
        {
            yield break;
        }

        foreach (ImmutableArray<SymbolTerm> term in _terms)
        {
            ImmutableArray<string> required =
            [
                .. term.Where(t => t.IsDefined).Select(t => t.Symbol).Distinct(StringComparer.Ordinal)
            ];

            if (required.Length > 1)
            {
                yield return required;
            }
        }
    }

    /// <inheritdoc/>
    public bool Equals(SymbolCondition other)
    {
        if (IsUnknown || other.IsUnknown) { return IsUnknown && other.IsUnknown; }
        if (IsAlways || other.IsAlways) { return IsAlways && other.IsAlways; }
        if (_terms.Length != other._terms.Length) { return false; }

        // 正規化してあるので、順序も含めて一致する。
        for (int i = 0; i < _terms.Length; i++)
        {
            if (!_terms[i].SequenceEqual(other._terms[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj)
        => obj is SymbolCondition other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (IsAlways) { return 0; }
        if (IsUnknown) { return -1; }

        HashCode hash = new();

        foreach (ImmutableArray<SymbolTerm> term in _terms)
        {
            foreach (SymbolTerm literal in term)
            {
                hash.Add(literal);
            }

            hash.Add(0);
        }

        return hash.ToHashCode();
    }

    /// <summary>2 つの条件が等しいかを判定する。</summary>
    /// <param name="left">左辺。</param>
    /// <param name="right">右辺。</param>
    /// <returns>等しければ <see langword="true"/>。</returns>
    public static bool operator ==(SymbolCondition left, SymbolCondition right) => left.Equals(right);

    /// <summary>2 つの条件が等しくないかを判定する。</summary>
    /// <param name="left">左辺。</param>
    /// <param name="right">右辺。</param>
    /// <returns>等しくなければ <see langword="true"/>。</returns>
    public static bool operator !=(SymbolCondition left, SymbolCondition right) => !left.Equals(right);

    /// <summary>条件式としての表記を返す。</summary>
    /// <returns>表示用の文字列。</returns>
    public override string ToString()
    {
        if (IsAlways) { return "常に"; }
        if (IsNever) { return "決して成り立たない"; }
        if (IsUnknown) { return "不明"; }

        IEnumerable<string> terms = _terms.Select(
            t => t.Length == 1 ? t[0].ToString() : string.Join(" && ", t));

        return _terms.Length == 1 ? terms.First() : string.Join(" || ", terms.Select(t => $"({t})"));
    }

    /// <summary>
    /// 2 つの項を論理積で 1 つにする。
    /// </summary>
    /// <param name="left">左の項。</param>
    /// <param name="right">右の項。</param>
    /// <param name="merged">組み立てた項。</param>
    /// <returns>成り立ちうる項になれば <see langword="true"/>。</returns>
    /// <remarks>
    /// 同じシンボルの真と偽が同居したら、その項は決して成り立たないので落とす。
    /// </remarks>
    private static bool TryMergeTerm(
        ImmutableArray<SymbolTerm> left,
        ImmutableArray<SymbolTerm> right,
        out ImmutableArray<SymbolTerm> merged)
    {
        Dictionary<string, bool> literals = new(StringComparer.Ordinal);

        foreach (SymbolTerm literal in left.Concat(right))
        {
            if (literals.TryGetValue(literal.Symbol, out bool existing))
            {
                if (existing != literal.IsDefined)
                {
                    merged = default;
                    return false;
                }

                continue;
            }

            literals[literal.Symbol] = literal.IsDefined;
        }

        merged = SortTerm(literals);
        return true;
    }

    /// <summary>項を一意な並びに整える。</summary>
    /// <param name="literals">シンボルと真偽の対応。</param>
    /// <returns>整えた項。</returns>
    /// <remarks>
    /// 名前順に並べる。並びが決まっていないと、
    /// 同じ条件が別物として扱われ、併合が働かない。
    /// </remarks>
    private static ImmutableArray<SymbolTerm> SortTerm(Dictionary<string, bool> literals)
        => [.. literals.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new SymbolTerm(p.Key, p.Value))];

    /// <summary>
    /// 項の並びを正規化する。
    /// </summary>
    /// <param name="terms">整える項。</param>
    /// <returns>正規化した条件。</returns>
    /// <remarks>
    /// <para>
    /// 重複を除き、名前順に並べる。
    /// これをしないと、同じ意味の条件が別の値になり、併合で一致しない。
    /// </para>
    /// <para>
    /// <b>相補する項をまとめる。</b>
    /// <c>A ∨ ¬A</c> は「常に」であり、<c>(A ∧ B) ∨ (¬A ∧ B)</c> は <c>B</c> である。
    /// これは飾りではない。
    /// 併合で最も多いのは「既定の構成にも、シンボルを有効にした構成にも同じ形がある」場合で、
    /// その条件がまさに <c>K ∨ ¬K</c> の形になる。
    /// ここでまとめられないと、無条件のはずのノードに条件が付いたままになる。
    /// </para>
    /// <para>
    /// 吸収も行う。<c>A ∨ (A ∧ B)</c> は <c>A</c> と同じである。
    /// 入れ子の <c>#if</c> と <c>#else</c> を重ねると、この形が素直に出てくる。
    /// </para>
    /// </remarks>
    private static SymbolCondition Normalize(List<ImmutableArray<SymbolTerm>> terms)
    {
        if (terms.Count == 0) { return Never; }

        // 空の項は「無条件」を意味する。1 つでもあれば全体が常に成り立つ。
        if (terms.Any(t => t.IsEmpty)) { return Always; }

        List<ImmutableArray<SymbolTerm>> current = terms;

        // まとめるたびに新しい組み合わせが生まれうるので、変化が止まるまで繰り返す。
        // 項は上限で抑えてあるため、この繰り返しは必ず終わる。
        while (TryCombineComplementaryTerms(current, out List<ImmutableArray<SymbolTerm>> combined))
        {
            if (combined.Any(t => t.IsEmpty)) { return Always; }

            current = combined;
        }

        List<ImmutableArray<SymbolTerm>> sorted = [.. current];
        sorted.Sort(CompareTerms);

        List<ImmutableArray<SymbolTerm>> kept = [];

        foreach (ImmutableArray<SymbolTerm> term in sorted)
        {
            // 既にある、より短い項に含まれるなら、この項は何も足さない。
            if (kept.Any(k => IsSubsetOf(k, term)))
            {
                continue;
            }

            kept.Add(term);

            if (kept.Count > MaxTerms)
            {
                return Unknown;
            }
        }

        return new([.. kept]);
    }

    /// <summary>
    /// 1 つのシンボルの真偽だけが違う項を見つけて、そのシンボルを落とした項にまとめる。
    /// </summary>
    /// <param name="terms">対象の項。</param>
    /// <param name="combined">まとめた結果。まとめられなかった場合は空。</param>
    /// <returns>1 組でもまとめられれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <c>(A ∧ B) ∨ (¬A ∧ B)</c> では、A が真でも偽でも B なら成り立つ。つまり <c>B</c> である。
    /// 同じ形が両方の構成で見つかったときに、この規則が条件を消す。
    /// </remarks>
    private static bool TryCombineComplementaryTerms(
        List<ImmutableArray<SymbolTerm>> terms,
        out List<ImmutableArray<SymbolTerm>> combined)
    {
        for (int i = 0; i < terms.Count; i++)
        {
            for (int j = i + 1; j < terms.Count; j++)
            {
                if (!TryDropOpposedLiteral(terms[i], terms[j], out ImmutableArray<SymbolTerm> reduced))
                {
                    continue;
                }

                combined = [reduced];

                for (int k = 0; k < terms.Count; k++)
                {
                    if (k != i && k != j)
                    {
                        combined.Add(terms[k]);
                    }
                }

                return true;
            }
        }

        combined = [];
        return false;
    }

    /// <summary>
    /// 2 つの項が 1 つのシンボルの真偽だけで違うなら、そのシンボルを落とした項を返す。
    /// </summary>
    /// <param name="left">片方の項。</param>
    /// <param name="right">もう片方の項。</param>
    /// <param name="reduced">落とした結果の項。</param>
    /// <returns>まとめられれば <see langword="true"/>。</returns>
    private static bool TryDropOpposedLiteral(
        ImmutableArray<SymbolTerm> left,
        ImmutableArray<SymbolTerm> right,
        out ImmutableArray<SymbolTerm> reduced)
    {
        reduced = default;

        if (left.Length != right.Length)
        {
            return false;
        }

        // 名前順に並んでいるので、同じ位置に同じシンボルが来る。
        int opposed = -1;

        for (int i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i].Symbol, right[i].Symbol, StringComparison.Ordinal))
            {
                return false;
            }

            if (left[i].IsDefined == right[i].IsDefined)
            {
                continue;
            }

            if (opposed >= 0)
            {
                // 食い違いが 2 つ以上あるなら、まとめられない。
                return false;
            }

            opposed = i;
        }

        if (opposed < 0)
        {
            return false;
        }

        reduced = [.. left.Where((_, i) => i != opposed)];
        return true;
    }

    /// <summary>
    /// 項を一意な順序で比べる。
    /// </summary>
    /// <param name="left">左の項。</param>
    /// <param name="right">右の項。</param>
    /// <returns>比較結果。</returns>
    /// <remarks>
    /// 短い項を先に置く。吸収は短い項が長い項を飲み込む向きにしか働かないため、
    /// 短いものから見ていくと 1 度の走査で済む。
    /// </remarks>
    private static int CompareTerms(ImmutableArray<SymbolTerm> left, ImmutableArray<SymbolTerm> right)
    {
        if (left.Length != right.Length)
        {
            return left.Length.CompareTo(right.Length);
        }

        for (int i = 0; i < left.Length; i++)
        {
            int bySymbol = string.CompareOrdinal(left[i].Symbol, right[i].Symbol);

            if (bySymbol != 0)
            {
                return bySymbol;
            }

            if (left[i].IsDefined != right[i].IsDefined)
            {
                return left[i].IsDefined ? 1 : -1;
            }
        }

        return 0;
    }

    /// <summary>片方の項がもう片方に含まれるかを判定する。</summary>
    /// <param name="smaller">含まれる側。</param>
    /// <param name="larger">含む側。</param>
    /// <returns>含まれていれば <see langword="true"/>。</returns>
    private static bool IsSubsetOf(
        ImmutableArray<SymbolTerm> smaller,
        ImmutableArray<SymbolTerm> larger)
        => smaller.Length <= larger.Length && smaller.All(larger.Contains);
}
