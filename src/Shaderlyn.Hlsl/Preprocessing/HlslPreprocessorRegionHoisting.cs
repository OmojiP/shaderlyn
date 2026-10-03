using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// 文の途中で分かれる <c>#if</c> を、分岐ごとに文を複製して並べる (条件の巻き上げ)。
/// </summary>
/// <remarks>
/// <para>
/// <b>両方の分岐をそのまま並べると文にならない領域がある。</b>
/// <c>float m</c> / <c>#ifdef _A</c> / <c>= 1</c> / <c>#else</c> / <c>= 2</c> / <c>#endif</c> / <c>;</c> を並べると
/// <c>float m = 1 = 2 ;</c> になる。これまでは構成ごとに展開し直していた (方法 B)。
/// </para>
/// <para>
/// 条件で中身が変わるマクロの複製 (<see cref="TryHoistConditionalMacro"/>) と同じく、
/// 条件をその文の外へ巻き上げ、分岐ごとに文を複製する。
/// <c>float m = 1 ;</c> (<c>_A</c>) と <c>float m = 2 ;</c> (<c>!_A</c>) が並び、方法 A と同じ形になる。
/// 構成ごとの展開は要らず、別々の <c>#if</c> の組み合わせも、型を求めるときに構成を仮定して調べられる。
/// </para>
/// <para>
/// <b><c>{ }</c> のブロックを含む単位は複製しない。</b>
/// 関数の本体や <c>if</c> の本体まで複製すると、どの構成にもあるコードまで条件付きになり、
/// 同じ誤りが構成ごとに報告される。仮引数を条件で足す形や <c>if</c> の頭だけを分ける形がこれにあたる。
/// 初期化の波括弧 (<c>= {</c>) は単位の中で閉じるので複製する。
/// </para>
/// </remarks>
internal sealed partial class HlslPreprocessor
{
    /// <summary>
    /// 文の途中で分かれる領域なら、分岐ごとに文を複製する。
    /// </summary>
    /// <param name="directive">領域を始めた指令名のトークン (<c>if</c> / <c>ifdef</c> / <c>ifndef</c>)。</param>
    /// <param name="first">最初の分岐に通る条件。シンボルの条件として読めなければ <see langword="null"/>。</param>
    /// <returns>複製した場合は <see langword="true"/>。領域は <c>#endif</c> の後ろの文の終わりまで読み終えている。</returns>
    /// <remarks>
    /// 複製できない場合は何も変えずに <see langword="false"/> を返す。
    /// 呼び出し側は今までどおり、並べるか、並べられなかったと記録する。
    /// </remarks>
    private bool TryHoistRegion(HlslSyntaxToken directive, SymbolCondition? first)
    {
        if (!IsHoistingEnabled
            || _hoisting
            || IsInKeptBranch
            || IsSkipping
            || first is not { } written
            || !CurrentSource.IsFile
            || _unitAnchor is not { } anchor
            || !ReferenceEquals(anchor.Source, CurrentSource)
            || _conditionals.Any(state => state.KeptCondition is null && state.IsConfigurationDependent)
            || (_options.MergeOnlyRegions is { } allowed
                && !allowed.Contains(new MergedRegion(directive.Source.FilePath, directive.Span.Start))))
        {
            return false;
        }

        TokenSource source = CurrentSource;
        ImmutableArray<HlslSyntaxToken> tokens = source.Tokens;
        int regionStart = source.Index;

        if (ConditionalRegionScanner.Scan(tokens, regionStart, out int regionEnd) != ConditionalRegionLayout.NotSelfContained
            || CallsConfigurationDependentMacro(regionStart, regionEnd)
            || ReadRegionBranches(tokens, regionStart, regionEnd, FixUnmergedSymbols(written)) is not { } branches
            || FindDirectiveHash(tokens, directive, anchor.SourceIndex) is not { } hash)
        {
            return false;
        }

        ImmutableArray<HlslSyntaxToken> prefix = [.. tokens.Skip(anchor.SourceIndex).Take(hash - anchor.SourceIndex)];
        ImmutableArray<int> candidates = [.. EnumerateRegionUnitEnds(tokens, prefix, branches[0].Tokens, regionEnd)];

        int savedParen = _unitParenDepth;
        int savedBracket = _unitBracketDepth;
        int savedInitializer = _unitInitializerDepth;
        int regionCount = _conditionalRegions.Count;
        int diagnosticCount = _diagnostics.Count;
        bool attempted = false;

        foreach (int unitEnd in candidates)
        {
            ImmutableArray<HlslSyntaxToken> suffix = [.. tokens.Skip(regionEnd).Take(unitEnd - regionEnd)];

            // 後ろの候補ほど単位が長くなる。ブロックが入ったら、それより先の候補にも入っている。
            if (branches.Any(b => !HasOnlyInitializerBraces([.. prefix, .. b.Tokens, .. suffix])))
            {
                break;
            }

            attempted = true;
            _output.RemoveRange(anchor.OutputIndex, _output.Count - anchor.OutputIndex);

            if (TryExpandRegionBranches(prefix, branches, suffix))
            {
                source.Index = unitEnd;
                _hoistedUnits++;
                MoveUnitAnchorPastUnit(source);
                _conditionalRangeStart = _output.Count;

                // 並べたのと同じに数える。バリアントもこの領域を複製する (MergeOnlyRegions)。
                MergedRegion region = new(directive.Source.FilePath, directive.Span.Start);
                _mergedRegions.Add(region);
                Record(recording => recording.MergedRegions.Add(region));

                foreach ((SymbolCondition condition, _) in branches)
                {
                    AddMergedSymbols(condition);
                }

                return true;
            }

            _hoistRetries++;
            _output.RemoveRange(anchor.OutputIndex, _output.Count - anchor.OutputIndex);
            _conditionalRegions.RemoveRange(regionCount, _conditionalRegions.Count - regionCount);
            _diagnostics.RemoveRange(diagnosticCount, _diagnostics.Count - diagnosticCount);
        }

        if (!attempted)
        {
            return false;
        }

        // どの切れ目でも文にならなかった。出し直した手前の部分を戻し、今までどおり並べるかを決めさせる。
        _hoistGiveUps++;
        _output.RemoveRange(anchor.OutputIndex, _output.Count - anchor.OutputIndex);
        ExpandTokensAsUnit(prefix);
        source.Index = regionStart;
        _unitParenDepth = savedParen;
        _unitBracketDepth = savedBracket;
        _unitInitializerDepth = savedInitializer;
        _unitAnchor = anchor;

        return false;
    }

