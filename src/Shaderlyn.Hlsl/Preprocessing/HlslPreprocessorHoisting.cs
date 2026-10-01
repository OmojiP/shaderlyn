using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// 条件によって中身が変わるマクロを使っている文を、定義ごとに複製する (条件の巻き上げ)。
/// </summary>
/// <remarks>
/// <para>
/// <b>マクロ表は 1 つしかなく、展開も 1 回しか行わない。</b>
/// そのため <c>#ifdef _A</c> で <c>#define CTYPE float3</c>、<c>#else</c> で <c>float4</c> と定義したマクロは、
/// どちらか 1 つの構成の値でしか展開できない。
/// </para>
/// <para>
/// SuperC は、この状況を「その場に条件分岐が書かれていたのと同じ」と見なし、
/// 条件を使用箇所の外へ<b>巻き上げて</b>トークン列を分ける (Gazzillo &amp; Grimm, PLDI 2012)。
/// ここではそれを<b>文・宣言の単位</b>で行う。
/// 単位ごと複製すれば、並べた分岐と同じ形に収まり、構文解析もそのまま通る。
/// </para>
/// <code>
/// CTYPE color = Load(uv);
///
/// // 巻き上げた後 (それぞれに条件が付く)
/// float3 color = Load(uv);   // _A
/// float4 color = Load(uv);   // !_A
/// </code>
/// <para>
/// <b>単位を取り違えると構文木が壊れる。</b>
/// そのため、同じファイルのトークンだけで文の切れ目が見つかり、
/// その間に指令が 1 つも無い場合に限って行う。
/// </para>
/// </remarks>
internal sealed partial class HlslPreprocessor
{
    /// <summary>複製する定義の数の上限。</summary>
    /// <remarks>定義が多いマクロを複製すると、同じ文が何本も並ぶ。読み手にも解析にも重い。</remarks>
    private const int MaxHoistedBranches = 4;

    /// <summary>複製する文の長さの上限 (トークン数)。</summary>
    private const int MaxHoistedUnitTokens = 512;

    /// <summary>試す切れ目の数の上限。</summary>
    /// <remarks>
    /// 切れ目を外すたびに、分岐の数だけ展開し直す。
    /// 当たりやすい順に並べてあるので、何度も外すならその形は諦めたほうが安い。
    /// </remarks>
    private const int MaxUnitEndCandidates = 4;

    /// <summary>巻き上げのために展開し直している最中かどうか。</summary>
    private bool _hoisting;

    /// <summary>定義ごとに複製した回数。</summary>
    private int _hoistedUnits;

    /// <summary>文の切れ目を読み直した回数。</summary>
    private int _hoistRetries;

    /// <summary>どの切れ目でも文にならず、複製を諦めた回数。</summary>
    private int _hoistGiveUps;

    /// <summary>
    /// 巻き上げを行うかどうか。
    /// </summary>
    /// <remarks>
    /// <b>並べる相手がいなければ巻き上げない。</b>
    /// 複製した文はそれぞれ条件付きの領域になる。1 本の木に並べる経路でしか受け取れない形である。
    /// 構成を固定した解析 (<c>SemanticsOptions.FixedSymbolConfiguration</c>) や
    /// 構成ごとに展開する経路では、その構成の値で 1 通りに展開しなければならない。
    /// </remarks>
    private bool IsHoistingEnabled
        => _options.HoistConditionalMacros && !_options.BothBranchSymbols.IsEmpty;

    /// <summary>出力に出した丸括弧の深さ。文の切れ目を見分けるのに使う。</summary>
    private int _unitParenDepth;

    /// <summary>出力に出した角括弧の深さ。</summary>
    private int _unitBracketDepth;

    /// <summary>
    /// 文の始まりの目印。巻き上げるときに、この位置から展開し直す。
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> は「今の文がどこから始まったか分からない」ことを表す。
    /// 取り込みや指令をまたいだ場合がそれにあたる。
    /// </remarks>
    private (TokenSource Source, int SourceIndex, int OutputIndex)? _unitAnchor;

