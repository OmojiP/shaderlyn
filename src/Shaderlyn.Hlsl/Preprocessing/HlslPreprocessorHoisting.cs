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

    /// <summary>今、定義ごとに複製しているマクロの名前。</summary>
    private string? _hoistingMacro;

    /// <summary>
    /// 定義するマクロがコードで使われるために並べなかったシンボルと、その領域で定義したマクロ。
    /// </summary>
    /// <remarks>
    /// 使う箇所がすべて巻き上げられたなら、構成ごとに展開し直す必要は無い (<see cref="RestoreHoistedDeclines"/>)。
    /// </remarks>
    private readonly Dictionary<string, HashSet<string>> _hoistableDeclines = new(StringComparer.Ordinal);

    /// <summary><see cref="_hoistableDeclines"/> のどれかの領域で定義したマクロの名前。</summary>
    private readonly HashSet<string> _hoistableMacros = new(StringComparer.Ordinal);

    /// <summary>巻き上げ以外で展開された、<see cref="_hoistableMacros"/> のマクロ。</summary>
    private readonly HashSet<string> _unhoistedMacros = new(StringComparer.Ordinal);

    /// <summary>巻き上げでは補えない理由でも並べなかったシンボル。</summary>
    private readonly HashSet<string> _firmDeclines = new(StringComparer.Ordinal);

    /// <summary>巻き上げで補えるかもしれない領域の、開始の指令と、その条件のシンボル。</summary>
    private readonly Dictionary<HlslSyntaxToken, ImmutableArray<string>> _hoistableRegions = [];

    /// <summary>
    /// 巻き上げで補えるかもしれない領域で求めた、同時に有効にする組。補えなかった場合にだけ記録する。
    /// </summary>
    /// <remarks>
    /// <c>multi_compile _A _B</c> の <c>#ifdef _A</c> の <c>#else</c> は、<c>_B</c> を有効にした構成でしか通らない。
    /// 補えたなら、その構成を作ってもどの木にも新しいものは載らない。
    /// </remarks>
    private readonly List<(ImmutableArray<string> Symbols, ImmutableArray<string> Combination, HlslSyntaxToken Directive)>
        _deferredCombinations = [];

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

        if (_conditionalMacros.Count == 0
            || _unitAnchor is not { } anchor
            || !ReferenceEquals(anchor.Source, CurrentSource))
        {
            return false;
        }

        // 文に書かれた名前そのものが条件で中身の変わるマクロでなくても、
        // その本体を通して参照していれば、そのマクロの定義ごとに複製する (#define APPLY BODY の APPLY)。
        string? hoisted = GetHoistableDefinitions(nameToken.Text) is not null
            ? nameToken.Text
            : FindHoistableThroughMacros(nameToken.Text);

        if (hoisted is null
            || GetHoistableDefinitions(hoisted, checkCallForm: hoisted == nameToken.Text) is not { } branches)
        {
            return false;
        }

        ImmutableArray<int> candidates = [.. EnumerateUnitEnds(anchor.Source, anchor.SourceIndex)];

        if (candidates.IsEmpty)
        {
            return false;
        }

        MacroDefinition? saved = _macros.GetValueOrDefault(hoisted);
        int regionCount = _conditionalRegions.Count;
        int diagnosticCount = _diagnostics.Count;

        foreach (int unitEnd in candidates)
        {
            // ここまでに出した分は、分岐ごとに出し直す。
            _output.RemoveRange(anchor.OutputIndex, _output.Count - anchor.OutputIndex);
            anchor.Source.Index = unitEnd;

            if (TryExpandBranches(hoisted, TakeUnit(anchor, unitEnd), branches, saved))
            {
                _hoistedUnits++;
                MoveUnitAnchorPastUnit(anchor.Source);
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

        MoveUnitAnchorPastUnit(anchor.Source);
        _conditionalRangeStart = _output.Count;

        return true;
    }

    /// <summary>
    /// 展開し直した文の次へ、文の始まりの目印を置く。
    /// </summary>
    /// <param name="source">文を読んだトークンソース。読み終えた位置を指している。</param>
    /// <remarks>
    /// <b>次の文もそこから始まる。</b>
    /// 展開し直した文のトークンは積んだソースから出すので、切れ目の目印は置かれない
    /// (<see cref="NoteUnitBoundary"/> はファイルのトークンにだけ置く)。
    /// 置かずにいると、続けて書いた次の文は、条件で中身が変わるマクロを使っていても複製されない
    /// (<c>CTYPE d = 1; VTYPE e = 1;</c> の 2 つ目)。
    /// </remarks>
    private void MoveUnitAnchorPastUnit(TokenSource source)
        => _unitAnchor = (source, source.Index, _output.Count);

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
        _hoistingMacro = name;

        foreach ((SymbolCondition condition, MacroDefinition? definition) in branches)
        {
            SetMacro(name, definition);

            // 複製ごとに別のトークンを使う。同じインスタンスを並べると、
            // どのノードがどの複製のものかを、位置からもトークンからも引けなくなる。
            int start = _output.Count;
            ExpandTokensAsUnit([.. unit.Select(t => t.Duplicate())]);

            if (_output.Count > start)
            {
                _conditionalRegions.Add(
                    new ConditionalTokenRange(start, _output.Count - start, condition) { IsHoisted = true });
            }

            complete &= IsSelfContained(start, _output.Count);
        }

        _hoisting = false;
        _hoistingMacro = null;
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
    /// <para>
    /// 別のマクロの本体を通して参照しているときは、呼び出しの形かを確かめない (<paramref name="checkCallForm"/>)。
    /// 次のトークンは外側のマクロの後ろであり、内側のマクロの後ろではない。
    /// </para>
    /// </remarks>
    /// <param name="checkCallForm">関数形式マクロが呼び出しの形で書かれているかを確かめるかどうか。</param>
    private List<(SymbolCondition Condition, MacroDefinition? Definition)>? GetHoistableDefinitions(
        string name,
        bool checkCallForm = true)
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
        if (checkCallForm && definitions.All(d => d.Definition.IsFunctionLike) && !NextNonExhaustedTokenIsOpenParen())
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

    /// <summary>本体をたどる深さの上限。</summary>
    private const int MaxHoistLookupDepth = 8;

    /// <summary>
    /// マクロの定義ごとに、その本体を通して参照する、条件で中身の変わるマクロ。
    /// </summary>
    /// <remarks>
    /// 答えは条件付きで覚えた定義によって変わるので、それが変わったら捨てる (<see cref="ForgetHoistLookups"/>)。
    /// 識別子のたびに本体をたどると、ヘッダのマクロを使うたびに同じ走査を繰り返すことになる。
    /// </remarks>
    private readonly Dictionary<MacroDefinition, string?> _hoistLookups = new(ReferenceEqualityComparer.Instance);

    /// <summary>本体をたどった結果を捨てる。条件付きで覚えた定義が変わったときに呼ぶ。</summary>
    private void ForgetHoistLookups() => _hoistLookups.Clear();

    /// <summary>
    /// マクロの本体を通して参照している、条件で中身の変わるマクロを探す。
    /// </summary>
    /// <param name="name">文に書かれたマクロの名前。</param>
    /// <returns>参照しているマクロ。無いか、2 つ以上あれば <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>1 つの文で複製できるのは 1 つのマクロだけである。</b>
    /// 2 つ以上を参照していれば、組ごとに複製することになる。そこまではしない。
    /// </para>
    /// <para>
    /// 関数形式マクロは、呼び出しの形で書かれていなければ展開されないので探さない。
    /// </para>
    /// </remarks>
    private string? FindHoistableThroughMacros(string name)
    {
        if (!_macros.TryGetValue(name, out MacroDefinition? outer)
            || (outer.IsFunctionLike && !NextNonExhaustedTokenIsOpenParen()))
        {
            return null;
        }

        if (_hoistLookups.TryGetValue(outer, out string? cached))
        {
            return cached;
        }

        HashSet<string> visited = new(StringComparer.Ordinal) { name };
        HashSet<string> found = new(StringComparer.Ordinal);

        Walk(outer, depth: 1);

        string? result = found.Count == 1 ? found.First() : null;
        _hoistLookups[outer] = result;

        return result;

        void Walk(MacroDefinition macro, int depth)
        {
            if (depth > MaxHoistLookupDepth)
            {
                return;
            }

            foreach (HlslSyntaxToken token in macro.Body)
            {
                if (token.Kind != HlslSyntaxKind.IdentifierToken || !visited.Add(token.Text))
                {
                    continue;
                }

                if (GetHoistableDefinitions(token.Text, checkCallForm: false) is not null)
                {
                    found.Add(token.Text);
                }
                else if (_macros.TryGetValue(token.Text, out MacroDefinition? inner))
                {
                    Walk(inner, depth + 1);
                }
            }
        }
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
    /// 並べなかった領域が、巻き上げで補える形かを調べる。
    /// </summary>
    /// <param name="start">領域の開始位置 (最初の分岐の先頭)。</param>
    /// <param name="end">領域の終了位置 (<c>#endif</c> の行の次)。</param>
    /// <returns>補えるなら、その領域が定義するマクロの名前。補えなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>どの分岐にも <c>#define</c> しか無い領域だけを補える。</b>
    /// 並べなかった領域は、既定の構成の分岐しか展開されない。
    /// コードがあれば、もう一方の分岐のコードはどの木にも載らない。
    /// <c>#define</c> だけなら、どの分岐の定義も条件付きで覚えてあり (<see cref="RecordConditionalDefinition"/>)、
    /// 使う文を定義ごとに複製すれば、どの構成の展開も 1 本の木に載る。
    /// </para>
    /// <para>
    /// <c>#elif</c> と入れ子の <c>#if</c>、<c>#undef</c> は扱わない。条件の組み立てが別に要る。
    /// 定義を条件付きで覚えるのは解析しているファイルだけなので、ヘッダの領域も扱わない。
    /// </para>
    /// </remarks>
    private ImmutableArray<string>? FindHoistableDefinitions(int start, int end)
    {
        TokenSource source = CurrentSource;

        if (!IsHoistingEnabled
            || start >= source.Tokens.Length
            || !IsInRootSource(source.Tokens[start]))
        {
            return null;
        }

        ImmutableArray<HlslSyntaxToken> tokens = source.Tokens;
        List<string> defined = [];

        for (int i = start; i < end && i < tokens.Length; i = SkipToNextLine(tokens, i))
        {
            if (tokens[i].Kind == HlslSyntaxKind.EndOfFileToken)
            {
                break;
            }

            if (tokens[i].Kind != HlslSyntaxKind.HashToken
                || !tokens[i].IsAtLineStart
                || i + 1 >= tokens.Length
                || tokens[i + 1].Kind != HlslSyntaxKind.IdentifierToken)
            {
                return null;
            }

            switch (tokens[i + 1].Text)
            {
                case "define" when i + 2 < tokens.Length && tokens[i + 2].Kind == HlslSyntaxKind.IdentifierToken:
                    defined.Add(tokens[i + 2].Text);
                    break;

                case "else" or "endif":
                    break;

                default:
                    return null;
            }
        }

        return defined.Count > 0 ? [.. defined] : null;
    }

    /// <summary>
    /// 巻き上げで補えるかもしれない理由で並べなかったシンボルを覚える。
    /// </summary>
    /// <param name="symbol">並べなかったシンボル。</param>
    /// <param name="macros">その領域が定義するマクロ。</param>
    private void NoteHoistableDecline(string symbol, ImmutableArray<string> macros)
    {
        if (!_hoistableDeclines.TryGetValue(symbol, out HashSet<string>? names))
        {
            names = new HashSet<string>(StringComparer.Ordinal);
            _hoistableDeclines[symbol] = names;
        }

        names.UnionWith(macros);
        _hoistableMacros.UnionWith(macros);
    }

    /// <summary>
    /// マクロがコードとして展開されたことを、巻き上げで補えたかどうかと合わせて覚える。
    /// </summary>
    /// <param name="name">展開したマクロの名前。</param>
    /// <remarks>
    /// 定義ごとに複製している最中の、そのマクロ自身の展開だけが補えた展開である。
    /// 複製を諦めて 1 通りで展開したもの、別のマクロの本体や取り込んだヘッダの中で展開されたもの、
    /// 同じ文で別のマクロを複製している最中の展開は、どれか 1 つの構成の値でしかない。
    /// </remarks>
    private void NoteUnhoistedExpansion(string name)
    {
        if (_hoistableMacros.Contains(name)
            && !(_hoisting && string.Equals(name, _hoistingMacro, StringComparison.Ordinal)))
        {
            _unhoistedMacros.Add(name);
        }
    }

    /// <summary>
    /// 使う箇所がすべて巻き上げられたシンボルを、並べたものとして数え直す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>巻き上げで済んだなら、構成ごとに展開し直してはならない。</b>
    /// 展開し直した木には複製の片方しか無く、既定の木の複製とは位置が同じなので、
    /// 突き合わせでどちらの複製と対応するかが決まらない。
    /// 取り違えると、ある構成に存在する宣言が「その構成には無い」ことになる。
    /// </para>
    /// <para>
    /// 展開を終えてから決める。並べるかを決める時点では、マクロがどこで使われるかが分からない。
    /// </para>
    /// </remarks>
    private void RestoreHoistedDeclines()
    {
        HashSet<string> restored = new(StringComparer.Ordinal);

        foreach ((string symbol, HashSet<string> macros) in _hoistableDeclines)
        {
            if (_firmDeclines.Contains(symbol) || macros.Overlaps(_unhoistedMacros))
            {
                continue;
            }

            restored.Add(symbol);
            _declinedBothBranchSymbols.Remove(symbol);
            _bothBranchDeclines.RemoveWhere(d => string.Equals(d.Symbol, symbol, StringComparison.Ordinal));
            _mergedSymbols.Add(symbol);
        }

        // 補えなかった領域の組は、構成ごとの展開にまだ要る。
        foreach ((ImmutableArray<string> symbols, ImmutableArray<string> combination, HlslSyntaxToken directive) in _deferredCombinations)
        {
            if (!symbols.All(restored.Contains))
            {
                AddRequiredCombination(combination, directive);
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