    /// <summary>
    /// 分岐ごとに、手前の部分・分岐の中身・後ろの部分をつないで展開する。
    /// </summary>
    /// <param name="prefix">文の始まりから <c>#if</c> の手前まで。</param>
    /// <param name="branches">分岐ごとの条件と中身。</param>
    /// <param name="suffix"><c>#endif</c> の次から文の終わりまで。</param>
    /// <returns>どの分岐も文になった場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// 複製ごとに別のトークンを使う (<see cref="HlslSyntaxToken.Duplicate"/>)。
    /// 中で条件で中身が変わるマクロを使っていても、ここでは複製しない (1 つの単位で複製するのは 1 回まで)。
    /// </remarks>
    private bool TryExpandRegionBranches(
        ImmutableArray<HlslSyntaxToken> prefix,
        List<(SymbolCondition Condition, ImmutableArray<HlslSyntaxToken> Tokens)> branches,
        ImmutableArray<HlslSyntaxToken> suffix)
    {
        bool complete = true;

        _hoisting = true;
        _hoistingMacro = null;

        foreach ((SymbolCondition condition, ImmutableArray<HlslSyntaxToken> body) in branches)
        {
            int start = _output.Count;
            ExpandTokensAsUnit([.. prefix.Concat(body).Concat(suffix).Select(t => t.Duplicate())]);

            if (_output.Count > start)
            {
                _conditionalRegions.Add(
                    new ConditionalTokenRange(start, _output.Count - start, condition) { IsHoisted = true });
            }

            complete &= IsSelfContained(start, _output.Count);
        }

        _hoisting = false;

        return complete;
    }

