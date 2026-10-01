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

        // バリアントが並べた範囲も集める。バリアントにしか無いノードも、その木で並べた分岐の中にあれば条件が付く
        // (LutBuilder3D.compute の TONEMAPPING_ACES_APPROX の木では、#ifdef HDR_COLORSPACE_CONVERSION を並べた中に呼び出しがある)。
        // 範囲の条件は、有効にしたキーワード以外のものなので、どの構成でも同じ意味を持つ。
        CollectEmittedRegions([.. programs, .. variants], regions);

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

            // 1 本の木として見せるための挿入先。親ごとにまとめる。
            foreach (NodeInsertion addition in merged.NodeInsertions)
            {
                if (!inserted.TryGetValue(addition.Parent, out List<ConditionalNode>? siblings))
                {
                    siblings = [];
                    inserted[addition.Parent] = siblings;
                }

                siblings.Add(new ConditionalNode(addition.Node, addition.Condition));
            }

            foreach (ConditionalNode conditional in merged.ConditionalNodes)
            {
                conditions[conditional.Node] =
                    conditions.TryGetValue(conditional.Node, out SymbolCondition existing)
                        ? existing.And(conditional.Condition)
                        : conditional.Condition;
            }
        }

        if (conditions.Count == 0 && unmerged.Count == 0 && regions.Count == 0)
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
            CollectConstraints(programs));
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
        ImmutableArray<ConditionalRegion>.Builder regions)
    {
        // 同じソース範囲を占める領域は、掛け合わせずに足し合わせる。
        // 条件で中身が変わるマクロを使う文は定義ごとに複製されるが (条件の巻き上げ)、
        // 複製はどれも同じ位置から作られるため、位置では見分けられない。
        // 掛け合わせると「_A かつ !_A」になり、そこにある宣言がどの構成にも無いことになってしまう。
        // どの複製も「いつか通る」コードなので、和が正しい。
        Dictionary<(SourceText Source, TextSpan Span), SymbolCondition> byLocation = [];
        List<(SourceText Source, TextSpan Span)> order = [];

        foreach (AnalyzedProgram program in programs)
        {
            PreprocessResult result = program.Tree.PreprocessResult;

            foreach (ConditionalTokenRange range in result.ConditionalRegions)
            {
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