    /// <summary>文の切れ目を覚える。</summary>
    /// <param name="token">これから出力へ足すトークン。</param>
    /// <remarks>
    /// <c>;</c> と波括弧で区切る。次の文はこの位置から始まる。
    /// マクロ展開の途中や、条件を付けている途中では目印を置かない。
    /// </remarks>
    private void NoteUnitBoundary(HlslSyntaxToken token)
    {
        if (!IsHoistingEnabled)
        {
            return;
        }

        switch (token.Kind)
        {
            case HlslSyntaxKind.OpenParenToken: _unitParenDepth++; break;
            case HlslSyntaxKind.CloseParenToken: _unitParenDepth--; break;
            case HlslSyntaxKind.OpenBracketToken: _unitBracketDepth++; break;
            case HlslSyntaxKind.CloseBracketToken: _unitBracketDepth--; break;
            default: break;
        }

        // 括弧の中の ; は文の切れ目ではない。for (uint i = 0; i < N; i++) がその形である。
        if (_unitParenDepth != 0
            || _unitBracketDepth != 0
            || token.Kind is not (HlslSyntaxKind.SemicolonToken
                or HlslSyntaxKind.OpenBraceToken
                or HlslSyntaxKind.CloseBraceToken))
        {
            return;
        }

        TokenSource source = CurrentSource;

        // 目印はこの記号の「次」である。まだ出力へ足していないので、どちらも 1 つ進める。
        _unitAnchor = source.IsFile && !IsInKeptBranch
            ? (source, source.Index + 1, _output.Count + 1)
            : null;
    }

    /// <summary>
    /// 指令の行の後ろへ、文の始まりの目印を進める。
    /// </summary>
    /// <remarks>
    /// <b>その文のトークンをまだ 1 つも出していないときだけ進める。</b>
    /// 文の途中に指令があると、指令より後ろだけを複製することになり、複製した断片が文にならない。
    /// </remarks>
    private void MoveUnitAnchorPastDirective()
    {
        if (!IsHoistingEnabled)
        {
            return;
        }

        if (_unitAnchor is { } anchor && anchor.OutputIndex != _output.Count)
        {
            _unitAnchor = null;
            return;
        }

        TokenSource source = CurrentSource;

        _unitAnchor = source.IsFile && !IsInKeptBranch && !IsSkipping
            ? (source, source.Index, _output.Count)
            : null;
    }

    /// <summary>
    /// 条件によって中身が変わるマクロなら、その文を定義ごとに複製する。
    /// </summary>
    /// <returns>複製した場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// 複製できない形 (文の切れ目が見つからない、間に指令がある、定義が多すぎる) では何もしない。
    /// その場合は今までどおり 1 つの構成の値で展開する。
    /// </remarks>
    private bool TryHoistConditionalMacro()
    {
        if (!IsHoistingEnabled || _hoisting || IsInKeptBranch)
        {
            return false;
        }

        HlslSyntaxToken nameToken = PeekToken()!;

        if (GetHoistableDefinitions(nameToken.Text) is not { } branches
            || _unitAnchor is not { } anchor
            || !ReferenceEquals(anchor.Source, CurrentSource))
        {
            return false;
        }

        ImmutableArray<int> candidates = [.. EnumerateUnitEnds(anchor.Source, anchor.SourceIndex)];

        if (candidates.IsEmpty)
        {
            return false;
        }

        MacroDefinition? saved = _macros.GetValueOrDefault(nameToken.Text);
        int regionCount = _conditionalRegions.Count;
        int diagnosticCount = _diagnostics.Count;

        foreach (int unitEnd in candidates)
        {
            // ここまでに出した分は、分岐ごとに出し直す。
            _output.RemoveRange(anchor.OutputIndex, _output.Count - anchor.OutputIndex);
            anchor.Source.Index = unitEnd;

            if (TryExpandBranches(nameToken.Text, TakeUnit(anchor, unitEnd), branches, saved))
            {
                _hoistedUnits++;
                _unitAnchor = null;
                _conditionalRangeStart = _output.Count;
                return true;
            }

            _hoistRetries++;

            // どこかの分岐が文にならなかった。切れ目の見当が違う。
            // 記号の数え方で決めた切れ目は、マクロが括弧や ; を作ると当てにならない。
            // 取り消して、次の切れ目で読み直す。
            _output.RemoveRange(anchor.OutputIndex, _output.Count - anchor.OutputIndex);
            _conditionalRegions.RemoveRange(regionCount, _conditionalRegions.Count - regionCount);
            _diagnostics.RemoveRange(diagnosticCount, _diagnostics.Count - diagnosticCount);
        }

        // どの切れ目でも文にならなかった。並べるのを諦め、1 通りで展開する。
        _hoistGiveUps++;
        anchor.Source.Index = candidates[0];
        ExpandTokensAsUnit(TakeUnit(anchor, candidates[0]));

        _unitAnchor = null;
        _conditionalRangeStart = _output.Count;

        return true;
    }

