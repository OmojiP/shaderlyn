using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Semantics.Conditional;

/// <summary>
/// 既定の構成とシンボルバリアントを突き合わせて、出現条件の索引を組み立てる。
/// </summary>
/// <remarks>
/// <para>
/// <b>バリアントごとの突き合わせは、それぞれ独立した制約を与える。</b>
/// あるノードがバリアント <c>K1</c> に無ければ「<c>K1</c> が無効なときだけ存在する」、
/// バリアント <c>K2</c> にも無ければ「<c>K2</c> が無効なときだけ存在する」。
/// 両方を満たす必要があるので、条件は論理積で重ねる。
/// </para>
/// <para>
/// <b>組み立ては呼ばれたときだけ行う。</b>
/// 条件を見ないルールに費用を負わせないためである。
/// </para>
/// </remarks>
internal static class ConditionMapBuilder
{
    /// <summary>
    /// 出現条件の索引を組み立てる。
    /// </summary>
    /// <param name="programs">既定の構成で解析したコードブロック。</param>
    /// <param name="variants">シンボルを 1 つ有効にして解析したコードブロック。</param>
    /// <returns>組み立てた索引。</returns>
    public static ConditionMap Build(
        ImmutableArray<AnalyzedProgram> programs,
        ImmutableArray<AnalyzedProgram> variants)
    {

        Dictionary<ShaderSyntaxNode, SymbolCondition> conditions = [];
        Dictionary<ShaderSyntaxNode, List<ConditionalNode>> inserted = [];
        ImmutableArray<Location>.Builder unmerged = ImmutableArray.CreateBuilder<Location>();
        ImmutableArray<ConditionalRegion>.Builder regions =
            ImmutableArray.CreateBuilder<ConditionalRegion>();
        Dictionary<ShaderSyntaxToken, HoistedToken> hoisted = [];
        Dictionary<AnalyzedProgram, List<Location>> hoistedLocations = [];

        // バリアントが並べた範囲も集める。バリアントにしか無いノードも、その木で並べた分岐の中にあれば条件が付く
        // (LutBuilder3D.compute の TONEMAPPING_ACES_APPROX の木では、#ifdef HDR_COLORSPACE_CONVERSION を並べた中に呼び出しがある)。
        // 範囲の条件は、有効にしたキーワード以外のものなので、どの構成でも同じ意味を持つ。
        CollectEmittedRegions([.. programs, .. variants], regions, hoisted, hoistedLocations);

        foreach (AnalyzedProgram variant in variants)
        {
            if (variant.EnabledSymbols.IsDefaultOrEmpty)
            {
                continue;
            }

            AnalyzedProgram? baseline = FindBaseline(programs, variant);

            if (baseline is null)
            {
                continue;
            }

            // まとめた構成は、組み立てのときにキーワードごとの条件で突き合わせてある。
            ConditionalMergeResult merged = variant.PackedMerge ?? ConditionalMerge.Merge(
                baseline.Tree.Root, variant.Tree.Root, variant.EnabledSymbols, baseline.Text.FilePath);

            unmerged.AddRange(merged.UnmergedLocations);

            // 定義ごとに複製した文の位置のうち、突き合わせが複製を取り違えうるところ。
            List<Location> ambiguous = FindAmbiguousCopies(baseline, variant, hoistedLocations, hoisted);

            // 1 本の木として見せるための挿入先。親ごとにまとめる。
            foreach (NodeInsertion addition in merged.NodeInsertions)
            {
                if (IsInsideAny(addition.Node, ambiguous))
                {
                    continue;
                }

                if (!inserted.TryGetValue(addition.Parent, out List<ConditionalNode>? siblings))
                {
                    siblings = [];
                    inserted[addition.Parent] = siblings;
                }

                siblings.Add(new ConditionalNode(addition.Node, addition.Condition));
            }

            foreach (ConditionalNode conditional in merged.ConditionalNodes)
            {
                // 複製の条件は巻き上げが付けたものが正しい。取り違えうる位置では突き合わせの結果を使わない。
                if (IsInsideAny(conditional.Node, ambiguous))
                {
                    continue;
                }

                conditions[conditional.Node] =
                    conditions.TryGetValue(conditional.Node, out SymbolCondition existing)
                        ? existing.And(conditional.Condition)
                        : conditional.Condition;
            }
        }

        if (conditions.Count == 0 && unmerged.Count == 0 && regions.Count == 0 && hoisted.Count == 0)
        {
            return ConditionMap.Empty;
        }

        // 位置の順に並べる。読み手は書かれた順に読む。
        foreach (List<ConditionalNode> siblings in inserted.Values)
        {
            siblings.Sort((a, b) => a.Node.Span.Start.CompareTo(b.Node.Span.Start));
        }

        return new ConditionMap(
            conditions,
            unmerged.ToImmutable(),
            regions.ToImmutable(),
            inserted,
            CollectConstraints(programs),
            hoisted,
            CollectBlocks([.. programs, .. variants]));
    }

