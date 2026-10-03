using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Semantics.Conditional;

/// <summary>
/// 条件付きで残された範囲 1 つ分。
/// </summary>
/// <param name="Location">その範囲。</param>
/// <param name="Condition">その範囲が存在する条件。</param>
/// <remarks>
/// 展開が両方の分岐を 1 つの木に並べた場合、条件はノードではなく範囲で分かる。
/// </remarks>
internal readonly record struct ConditionalRegion(Location Location, SymbolCondition Condition);

/// <summary>
/// 定義ごとに複製した文 (条件の巻き上げ) のトークン 1 つ分の条件。
/// </summary>
/// <param name="Copy">その複製の先頭のトークン。どの複製のものかを見分ける。</param>
/// <param name="Condition">その複製が存在する条件。</param>
internal readonly record struct HoistedToken(HlslSyntaxToken Copy, SymbolCondition Condition);

/// <summary>
/// 構文木のノードが、どのシンボルの組み合わせのもとで存在するか。
/// </summary>
/// <remarks>
/// <para>
/// <b>ルールはこれを見て「その指摘がどの構成の話か」を判断する。</b>
/// 構成によって成り立ったり成り立たなかったりする指摘をそのまま出すと、
/// 利用者は自分の構成では再現しない指摘を追うことになる。
/// </para>
/// <para>
/// <b>条件を持たないノードは無条件である。</b>
/// 木のほとんどがそれなので、表には条件が付くものだけを入れる。
/// </para>
/// </remarks>
public sealed class ConditionMap
{
    /// <summary>すべてのノードが無条件で、突き合わせも済んでいる索引。</summary>
    /// <remarks>シンボルバリアントが無い場合に使う。条件を引くと必ず「常に」を返す。</remarks>
    public static ConditionMap Empty { get; } = new([], [], [], [], SymbolConstraints.Empty, []);

    private readonly Dictionary<HlslSyntaxNode, SymbolCondition> _conditions;
    private readonly ImmutableArray<ConditionalRegion> _regions;
    private readonly Dictionary<HlslSyntaxNode, List<ConditionalNode>> _inserted;
    private readonly SymbolConstraints _constraints;
    private readonly Dictionary<HlslSyntaxToken, HoistedToken> _hoisted;

    internal ConditionMap(
        Dictionary<HlslSyntaxNode, SymbolCondition> conditions,
        ImmutableArray<Location> unmergedLocations,
        ImmutableArray<ConditionalRegion> regions,
        Dictionary<HlslSyntaxNode, List<ConditionalNode>> inserted,
        SymbolConstraints constraints,
        Dictionary<HlslSyntaxToken, HoistedToken> hoisted)
    {
        _constraints = constraints;
        _conditions = conditions;
        _regions = regions;
        _inserted = inserted;
        _hoisted = hoisted;
        UnmergedLocations = unmergedLocations;
    }

    /// <summary>
    /// 1 本の木として見せるときに、そのノードの子として足すノード。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <returns>足すノードとその条件。無ければ空。</returns>
    /// <remarks>
    /// <para>
    /// <b>既定の構成の木を歩くだけでは見えないノードがある。</b>
    /// <c>#ifdef</c> がマクロの定義を切り替えていると、
    /// 使う側の 1 行は既定の構成では空文に、バリアントでは呼び出し式になる。
    /// 呼び出し式はバリアントの木にしか無い。
    /// </para>
    /// <para>
    /// 既定の子と合わせて位置の順に並べれば、
    /// 両方の分岐が 1 本の木として読める。
    /// </para>
    /// </remarks>
    public IReadOnlyList<ConditionalNode> GetInsertedChildren(HlslSyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return _inserted.TryGetValue(node, out List<ConditionalNode>? found) ? found : [];
    }

    /// <summary>1 本の木として見せるときに足すノードが 1 つでもあるか。</summary>
    public bool HasInsertedChildren => _inserted.Count > 0;

    /// <summary>
    /// 突き合わせられなかった箇所。
    /// </summary>
    /// <remarks>
    /// <c>#if</c> が構文の単位をまたいでいると、対応が取れずここに残る。
    /// この箇所については条件が分かっていない。
    /// </remarks>
    public ImmutableArray<Location> UnmergedLocations { get; }