    /// <summary>目印からその位置までのトークンを取り出す。</summary>
    /// <param name="anchor">文の始まりの目印。</param>
    /// <param name="end">終わりの位置 (この位置は含まない)。</param>
    /// <returns>取り出したトークン。</returns>
    private static ImmutableArray<HlslSyntaxToken> TakeUnit(
        (TokenSource Source, int SourceIndex, int OutputIndex) anchor,
        int end)
        => [.. anchor.Source.Tokens.Skip(anchor.SourceIndex).Take(end - anchor.SourceIndex)];

    /// <summary>
    /// 定義ごとに展開し、どれも文になったかを返す。
    /// </summary>
    /// <param name="name">マクロの名前。</param>
    /// <param name="unit">展開するトークン。</param>
    /// <param name="branches">複製する分岐。</param>
    /// <param name="saved">元の定義。展開し終えたら戻す。</param>
    /// <returns>どの分岐も文になった場合は <see langword="true"/>。</returns>
    private bool TryExpandBranches(
        string name,
        ImmutableArray<HlslSyntaxToken> unit,
        List<(SymbolCondition Condition, MacroDefinition? Definition)> branches,
        MacroDefinition? saved)
    {
        bool complete = true;

        _hoisting = true;

        foreach ((SymbolCondition condition, MacroDefinition? definition) in branches)
        {
            SetMacro(name, definition);

            int start = _output.Count;
            ExpandTokensAsUnit(unit);

            if (_output.Count > start)
            {
                _conditionalRegions.Add(new ConditionalTokenRange(start, _output.Count - start, condition));
            }

            complete &= IsSelfContained(start, _output.Count);
        }

        _hoisting = false;
        SetMacro(name, saved);

        return complete;
    }

    /// <summary>
    /// 出力のその範囲が、文・宣言として閉じているかを判定する。
    /// </summary>
    /// <param name="start">範囲の開始位置。</param>
    /// <param name="end">範囲の終了位置 (この位置は含まない)。</param>
    /// <returns>閉じていれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>複製してよかったかは、複製してみるまで分からない。</b>
    /// 展開前のトークンで文の切れ目を探しているので、マクロの本体が括弧を開いたまま終わっていると
    /// (<c>#define BEGIN struct Foo {</c> のような書き方)、複製した断片は文にならない。
    /// 崩れていれば巻き上げを取り消す。
    /// </para>
    /// <para>
    /// <b>判定は構文解析に任せる。</b>
    /// 括弧の数と末尾の記号では足りない。
    /// <c>float v = ;</c> は括弧が釣り合い末尾も <c>;</c> だが、文ではない
    /// (<see cref="HlslParser.IsCompleteUnits"/>)。
    /// </para>
    /// </remarks>
    private bool IsSelfContained(int start, int end)
        => HlslParser.IsCompleteUnits([.. _output.Skip(start).Take(end - start)]);

    /// <summary>
    /// その名前が、定義ごとに複製すべきマクロかを判定する。
    /// </summary>
    /// <param name="name">マクロの名前。</param>
    /// <returns>複製する分岐。複製しない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 条件付きで覚えた定義が 2 つ以上あり、中身が違い、条件が分かっている場合に複製する。
    /// 定義がどの構成も覆っていなければ、「どの定義も無い」分岐も作る。
    /// </para>
    /// <para>
    /// <b>関数形式マクロも複製する。</b>
    /// 引数は文の単位の中に入っているので、分岐ごとに展開し直せば、その定義の引数として読まれる。
    /// ただし、どの定義も関数形式で、次が <c>(</c> でなければ展開されない。
    /// そのときはどの分岐も同じトークン列になるので、複製しない。
    /// </para>
    /// </remarks>
    private List<(SymbolCondition Condition, MacroDefinition? Definition)>? GetHoistableDefinitions(string name)
    {
        if (!_conditionalMacros.TryGetValue(name, out List<ConditionalMacro>? recorded))
        {
            return null;
        }

        // 並べずに展開しているキーワードの値は決まっている。この構成では通らない定義は複製しない。
        // バリアントで _A を有効にしたなら、#ifdef _A ... #else の側の定義は無い。
        List<ConditionalMacro> definitions =
        [
            .. recorded
                .Select(d => d with { Condition = FixUnmergedSymbols(d.Condition) })
                .Where(d => !d.Condition.IsNever),
        ];

        if (definitions.Count < 2
            || definitions.Any(d => d.Condition.IsUnknown || d.Condition.IsAlways)
            || definitions.Select(d => d.Body).Distinct(StringComparer.Ordinal).Count() < 2)
        {
            return null;
        }

        // 関数形式マクロは、呼び出しの形になっていなければ展開されない。
        // 展開されないなら、どの分岐も同じトークン列になる。複製する意味が無い。
        if (definitions.All(d => d.Definition.IsFunctionLike) && !NextNonExhaustedTokenIsOpenParen())
        {
            return null;
        }

        List<(SymbolCondition, MacroDefinition?)> branches = [];
        SymbolCondition covered = SymbolCondition.Never;

        foreach (ConditionalMacro definition in definitions)
        {
            branches.Add((definition.Condition, definition.Definition));
            covered = covered.Or(definition.Condition);
        }

        // どの定義も無い構成があるなら、その分岐も作る。名前がそのまま残る。
        if (!covered.IsAlways && !covered.IsUnknown)
        {
            branches.Add((covered.Negate(), null));
        }

        return branches.Count is >= 2 and <= MaxHoistedBranches ? branches : null;
    }

