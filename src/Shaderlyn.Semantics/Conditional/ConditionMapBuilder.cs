using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Hlsl.Preprocessing;

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

        Dictionary<HlslSyntaxNode, SymbolCondition> conditions = [];
        Dictionary<HlslSyntaxNode, List<ConditionalNode>> inserted = [];
        ImmutableArray<Location>.Builder unmerged = ImmutableArray.CreateBuilder<Location>();
        ImmutableArray<ConditionalRegion>.Builder regions =
            ImmutableArray.CreateBuilder<ConditionalRegion>();
        Dictionary<HlslSyntaxToken, HoistedToken> hoisted = [];
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
            hoisted);
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
        Dictionary<HlslSyntaxToken, HoistedToken> hoisted)
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
        ImmutableArray<HlslSyntaxToken> tokens,
        Location location,
        Dictionary<HlslSyntaxToken, HoistedToken> hoisted)
    {
        foreach (HlslSyntaxToken token in tokens)
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
    private static bool IsInsideAny(HlslSyntaxNode node, List<Location> locations)
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
    /// 取り込んだヘッダの宣言も含める (<see cref="PreprocessResult.Pragmas"/>)。
    /// </para>
    /// </remarks>
    private static SymbolConstraints CollectConstraints(ImmutableArray<AnalyzedProgram> programs)
    {
        List<(HashSet<string> Declared, SymbolConstraints Constraints)> blocks = [];

        foreach (AnalyzedProgram program in programs)
        {
            ImmutableArray<PragmaDirective> pragmas = program.Tree.PreprocessResult.Pragmas;
            blocks.Add((ShaderSymbols.CollectDeclared(pragmas), ShaderSymbols.CollectConstraints(pragmas)));
        }

        if (blocks.All(block => block.Constraints.IsEmpty))
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
                         || block.Constraints.AreExclusive(pair.First, pair.Second)));

        // どれか 1 つが必ず定義される集まりは、その中のどれかを宣言しているどのブロックでも、同じ集まりであるものだけ。
        List<ImmutableArray<string>> groups = [];

        foreach (ImmutableArray<string> group in blocks.SelectMany(block => block.Constraints.RequiredGroups))
        {
            if (groups.Any(g => g.SequenceEqual(group))
                || !blocks.All(block => !group.Any(block.Declared.Contains)
                                        || block.Constraints.RequiredGroups.Any(g => g.SequenceEqual(group))))
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
        Dictionary<HlslSyntaxToken, HoistedToken> hoisted,
        Dictionary<AnalyzedProgram, List<Location>> hoistedLocations)
    {
        // 同じソース範囲を占める領域は、掛け合わせずに足し合わせる。
        // 既定の木とバリアントの木は、同じ領域を同じ条件で並べている。
        Dictionary<(SourceText Source, TextSpan Span), SymbolCondition> byLocation = [];
        List<(SourceText Source, TextSpan Span)> order = [];

        foreach (AnalyzedProgram program in programs)
        {
            PreprocessResult result = program.Tree.PreprocessResult;

            foreach (ConditionalTokenRange range in result.ConditionalRegions)
            {
                // 定義ごとに複製した文 (条件の巻き上げ) は、どれも同じ位置から作られる。
                // 位置に直すと _A の複製と !_A の複製が重なり、どちらも「常に」になってしまう。
                // 複製ごとに別のトークンを持っているので、トークンで引く。
                if (range.IsHoisted)
                {
                    AddHoistedRange(result.Tokens, range, hoisted);

                    if (TryGetRegionLocation(result.Tokens, range, out Location copied))
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

                if (!TryGetRegionLocation(result.Tokens, range, out Location location))
                {
                    continue;
                }

                (SourceText Source, TextSpan Span) key = (location.Source, location.Span);

                if (byLocation.TryGetValue(key, out SymbolCondition existing))
                {
                    byLocation[key] = existing.Or(range.Condition);
                    continue;
                }

                byLocation[key] = range.Condition;
                order.Add(key);
            }
        }

        foreach ((SourceText Source, TextSpan Span) key in order)
        {
            regions.Add(new ConditionalRegion(Location.Create(key.Source, key.Span), byLocation[key]));
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
        ImmutableArray<HlslSyntaxToken> tokens,
        ConditionalTokenRange range,
        Dictionary<HlslSyntaxToken, HoistedToken> hoisted)
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
    /// <param name="location">求めた位置。</param>
    /// <returns>求められた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// マクロ展開で生まれたトークンは出どころが呼び出し位置に移っているため、
    /// 範囲の中で出どころが変わることがある。
    /// 先頭のトークンと同じ出どころのものだけで範囲を作る。
    /// </remarks>
    private static bool TryGetRegionLocation(
        ImmutableArray<HlslSyntaxToken> tokens,
        ConditionalTokenRange range,
        out Location location)
    {
        location = null!;

        if (range.Start < 0 || range.Start >= tokens.Length || range.Length <= 0)
        {
            return false;
        }

        HlslSyntaxToken first = tokens[range.Start];
        int end = first.Span.End;

        for (int i = range.Start + 1; i < range.Start + range.Length && i < tokens.Length; i++)
        {
            if (!ReferenceEquals(tokens[i].Source, first.Source))
            {
                break;
            }

            end = Math.Max(end, tokens[i].Span.End);
        }

        location = Location.Create(first.Source, TextSpan.FromBounds(first.Span.Start, end));
        return true;
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