    /// <summary>すべて突き合わせられたかどうか。</summary>
    public bool IsComplete => UnmergedLocations.IsEmpty;

    /// <summary>
    /// ノードが存在する条件を返す。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <returns>存在する条件。条件が付いていなければ「常に」。</returns>
    /// <remarks>
    /// <para>
    /// <b>親を辿って集める。</b>
    /// 条件は、片方の構成にしか無い部分木の<b>根</b>にだけ記録してある。
    /// その中身はまるごと同じ条件のもとにあるので、
    /// 子孫にも書くと同じことを繰り返すだけになる。
    /// シンボルが <c>#include</c> を切り替えている場合、
    /// 1 つの部分木が 1 万を超えるノードを持つため、これは量の問題になる。
    /// </para>
    /// <para>
    /// 入れ子の条件は論理積で重なる。
    /// <c>#ifdef A</c> の中の <c>#ifdef B</c> は、両方が成り立つときだけ存在する。
    /// </para>
    /// <para>
    /// 同時には定義されないシンボルを両方求める項は落とす (<see cref="IsPossible"/>)。
    /// </para>
    /// </remarks>
    public SymbolCondition GetCondition(HlslSyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // 突き合わせられなかった箇所では、条件が分かっていない。
        // 「常に」を返すと、分からないものを分かったことにしてしまう。
        if (IsInUnmergedRegion(node))
        {
            return SymbolCondition.Unknown;
        }

        SymbolCondition condition = SymbolCondition.Always;

        // バリアントとの突き合わせから出た条件。親を辿って集める。
        if (_conditions.Count > 0)
        {
            for (SyntaxNode? current = node; current is not null; current = current.Parent)
            {
                if (current is HlslSyntaxNode hlsl
                    && _conditions.TryGetValue(hlsl, out SymbolCondition found))
                {
                    condition = condition.And(found);
                }
            }
        }

        // 展開が両方の分岐を並べた範囲から出た条件。
        // バリアントが作られていないので、こちらにしか条件が無い。
        foreach (ConditionalRegion region in _regions)
        {
            if (IsInside(node, region.Location))
            {
                condition = condition.And(region.Condition);
            }
        }

        // 定義ごとに複製した文から出た条件。複製はどれも同じ位置にあるので、トークンで引く。
        if (GetHoistedCondition(node) is { } hoisted)
        {
            condition = condition.And(hoisted);
        }

        return _constraints.Apply(condition);
    }

    /// <summary>
    /// ノードが定義ごとに複製した文の中にあれば、その複製の条件を返す。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <returns>複製の条件。複製の中に無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>先頭と末尾のトークンが同じ複製のものであるときだけ、その中にある。</b>
    /// 複製を含むブロックは、先頭も末尾も複製の外にある。
    /// 末尾を求めるには子孫を辿るので、先頭が複製のものだったときだけ求める。
    /// </remarks>
    private SymbolCondition? GetHoistedCondition(HlslSyntaxNode node)
    {
        if (_hoisted.Count == 0
            || node.FirstToken is not { } first
            || !_hoisted.TryGetValue(first, out HoistedToken entry))
        {
            return null;
        }

        HlslSyntaxToken? last = node.DescendantTokens().LastOrDefault();

        return last is not null
               && _hoisted.TryGetValue(last, out HoistedToken end)
               && ReferenceEquals(entry.Copy, end.Copy)
            ? entry.Condition
            : null;
    }

    /// <summary>
    /// その条件が成り立つ構成があるかを判定する。
    /// </summary>
    /// <param name="condition">対象の条件。</param>
    /// <returns>あれば <see langword="true"/>。<see cref="SymbolCondition.Unknown"/> もあるものとする。</returns>
    /// <remarks>
    /// <para>
    /// <b>条件を掛け合わせて「成り立つか」を見るときは、<c>IsNever</c> ではなくこちらを使う。</b>
    /// 条件の代数は、同じ <c>#pragma</c> 行のシンボルが同時に有効にならないことを知らない。
    /// <c>#pragma multi_compile _ _X _Y</c> の <c>_X</c> にある宣言と <c>_Y</c> にある宣言は、
    /// 掛け合わせても <c>IsNever</c> にならないが、同時には存在しない。
    /// </para>
    /// <para>
    /// 排他とするのは、そのシンボルを宣言しているどのブロックでも同じ行に並んでいる組だけである
    /// (<see cref="ConditionMapBuilder"/>)。
    /// </para>
    /// </remarks>
    public bool IsPossible(SymbolCondition condition) => _constraints.IsPossible(condition);