    /// <summary>
    /// 領域を分岐に分け、それぞれに通る条件を求める。
    /// </summary>
    /// <param name="tokens">トークン列。</param>
    /// <param name="start">最初の分岐の先頭。</param>
    /// <param name="end">領域の終わり (<c>#endif</c> の行の次)。</param>
    /// <param name="first">最初の分岐の条件。</param>
    /// <returns>分岐ごとの条件と中身。複製できない形なら <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <c>#elif</c> は「それまでの分岐がすべて外れ、自分の条件が成り立つとき」に通る。
    /// <c>#else</c> が無ければ、どの分岐も通らない構成のために中身の無い分岐を作る。
    /// 実在しない構成でしか通らない分岐は作らない。
    /// </para>
    /// <para>
    /// 分岐の中に <c>#elif</c> / <c>#else</c> / <c>#endif</c> 以外の指令があれば複製しない。
    /// 複製すると指令を何度も処理することになる。
    /// </para>
    /// </remarks>
    private List<(SymbolCondition Condition, ImmutableArray<HlslSyntaxToken> Tokens)>? ReadRegionBranches(
        ImmutableArray<HlslSyntaxToken> tokens,
        int start,
        int end,
        SymbolCondition first)
    {
        List<(SymbolCondition, ImmutableArray<HlslSyntaxToken>)> branches = [];
        SymbolCondition remaining = first.Negate();
        SymbolCondition current = first;
        int branchStart = start;

        for (int i = start; i < end; i++)
        {
            if (tokens[i].Kind != HlslSyntaxKind.HashToken || !tokens[i].IsAtLineStart)
            {
                continue;
            }

            if (i + 1 >= tokens.Length || tokens[i + 1].Kind != HlslSyntaxKind.IdentifierToken)
            {
                return null;
            }

            string name = tokens[i + 1].Text;
            int lineEnd = SkipToNextLine(tokens, i);

            if (name is not ("elif" or "else" or "endif"))
            {
                return null;
            }

            Add(current, tokens, branchStart, i);

            if (name == "endif")
            {
                if (!remaining.IsNever)
                {
                    Add(remaining, tokens, lineEnd, lineEnd);
                }

                break;
            }

            if (name == "else")
            {
                current = remaining;
                remaining = SymbolCondition.Never;
            }
            else
            {
                ImmutableArray<HlslSyntaxToken> line = [.. tokens.Skip(i + 2).Take(lineEnd - i - 2)];
                int recordedBefore = _conditionalIdentifiers.Count;

                if (TryReadDefinedCondition(line) is not { IsUnknown: false } read)
                {
                    return null;
                }

                RecordBareKeywords(line, recordedBefore);

                SymbolCondition condition = FixUnmergedSymbols(read);
                current = remaining.And(condition);
                remaining = remaining.And(condition.Negate());
            }

            branchStart = lineEnd;
            i = lineEnd - 1;
        }

        return branches.Count is >= 2 and <= MaxHoistedBranches ? branches : null;

        void Add(SymbolCondition condition, ImmutableArray<HlslSyntaxToken> source, int from, int to)
        {
            if (!condition.IsUnknown && _options.SymbolConstraints.IsPossible(condition))
            {
                branches.Add((condition, [.. source.Skip(from).Take(to - from)]));
            }
        }
    }