    /// <summary>木の根から、その木のコードブロックを引く表を作る。</summary>
    /// <param name="programs">既定の構成とバリアントの木。</param>
    /// <returns>根からコードブロックへの表。</returns>
    private static Dictionary<SyntaxNode, (int Start, string? Kernel)> CollectBlocks(ImmutableArray<AnalyzedProgram> programs)
    {
        Dictionary<SyntaxNode, (int Start, string? Kernel)> blocks = new(ReferenceEqualityComparer.Instance);

        foreach (AnalyzedProgram program in programs)
        {
            blocks[program.Tree.Root] = program.BlockKey;
        }

        return blocks;
    }

    /// <summary>
    /// 突き合わせが、定義ごとに複製した文を取り違えうる位置を求める。
    /// </summary>
    /// <param name="baseline">既定の構成の木。</param>
    /// <param name="variant">バリアントの木。</param>
    /// <param name="hoistedLocations">木ごとの、複製した文の位置。</param>
    /// <param name="hoisted">複製した文のトークン。</param>
    /// <returns>取り違えうる位置。</returns>
    /// <remarks>
    /// <para>
    /// <b>一方の木では複製してあり、もう一方の木では同じ位置に複製していない文がある位置だけである。</b>
    /// そこでは、1 つの文がどの複製と対応するかが位置からは決まらない。
    /// 取り違えると、ある構成に存在する宣言に「その構成には無い」条件が付く
    /// (<c>multi_compile _A _B</c> で、<c>_B</c> の木の <c>float4 d</c> が既定の木の <c>float3 d</c> と対応した)。
    /// </para>
    /// <para>
    /// 両方の木で複製してあれば、複製どうしが同じ順に並ぶので取り違えない。
    /// 片方の木にしか文が無ければ (<c>#ifdef _B</c> の中で使った場合など)、
    /// その文は片方の構成にしか無いという突き合わせの結果が正しい。捨てると <c>_B</c> の条件が消える。
    /// </para>
    /// </remarks>
    private static List<Location> FindAmbiguousCopies(
        AnalyzedProgram baseline,
        AnalyzedProgram variant,
        Dictionary<AnalyzedProgram, List<Location>> hoistedLocations,
        Dictionary<ShaderSyntaxToken, HoistedToken> hoisted)
    {
        List<Location> ambiguous = [];

        Collect(baseline, variant);
        Collect(variant, baseline);

        return ambiguous;

        void Collect(AnalyzedProgram copied, AnalyzedProgram other)
        {
            if (!hoistedLocations.TryGetValue(copied, out List<Location>? locations))
            {
                return;
            }

            foreach (Location location in locations)
            {
                if (HasUncopiedTokens(other.Tree.PreprocessResult.Tokens, location, hoisted))
                {
                    ambiguous.Add(location);
                }
            }
        }
    }