    /// <summary>決して成り立たない項を落とした条件を返す。</summary>
    /// <param name="condition">対象の条件。</param>
    /// <returns>落とした条件。</returns>
    /// <remarks>
    /// 利用者に条件を示すときに使う。<c>!_X || !_Y</c> のうち実在しない構成の項を示しても、読み手を迷わせるだけである。
    /// </remarks>
    public SymbolCondition Simplify(SymbolCondition condition) => _constraints.Apply(condition);

    /// <summary>ノードが範囲の中に収まっているかを判定する。</summary>
    /// <param name="node">対象のノード。</param>
    /// <param name="location">比べる範囲。</param>
    /// <returns>収まっていれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>重なりで判定してはならない。</b>
    /// 関数の途中に <c>#ifdef</c> があると、関数の宣言そのものが範囲と重なる。
    /// 重なりを条件と見なすと、条件付きなのは中の数行だけなのに
    /// 関数全体が「そのシンボルのときだけ存在する」ことになってしまう。
    /// </remarks>
    private static bool IsInside(HlslSyntaxNode node, Location location)
    {
        if (node.Source is not { } source || !ReferenceEquals(source, location.Source))
        {
            return false;
        }

        TextSpan span = node.Span;

        return location.Span.Start <= span.Start && span.End <= location.Span.End;
    }

    /// <summary>
    /// ノードがどの構成でも存在するかどうかを判定する。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <returns>どの構成でも存在すれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>ルールが既定で使うのはこちらである。</b>
    /// どの構成でも成り立つことだけを指摘すれば、
    /// 利用者は「自分の構成では起きない指摘」を追わずに済む。
    /// </remarks>
    public bool IsAlwaysPresent(HlslSyntaxNode node) => GetCondition(node).IsAlways;

    /// <summary>
    /// ノードが、突き合わせられなかった範囲に入っているかを判定する。
    /// </summary>
    /// <param name="node">対象のノード。</param>
    /// <returns>入っていれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>ここで、何も伝えずに「常に」を返してはならない。</b>
    /// 条件を追えなかった箇所を無条件と見なすと、
    /// 「どの構成でも成り立つ」を根拠にした指摘が、
    /// 実際には成り立たない構成について出ることになる。
    /// これは条件を導入する前より悪い。
    /// </para>
    /// <para>
    /// <see cref="SymbolCondition.Unknown"/> を返せば、
    /// <see cref="IsAlwaysPresent"/> が <see langword="false"/> になり、
    /// 保守的なルールは自動的に報告しない。
    /// ルール側が確認を忘れても壊れないようにするための作りである。
    /// </para>
    /// <para>
    /// 実測では 545 組のうち 2 組でしか起きない。
    /// 普段はこの並びが空なので、判定は費用にならない。
    /// </para>
    /// </remarks>
    private bool IsInUnmergedRegion(HlslSyntaxNode node)
    {
        if (UnmergedLocations.IsEmpty || node.Source is not { } source)
        {
            return false;
        }

        TextSpan span = node.Span;

        foreach (Location unmerged in UnmergedLocations)
        {
            if (ReferenceEquals(unmerged.Source, source)
                && span.Start < unmerged.Span.End
                && unmerged.Span.Start < span.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>条件が付いているノードを列挙する。</summary>
    /// <returns>ノードとその条件。</returns>
    /// <remarks>「このシンボルでだけ存在するもの」を一覧するのに使う。</remarks>
    public IEnumerable<ConditionalNode> EnumerateConditional()
        => _conditions.Select(p => new ConditionalNode(p.Key, p.Value));
}