    /// <summary>
    /// 文の終わりになりうる位置を、近いものから並べる。
    /// </summary>
    /// <param name="source">対象のトークンソース。</param>
    /// <param name="start">文の始まりの位置。</param>
    /// <returns>文の次の位置。見つからなければ空。</returns>
    /// <remarks>
    /// <para>
    /// <b>ここで決めるのは候補までである。</b>
    /// 展開前のトークンにはマクロ名しか無く、記号を数えても本当の切れ目は分からない。
    /// <c>#define BEGIN struct Foo \{</c> のように、マクロが括弧や <c>;</c> を作ることがあるためである。
    /// どれが本当の切れ目かは、展開してから構文解析に決めさせる
    /// (<see cref="TryHoistConditionalMacro"/>)。
    /// </para>
    /// <para>
    /// 括弧の釣り合いを見ているのは、当たりやすい順に並べるためである。
    /// 最初の候補が当たれば展開は 1 回で済む。
    /// </para>
    /// <para>
    /// 間に指令があれば、そこで打ち切る。複製すると指令まで 2 度処理することになる。
    /// </para>
    /// </remarks>
    private static IEnumerable<int> EnumerateUnitEnds(TokenSource source, int start)
    {
        int brace = 0;
        int paren = 0;
        int bracket = 0;
        int found = 0;

        for (int i = start; i < source.Tokens.Length && i - start < MaxHoistedUnitTokens; i++)
        {
            HlslSyntaxToken token = source.Tokens[i];

            if (token.Kind == HlslSyntaxKind.HashToken && token.IsAtLineStart)
            {
                yield break;
            }

            bool boundary = false;

            switch (token.Kind)
            {
                case HlslSyntaxKind.OpenBraceToken: brace++; break;
                case HlslSyntaxKind.OpenParenToken: paren++; break;
                case HlslSyntaxKind.OpenBracketToken: bracket++; break;
                case HlslSyntaxKind.CloseParenToken: paren--; break;
                case HlslSyntaxKind.CloseBracketToken: bracket--; break;

                case HlslSyntaxKind.CloseBraceToken:
                    brace--;
                    boundary = brace <= 0 && paren == 0 && bracket == 0;

                    // 開く側をマクロが作っていると、閉じる側だけが数に入る。
                    // 負のままにすると、その後ろの ; を切れ目と見なせなくなる。
                    brace = Math.Max(brace, 0);
                    break;

                case HlslSyntaxKind.SemicolonToken:
                    boundary = brace == 0 && paren == 0 && bracket == 0;
                    break;

                default:
                    break;
            }

            if (!boundary)
            {
                continue;
            }

            yield return i + 1;

            // 閉じ括弧が余っていても打ち切らない。
            // マクロが波括弧を開いていれば、数え方のほうがずれている
            // (#define BEGIN struct Foo { を使うと、閉じる側だけが数に入る)。
            if (++found >= MaxUnitEndCandidates)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// 1 つの単位のトークンを、今のマクロ表で展開して出力へ足す。
    /// </summary>
    /// <param name="unit">展開するトークン。</param>
    /// <remarks>
    /// 主ループと同じ処理を、積んだソースを読み終えるまで回す。
    /// 指令は含まれていない (<see cref="EnumerateUnitEnds"/> が確かめている)。
    /// </remarks>
    private void ExpandTokensAsUnit(ImmutableArray<HlslSyntaxToken> unit)
    {
        int depth = _sources.Count;
        PushExpansionSource(unit, "<巻き上げ>");

        while (_sources.Count > depth)
        {
            if (CurrentSource.IsExhausted)
            {
                PopSource();
                continue;
            }

            ProcessNextToken();
        }
    }
}