    /// <summary>指令名のトークンの手前にある <c>#</c> の位置を探す。</summary>
    /// <param name="tokens">トークン列。</param>
    /// <param name="directive">指令名のトークン。</param>
    /// <param name="from">探し始める位置 (文の始まり)。</param>
    /// <returns><c>#</c> の位置。見つからなければ <see langword="null"/>。</returns>
    private static int? FindDirectiveHash(ImmutableArray<HlslSyntaxToken> tokens, HlslSyntaxToken directive, int from)
    {
        for (int i = Math.Max(from, 1); i < tokens.Length; i++)
        {
            if (ReferenceEquals(tokens[i], directive))
            {
                return tokens[i - 1].Kind == HlslSyntaxKind.HashToken && i - 1 >= from ? i - 1 : null;
            }
        }

        return null;
    }

    /// <summary>
    /// 複製する単位の終わりになりうる位置を、近いものから並べる。
    /// </summary>
    /// <param name="tokens">トークン列。</param>
    /// <param name="prefix">文の始まりから <c>#if</c> の手前まで。</param>
    /// <param name="firstBranch">最初の分岐の中身。括弧の深さを数えるのに使う。</param>
    /// <param name="from">領域の終わり (<c>#endif</c> の行の次)。</param>
    /// <returns>単位の次の位置。</returns>
    /// <remarks>
    /// 手前の部分と最初の分岐で開いた括弧が閉じ、<c>;</c> か <c>}</c> が来たところが切れ目の候補になる。
    /// 本当に文になるかは、展開してから構文解析に決めさせる (<see cref="IsSelfContained"/>)。
    /// 指令があればそこで打ち切る。
    /// </remarks>
    private static IEnumerable<int> EnumerateRegionUnitEnds(
        ImmutableArray<HlslSyntaxToken> tokens,
        ImmutableArray<HlslSyntaxToken> prefix,
        ImmutableArray<HlslSyntaxToken> firstBranch,
        int from)
    {
        int depth = 0;

        foreach (HlslSyntaxToken token in prefix.Concat(firstBranch))
        {
            depth += DepthChange(token);
        }

        int found = 0;

        for (int i = from; i < tokens.Length && i - from < MaxHoistedUnitTokens; i++)
        {
            HlslSyntaxToken token = tokens[i];

            if (token.Kind == HlslSyntaxKind.HashToken && token.IsAtLineStart)
            {
                yield break;
            }

            depth += DepthChange(token);

            if (depth == 0 && token.Kind is HlslSyntaxKind.SemicolonToken or HlslSyntaxKind.CloseBraceToken)
            {
                yield return i + 1;

                if (++found >= MaxUnitEndCandidates)
                {
                    yield break;
                }
            }

            if (depth < 0)
            {
                yield break;
            }
        }

        static int DepthChange(HlslSyntaxToken token) => token.Kind switch
        {
            HlslSyntaxKind.OpenParenToken or HlslSyntaxKind.OpenBracketToken or HlslSyntaxKind.OpenBraceToken => 1,
            HlslSyntaxKind.CloseParenToken or HlslSyntaxKind.CloseBracketToken or HlslSyntaxKind.CloseBraceToken => -1,
            _ => 0,
        };
    }

    /// <summary>
    /// 波括弧が初期化のものだけかを判定する。
    /// </summary>
    /// <param name="tokens">調べるトークン。</param>
    /// <returns>ブロックを開く波括弧が無ければ <see langword="true"/>。</returns>
    /// <remarks>
    /// 初期化の波括弧は <c>=</c> / <c>,</c> / <c>{</c> の直後に来る (<c>float4 c = { 1, 2 };</c>)。
    /// それ以外は関数や <c>if</c> の本体、構造体などのブロックであり、複製しない。
    /// </remarks>
    private static bool HasOnlyInitializerBraces(ImmutableArray<HlslSyntaxToken> tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == HlslSyntaxKind.OpenBraceToken
                && (i == 0 || tokens[i - 1].Kind is not (HlslSyntaxKind.EqualsToken or HlslSyntaxKind.CommaToken or HlslSyntaxKind.OpenBraceToken)))
            {
                return false;
            }
        }

        return true;
    }
}