    /// <summary>その位置に、複製していないトークンがあるかを判定する。</summary>
    /// <param name="tokens">展開後のトークン列。</param>
    /// <param name="location">調べる位置。</param>
    /// <param name="hoisted">複製した文のトークン。</param>
    /// <returns>あれば <see langword="true"/>。</returns>
    private static bool HasUncopiedTokens(
        ImmutableArray<ShaderSyntaxToken> tokens,
        Location location,
        Dictionary<ShaderSyntaxToken, HoistedToken> hoisted)
    {
        foreach (ShaderSyntaxToken token in tokens)
        {
            if (ReferenceEquals(token.Source, location.Source)
                && location.Span.Start <= token.Span.Start
                && token.Span.End <= location.Span.End
                && !hoisted.ContainsKey(token))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>ノードが、どれかの範囲の中に収まっているかを判定する。</summary>
    /// <param name="node">対象のノード。</param>
    /// <param name="locations">比べる範囲。</param>
    /// <returns>収まっていれば <see langword="true"/>。</returns>
    private static bool IsInsideAny(ShaderSyntaxNode node, List<Location> locations)
    {
        if (locations.Count == 0 || node.Source is not { } source)
        {
            return false;
        }

        TextSpan span = node.Span;

        foreach (Location location in locations)
        {
            if (ReferenceEquals(source, location.Source)
                && location.Span.Start <= span.Start
                && span.End <= location.Span.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 宣言から分かる構成の制約を、すべてのブロックについて集める。
    /// </summary>
    /// <param name="programs">既定の構成で解析したコードブロック。</param>
    /// <returns>どのブロックでも成り立つ制約。</returns>
    /// <remarks>
    /// <para>
    /// <b>どのブロックでも成り立つ制約だけを持つ。</b>
    /// 索引はすべてのブロックのノードを 1 つにまとめて持つので、ブロックごとには判断できない。
    /// 1 つのブロックで同じ行に並べていても、別のブロックで独立に宣言していれば、そこでは同時に有効になりうる。
    /// 成り立たない制約を当てはめると、存在する宣言を「その構成には無い」と見て、
    /// 呼び出しに合う宣言が無いと誤って報告することになる。
    /// </para>
    /// <para>
    /// 取り込んだヘッダの宣言も含める (<see cref="AnalyzedProgram.Variants"/>)。
    /// </para>
    /// </remarks>
    private static SymbolConstraints CollectConstraints(ImmutableArray<AnalyzedProgram> programs)
    {
        List<(ImmutableHashSet<string> Declared, SymbolConstraints Constraints)> blocks = [];

        foreach (AnalyzedProgram program in programs)
        {
            blocks.Add((program.Variants.Symbols, program.Variants.Constraints));
        }

        // 同じ名前の別の値 (QUALITY==1 と QUALITY==2) は、どのブロックでも同時には成り立たない。
        // 値を宣言したブロックが違っても排他である (Unreal の頂点ファクトリの値は、同じファイルの単位ごとに違う)。
        // 入れないと、ある単位の木の構成 (SHADERLYN_VERTEX_FACTORY == 4) と、ほかの単位の分岐の条件 (== 10) を掛けた条件を、ありうると見ていた。
        List<(string First, string Second)> valuePairs =
        [
            .. blocks
                .SelectMany(block => block.Declared)
                .Distinct(StringComparer.Ordinal)
                .Select(symbol => VariantValues.TryParseAtom(symbol, out string? name, out _) ? (Name: name, Symbol: symbol) : default)
                .Where(atom => atom.Name is not null)
                .GroupBy(atom => atom.Name, StringComparer.Ordinal)
                .SelectMany(byName =>
                {
                    string[] atoms = [.. byName.Select(atom => atom.Symbol).Order(StringComparer.Ordinal)];
                    return atoms.SelectMany((first, i) => atoms.Skip(i + 1).Select(second => (first, second)));
                }),
        ];

        if (valuePairs.Count == 0 && blocks.All(block => block.Constraints.IsEmpty))
        {
            return SymbolConstraints.Empty;
        }

        // 同時には定義されない 2 つ組は、両方を宣言しているどのブロックでも排他であるものだけ。
        IEnumerable<(string First, string Second)> pairs = blocks
            .SelectMany(block => block.Constraints.ExclusivePairs)
            .Distinct()
            .Where(pair => blocks.All(
                block => !block.Declared.Contains(pair.First)
                         || !block.Declared.Contains(pair.Second)
                         || block.Constraints.AreExclusive(pair.First, pair.Second)))
            .Concat(valuePairs)
            .Distinct();

        // どれか 1 つが必ず定義される集まりは、その中のどれかを宣言しているどのブロックでも、その集まりに含まれる集まりがあるものだけ。
        // 含まれる集まりのどれか 1 つが定義されるなら、その集まりのどれか 1 つも定義される。
        // 値を持つシンボルは、ブロック (単位) ごとに値が違うことがある (QUALITY が 1 から 4 の単位と 1 から 3 の単位)。
        // 同じ集まりに限ると、どちらの集まりも残らず、「どの値でもない」構成があることになって、
        // その構成で宣言が無いと報告していた (Unreal の SSRTReflections.usf で HL0310)。値を合わせた集まりも候補にする。
        IEnumerable<ImmutableArray<string>> candidates = blocks
            .SelectMany(block => block.Constraints.RequiredGroups)
            .Concat(blocks
                .SelectMany(block => block.Constraints.RequiredGroups)
                .Where(group => group.All(symbol => VariantValues.TryParseAtom(symbol, out _, out _)))
                .GroupBy(group => VariantValues.TryParseAtom(group[0], out string? name, out _) ? name : string.Empty, StringComparer.Ordinal)
                .Where(byName => byName.Skip(1).Any())
                .Select(byName => byName.SelectMany(group => group).Distinct(StringComparer.Ordinal).ToImmutableArray()));

        List<ImmutableArray<string>> groups = [];

        // ブロックごとの、値を宣言した名前。
        ImmutableHashSet<string>[] valueNames =
        [
            .. blocks.Select(block => block.Declared
                .Select(symbol => VariantValues.TryParseAtom(symbol, out string? name, out _) ? name : null)
                .OfType<string>()
                .ToImmutableHashSet(StringComparer.Ordinal)),
        ];

        foreach (ImmutableArray<string> group in candidates)
        {
            // 値の原子の集まりは、同じ名前のほかの値を宣言したブロックでも成り立たなければならない。
            // 4 だけの単位の集まり (QUALITY==4) を、1 と 2 の単位を見ずに残すと、どこでも QUALITY==4 が成り立つことになり、
            // 同じ名前の値の排他と合わせて、QUALITY==1 の構成がありえないことになる。
            ImmutableHashSet<string> names = [.. group.Select(symbol => VariantValues.TryParseAtom(symbol, out string? name, out _) ? name : null).OfType<string>()];

            if (groups.Any(g => g.Length == group.Length && g.All(group.Contains))
                || !blocks.Select((block, i) => (block, i)).All(
                    b => !(group.Any(b.block.Declared.Contains) || (!names.IsEmpty && names.Overlaps(valueNames[b.i])))
                         || b.block.Constraints.RequiredGroups.Any(g => g.All(group.Contains))))
            {
                continue;
            }

            groups.Add(group);
        }

        return new SymbolConstraints(pairs, groups.Select(g => (IReadOnlyList<string>)g));
    }

    /// <summary>
    /// 展開が両方の分岐を並べた範囲を集める。
    /// </summary>
    /// <param name="programs">対象のコードブロック。</param>
    /// <param name="regions">集めた範囲を書き出す先。</param>
    /// <param name="hoisted">定義ごとに複製した文のトークンと、その条件を書き出す先。</param>
    /// <param name="hoistedLocations">定義ごとに複製した文の、ソース上の範囲を木ごとに書き出す先。</param>
    /// <remarks>
    /// <para>
    /// <b>これを読まないと、並べた分岐のノードが無条件に見える。</b>
    /// 展開が両方の分岐を 1 つの木に入れた場合、
    /// バリアントは作られないので、バリアントとの突き合わせからは条件が出てこない。
    /// 条件を持っているのは展開の記録だけである。
    /// </para>
    /// <para>
    /// 記録はトークンの位置で持っているので、ソース上の範囲に直す。
    /// ノードとの対応は範囲の重なりで取る。
    /// </para>
    /// </remarks>
    private static void CollectEmittedRegions(
        ImmutableArray<AnalyzedProgram> programs,
        ImmutableArray<ConditionalRegion>.Builder regions,
        Dictionary<ShaderSyntaxToken, HoistedToken> hoisted,
        Dictionary<AnalyzedProgram, List<Location>> hoistedLocations)
    {
        // 同じソース範囲を占める領域は、掛け合わせずに足し合わせる。
        // 既定の木とバリアントの木は、同じ領域を同じ条件で並べている。
        Dictionary<((int Start, string? Kernel) Block, SourceText Source, TextSpan Span), SymbolCondition> byLocation = [];
        List<((int Start, string? Kernel) Block, SourceText Source, TextSpan Span)> order = [];

        foreach (AnalyzedProgram program in programs)
        {
            PreprocessResult result = program.Tree.PreprocessResult;

            // バリアントの木の条件は、その木を解析した構成の中での話である。その構成を掛けてから足す。
            // 掛けないと、F == 1 の木で外側の #if V だけが付いた宣言と、既定の木で内側の #else (F != 0) も付いた宣言が
            // 同じ範囲になり、足すと内側の条件が消える。値で書き分けた宣言どうしを二重の宣言と報告していた
            // (Unreal の HairStrandsVelocity.usf の OutVelocityTexture)。
            SymbolCondition analyzed = program.AnalyzedCondition;

            foreach (ConditionalTokenRange range in result.ConditionalRegions)
            {
                // 定義ごとに複製した文 (条件の巻き上げ) は、どれも同じ位置から作られる。
                // 位置に直すと _A の複製と !_A の複製が重なり、どちらも「常に」になってしまう。
                // 複製ごとに別のトークンを持っているので、トークンで引く。
                if (range.IsHoisted)
                {
                    AddHoistedRange(result.Tokens, range, hoisted);

                    foreach (Location copied in GetRegionLocations(result.Tokens, range))
                    {
                        if (!hoistedLocations.TryGetValue(program, out List<Location>? locations))
                        {
                            locations = [];
                            hoistedLocations[program] = locations;
                        }

                        locations.Add(copied);
                    }

                    continue;
                }

                foreach (Location location in GetRegionLocations(result.Tokens, range))
                {
                    ((int Start, string? Kernel) Block, SourceText Source, TextSpan Span) key = (program.BlockKey, location.Source, location.Span);

                    SymbolCondition present = range.Condition.And(analyzed);

                    if (byLocation.TryGetValue(key, out SymbolCondition existing))
                    {
                        byLocation[key] = existing.Or(present);
                        continue;
                    }

                    byLocation[key] = present;
                    order.Add(key);
                }
            }
        }

        foreach (((int Start, string? Kernel) Block, SourceText Source, TextSpan Span) key in order)
        {
            regions.Add(new ConditionalRegion(Location.Create(key.Source, key.Span), byLocation[key], key.Block));
        }
    }

    /// <summary>
    /// 定義ごとに複製した文のトークンに、その複製の条件を付ける。
    /// </summary>
    /// <param name="tokens">展開後のトークン列。</param>
    /// <param name="range">複製 1 つ分の範囲。</param>
    /// <param name="hoisted">書き出す先。</param>
    /// <remarks>
    /// 複製は先頭のトークンで見分ける。複製ごとに別のインスタンスである
    /// (<see cref="ConditionalTokenRange.IsHoisted"/>)。
    /// 取り込みの結果を使い回すと、同じトークンが別のブロックの木にも現れる。条件は同じなので、先に覚えたほうを残す。
    /// </remarks>
    private static void AddHoistedRange(
        ImmutableArray<ShaderSyntaxToken> tokens,
        ConditionalTokenRange range,
        Dictionary<ShaderSyntaxToken, HoistedToken> hoisted)
    {
        if (range.Start < 0 || range.Start >= tokens.Length || range.Length <= 0)
        {
            return;
        }

        HoistedToken entry = new(tokens[range.Start], range.Condition);

        for (int i = range.Start; i < range.Start + range.Length && i < tokens.Length; i++)
        {
            hoisted.TryAdd(tokens[i], entry);
        }
    }

    /// <summary>
    /// トークンの範囲を、ソース上の範囲に直す。
    /// </summary>
    /// <param name="tokens">展開後のトークン列。</param>
    /// <param name="range">対象の範囲。</param>
    /// <returns>出どころの続く区切りごとの位置。</returns>
    /// <remarks>
    /// <para>
    /// <b>範囲の中で出どころが変わるのは、取り込みをまたぐときである。</b>
    /// マクロ展開で生まれたトークンの出どころは呼び出し位置に揃えてあるので、マクロでは変わらない。
    /// <code>
    /// #if DEBUG
    /// #include "Debug.ush"   // 指令の無いヘッダなら、ヘッダの中身とその後ろが 1 つの範囲になる
    /// void WriteDebug() { FDebugInfo o; o.Color = 1; }
    /// #endif
    /// </code>
    /// 以前は先頭のトークンと同じ出どころのものだけで範囲を作っていた。
    /// ヘッダより後ろの文が条件を持たず、ヘッダの構造体のメンバーを「DEBUG でないときは無い」(HL0313) と報告していた
    /// (Unreal の <c>HairStrandsEnvironmentLighting.usf</c>)。
    /// </para>
    /// </remarks>
    private static IEnumerable<Location> GetRegionLocations(
        ImmutableArray<ShaderSyntaxToken> tokens,
        ConditionalTokenRange range)
    {
        if (range.Start < 0 || range.Start >= tokens.Length || range.Length <= 0)
        {
            yield break;
        }

        int last = Math.Min(range.Start + range.Length, tokens.Length);
        ShaderSyntaxToken first = tokens[range.Start];
        int end = first.Span.End;

        for (int i = range.Start + 1; i < last; i++)
        {
            if (!ReferenceEquals(tokens[i].Source, first.Source))
            {
                yield return Location.Create(first.Source, TextSpan.FromBounds(first.Span.Start, end));

                first = tokens[i];
                end = first.Span.End;
                continue;
            }

            end = Math.Max(end, tokens[i].Span.End);
        }

        yield return Location.Create(first.Source, TextSpan.FromBounds(first.Span.Start, end));
    }

    /// <summary>バリアントに対応する既定の構成を探す。</summary>
    /// <param name="programs">既定の構成で解析したコードブロック。</param>
    /// <param name="variant">対象のバリアント。</param>
    /// <returns>見つかったブロック。無い場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 同じ <c>*PROGRAM</c> ブロックから作られたものどうしを対応させる。
    /// </remarks>
    private static AnalyzedProgram? FindBaseline(
        ImmutableArray<AnalyzedProgram> programs,
        AnalyzedProgram variant)
    {
        foreach (AnalyzedProgram program in programs)
        {
            if (program.CodeSpan == variant.CodeSpan
                && string.Equals(program.KernelName, variant.KernelName, StringComparison.Ordinal))
            {
                return program;
            }
        }

        return null;
    }
}
