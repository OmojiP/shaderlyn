using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// 条件付きコンパイル。#if / #ifdef / #elif / #else / #endif の状態と、条件式の評価。
/// </summary>
internal sealed partial class HlslPreprocessor
{
    // --------------------------------------------------------------------
    // 条件付きコンパイル
    // --------------------------------------------------------------------

    /// <summary>
    /// 1 つの <c>#if</c> ブロックの状態。
    /// </summary>
    /// <param name="Directive">この状態を作った指令のトークン。診断の位置に使う。</param>
    private sealed record ConditionalState(HlslSyntaxToken Directive)
    {
        /// <summary>現在の分岐が有効かどうか。</summary>
        public bool IsBranchActive { get; set; }

        /// <summary>これまでにいずれかの分岐が採用されたかどうか。</summary>
        public bool HasTakenBranch { get; set; }

        /// <summary>親が非活性であるために、このブロック全体が非活性かどうか。</summary>
        public bool IsParentSkipping { get; init; }

        /// <summary><c>#else</c> を通過済みかどうか。</summary>
        public bool SeenElse { get; set; }

        /// <summary>
        /// この連なりの条件が、構成によって結果が変わるものを参照しているかどうか。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>参照していない条件は、単一構成で決めた分岐がどの構成でも通る分岐である。</b>
        /// インクルードガードや <c>#if SHADER_TARGET &gt;= 30</c> がこれにあたる。
        /// その中にあるシンボルの条件は、外側を気にせず並べてよく、
        /// その中の <c>#define</c> は構成によって定義が変わるマクロではない。
        /// </para>
        /// <para>
        /// <c>#elif</c> のどれか 1 つでも参照していれば、連なり全体を参照しているものとする。
        /// </para>
        /// </remarks>
        public bool IsConfigurationDependent { get; set; }

        /// <summary>
        /// 今の分岐に付ける条件。付けない (この分岐を残さない) 場合は <see langword="null"/>。
        /// </summary>
        /// <remarks>
        /// <c>#ifdef _NORMALMAP</c> の側では <c>_NORMALMAP</c>、
        /// その <c>#else</c> の側では <c>!_NORMALMAP</c> になる。
        /// </remarks>
        public SymbolCondition? KeptCondition { get; set; }

        /// <summary>この領域の分岐をすべて残すかどうか。</summary>
        /// <remarks>
        /// <see cref="KeptCondition"/> と分けているのは、
        /// どの構成でも通らない分岐 (<c>#elif</c> の条件が偽で確定した側) では
        /// 条件を付けずに読み飛ばすためである。
        /// そこでも領域そのものは残す扱いのままにしておかないと、
        /// その次の <c>#else</c> に条件を付けられなくなる。
        /// </remarks>
        public bool Folded { get; init; }

        /// <summary>
        /// まだどの分岐にも当たっていない条件。
        /// </summary>
        /// <remarks>
        /// <c>#elif</c> と <c>#else</c> は「それまでの分岐がすべて外れたとき」に通る。
        /// <c>#ifdef A</c> の後は <c>!A</c>、
        /// その <c>#elif defined(B)</c> の後は <c>!A &amp;&amp; !B</c> になる。
        /// </remarks>
        public SymbolCondition Remaining { get; set; }

        /// <summary>
        /// 今の分岐が非活性として始まった位置。記録しない分岐では <see langword="null"/>。
        /// </summary>
        public int? InactiveFrom { get; set; }

        /// <summary>この連なりの <c>#if</c> / <c>#elif</c> の条件に書かれた名前。</summary>
        public List<string> ConditionSymbols { get; } = [];

        /// <summary>この連なりの条件が見ているキーワード (<see cref="KeywordRegion.Symbols"/>)。</summary>
        public HashSet<string> RegionSymbols { get; } = new(StringComparer.Ordinal);

        /// <summary>この連なりの条件が、どのキーワードで変わるのか分からないマクロを見ているかどうか。</summary>
        public bool RegionDependsOnMacros { get; set; }

        /// <summary>
        /// 今の分岐を通る条件。宣言されたシンボルで表す (<see cref="PreprocessorOptions.DeclaredSymbols"/>)。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>並べたかどうかに関わらず持つ。</b>
        /// <see cref="KeptCondition"/> は並べた分岐にしか付かないが、
        /// 組を記録するには、読み飛ばした外側の条件も要る
        /// (<see cref="RecordRequiredCombinations"/>)。
        /// </para>
        /// <para>
        /// 読めない条件は制約にしない (<see cref="SymbolCondition.Always"/>)。
        /// 単一構成で値の決まる条件なので、どの構成でも同じように評価される。
        /// </para>
        /// </remarks>
        public SymbolCondition PathBranch { get; set; }

        /// <summary>まだどの分岐にも当たっていない条件。<see cref="PathBranch"/> と同じ読み方をする。</summary>
        public SymbolCondition PathRemaining { get; set; }
    }

    /// <summary>
    /// 現在のトークンが非活性領域にあるかどうか。
    /// </summary>
    /// <remarks>
    /// <b>両方の分岐を残す条件では、どちらの分岐も非活性にならない。</b>
    /// どちらの経路もいつか通るコードなので、捨てずに並べて条件を付ける。
    /// </remarks>
    private bool IsSkipping
        => _conditionals.Count > 0
           && !_conditionals[^1].IsBranchActive
           && !IsInKeptBranch;

    /// <summary>両方の分岐を残す条件の中にいるかどうか。</summary>
    /// <remarks>
    /// <b>見るのは一番内側だけで足りる。</b>
    /// 両方を残す条件は、外側から途切れずに続く範囲でしか選ばない
    /// (<see cref="ChooseKeptCondition(SymbolCondition?, HlslSyntaxToken)"/>)。
    /// 片方だけを残す条件が 1 つでも挟まれば、その内側はもう選ばれない。
    /// </remarks>
    private bool IsInKeptBranch
        => _conditionals.Count > 0 && _conditionals[^1].KeptCondition is not null;

    /// <summary>
    /// 今の位置に付ける条件。両方を残す条件の中でなければ「常に」。
    /// </summary>
    /// <remarks>
    /// <b>入れ子は掛け合わせる。</b>
    /// <c>#ifdef A</c> の中の <c>#ifdef B</c> にあるコードが現れるのは
    /// A と B の両方が定義されているときだけである。
    /// </remarks>
    private SymbolCondition CurrentCondition
    {
        get
        {
            SymbolCondition current = SymbolCondition.Always;

            foreach (ConditionalState state in _conditionals)
            {
                if (state.KeptCondition is { } kept)
                {
                    current = current.And(kept);
                }
            }

            return current;
        }
    }

    /// <summary>
    /// そのシンボルの条件で、両方の分岐を残すかどうかを決める。
    /// </summary>
    /// <param name="symbol">条件に書かれたシンボル。読み取れなかった場合は <see langword="null"/>。</param>
    /// <param name="expectDefined">定義されている側が最初の分岐なら <see langword="true"/>。</param>
    /// <param name="directive">指令名のトークン。既定の構成と同じ領域だけを並べるのに使う。</param>
    /// <returns>残す場合は最初の分岐の条件。残さない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 残すのは、そのシンボルが <c>BothBranchSymbols</c> にあり、
    /// かつ領域が並べてよい形をしている場合だけである。
    /// 判定できないものは残さない側へ倒す (<see cref="ConditionalRegionScanner"/>)。
    /// </para>
    /// <para>
    /// <b>入れ子は、外側も両方を残している場合にだけ残す。</b>
    /// 外側が片方だけを残していると、その外側で捨てられた分岐の中に
    /// この条件があったのかどうかが、付けた条件から読み取れなくなる。
    /// 外側も残していれば、条件を掛け合わせるだけで済む
    /// (<see cref="CurrentCondition"/>)。
    /// </para>
    /// </remarks>
    private SymbolCondition? ChooseKeptCondition(string? symbol, bool expectDefined, HlslSyntaxToken directive)
    {
        if (symbol is null)
        {
            return null;
        }

        if (_options.BothBranchSymbols.Contains(symbol))
        {
            SymbolCondition present = SymbolOrDefinition(symbol);
            return ChooseKeptCondition(expectDefined ? present : present.Negate(), directive);
        }

        // 条件付きで覚えたマクロなら、その定義がある条件として読む。
        // #ifdef _A の中で定義した USE_A を #ifdef USE_A で見る書き方は、_A の条件と同じである。
        return GetDefinedCondition(symbol) is { } defined
            ? ChooseKeptCondition(expectDefined ? defined : defined.Negate(), directive)
            : null;
    }

    /// <summary>
    /// その条件で、すべての分岐を残すかどうかを決める。
    /// </summary>
    /// <param name="candidate">変換した条件。変換できなかった場合は <see langword="null"/>。</param>
    /// <param name="directive">指令名のトークン。既定の構成と同じ領域だけを並べるのに使う。</param>
    /// <returns>残す場合は最初の分岐の条件。残さない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// シンボルを 1 つも含まない条件は残さない。
    /// 単一構成での値が決まっているので、並べる意味が無い。
    /// </remarks>
    private SymbolCondition? ChooseKeptCondition(SymbolCondition? candidate, HlslSyntaxToken directive)
    {
        if (candidate is not { } written || _options.BothBranchSymbols.IsEmpty)
        {
            return null;
        }

        // 並べずに展開しているキーワードは、この構成では値が決まっている。
        // 条件付きで覚えたマクロの条件には、そうしたキーワードも現れる
        // (#ifdef _HEIGHTMAP の中で定義した _CONSERVATIVE_DEPTH_OFFSET を、ヘッダが #ifdef で見る)。
        // 残したまま並べると、そのキーワードのときだけの分岐も展開され、その #define がこの構成に漏れる。
        SymbolCondition condition = FixUnmergedSymbols(written);

        // 既定の構成が並べた領域だけを並べる。
        // 判断は展開しながら決めるので、マクロの状態が違えば構成ごとに変わりうる。
        // 変わると、既定の木とバリアントの違いが「そのシンボルで変わる部分」だけではなくなり、
        // 突き合わせが無関係な条件を付ける。
        if (_options.MergeOnlyRegions is { } allowed
            && !allowed.Contains(new MergedRegion(directive.Source.FilePath, directive.Span.Start)))
        {
            foreach (string declined in condition.EnumerateSymbols())
            {
                AddDeclinedSymbol(declined, BothBranchDeclineReason.OutsideDefaultMergedRegions, directive);
            }

            return null;
        }

        ImmutableArray<string> symbols = [.. condition.EnumerateSymbols()];

        if (symbols.IsEmpty)
        {
            return null;
        }

        // 構成によらない条件のために読み飛ばしている分岐は、どの構成でも読まれない。
        // 並べる意味も、並べられなかったと数える意味も無い。
        // そのキーワードを有効にして展開し直しても読めるものは増えないので、扱い済みとして数える
        // (別のカーネルの指定で読み飛ばしている分岐など)。
        if (IsInStaticSkippedBranch(_conditionals.Count))
        {
            AddMergedSymbols(condition);
            return null;
        }

        // ここから先は「残すつもりだったが残せなかった」場合を記録する。
        // 1 か所でも残せなかったシンボルは、まだ構成ごとの展開が要る。
        // 1 つずつ有効にしただけでは通らない組は、分岐ごとに別に記録する (RecordRequiredCombinations)。
        ConditionalRegionLayout layout =
            ConditionalRegionScanner.Scan(CurrentSource.Tokens, CurrentSource.Index, out int end);

        if (FindDeclineReason(layout, end, directive) is { } reason)
        {
            // #define しか無い領域なら、使う文を定義ごとに複製して補えることがある。
            // 補えたかは展開を終えてから決める (RestoreHoistedDeclines)。
            ImmutableArray<string>? hoistable = reason == BothBranchDeclineReason.RegionDefinesMacros
                ? FindHoistableDefinitions(CurrentSource.Index, end)
                : null;

            if (hoistable is not null)
            {
                _hoistableRegions[directive] = symbols;
            }

            foreach (string symbol in symbols)
            {
                AddDeclinedSymbol(symbol, reason, directive, hoistable);
            }

            return null;
        }

        return condition;
    }

    /// <summary>並べずに展開しているキーワードを、この構成での値で置き換える。</summary>
    /// <param name="condition">対象の条件。</param>
    /// <returns>置き換えた条件。</returns>
    /// <remarks>
    /// 並べるキーワード (<see cref="PreprocessorOptions.BothBranchSymbols"/>) 以外は、この構成では定義されているかが決まっている。
    /// バリアントで有効にしたキーワードや、並べ直しのために外したキーワードがこれにあたる。
    /// </remarks>
    private SymbolCondition FixUnmergedSymbols(SymbolCondition condition)
        => condition.Assume(
            symbol => _options.BothBranchSymbols.Contains(symbol) ? null : _options.PredefinedMacros.ContainsKey(symbol));

    /// <summary>キーワードが定義されている条件を求める。</summary>
    /// <param name="symbol">宣言されたキーワード。</param>
    /// <returns>キーワードそのものが有効な条件と、その名前を <c>#define</c> した条件の和。</returns>
    /// <remarks>
    /// <para>
    /// <b>キーワードの名前は、別のキーワードの条件の下で <c>#define</c> されることがある。</b>
    /// HDRP の Lit は、廃止した <c>_ENABLESPECULAROCCLUSION</c> が有効なら
    /// <c>_SPECULAR_OCCLUSION_FROM_BENT_NORMAL_MAP</c> を定義して、新しいコードを通す。
    /// <c>#ifdef _SPECULAR_OCCLUSION_FROM_BENT_NORMAL_MAP</c> の中は、どちらのキーワードでも存在する。
    /// </para>
    /// <para>
    /// キーワードそのものの条件だけを付けると、古いキーワードだけを有効にした構成でその中身が無いことになる。
    /// その構成を別に展開して補っていたが、条件を正しく付ければ要らない。
    /// </para>
    /// </remarks>
    private SymbolCondition SymbolOrDefinition(string symbol)
    {
        SymbolCondition keyword = SymbolCondition.Symbol(symbol);

        return GetDefinedCondition(symbol) is { } defined ? keyword.Or(defined) : keyword;
    }

    /// <summary>その名前が、構成によって定義が変わるものかを判定する。</summary>
    /// <param name="name">条件やマクロの本体に現れた名前。</param>
    /// <returns>変わるなら <see langword="true"/>。</returns>
    /// <remarks>
    /// 宣言されたシンボル (<see cref="PreprocessorOptions.ConfigurationSymbols"/> ほか) と、
    /// 構成によって定義が変わるマクロ (構成で変わる分岐の中で定義されたもの、
    /// そうした分岐の中でだけ定義されたと覚えているもの) が該当する。
    /// </remarks>
    private bool IsConfigurationDependentName(string name)
        => _options.ConfigurationSymbols.Contains(name)
           || _options.DeclaredSymbols.Contains(name)
           || _options.BothBranchSymbols.Contains(name)
           || _options.IncludedDeclaredSymbols.Contains(name)
           || _conditionallyDefinedMacros.Contains(name)
           || GetDefinedCondition(name) is { IsAlways: false };

    /// <summary>その行が、構成によって定義が変わる名前を参照しているかを判定する。</summary>
    /// <param name="line">条件式の行か、マクロの本体。</param>
    /// <returns>参照していれば <see langword="true"/>。</returns>
    private bool ReferencesConfigurationDependentName(IEnumerable<HlslSyntaxToken> line)
        => line.Any(token => token.Kind == HlslSyntaxKind.IdentifierToken && IsConfigurationDependentName(token.Text));

    /// <summary>その行が、構成によって定義が変わるマクロ (宣言されたシンボルを除く) を参照しているかを判定する。</summary>
    /// <param name="line">条件式の行。</param>
    /// <returns>参照していれば <see langword="true"/>。</returns>
    private bool ReferencesConfigurationDependentMacro(IEnumerable<HlslSyntaxToken> line)
        => line.Any(token => token.Kind == HlslSyntaxKind.IdentifierToken
                             && (_conditionallyDefinedMacros.Contains(token.Text)
                                 || GetDefinedCondition(token.Text) is { IsAlways: false }));

    /// <summary>今の位置が、構成によって結果が変わる条件の中にあるかどうか。</summary>
    private bool IsInConfigurationDependentBranch
        => _conditionals.Any(state => state.IsConfigurationDependent);

    /// <summary>
    /// 今の位置が、構成によらない条件のために読み飛ばしている分岐の中にあるかどうか。
    /// </summary>
    /// <param name="depth">見る外側の条件の数。</param>
    /// <returns>読み飛ばしていれば <see langword="true"/>。</returns>
    /// <remarks>
    /// そこにあるコードは、どの構成でも読まれない (別のプラットフォーム向けのコードなど)。
    /// その中のシンボルの条件を「並べられなかった」と数えても、構成ごとに展開し直して読めるものは無い。
    /// </remarks>
    private bool IsInStaticSkippedBranch(int depth)
    {
        for (int i = 0; i < depth && i < _conditionals.Count; i++)
        {
            ConditionalState state = _conditionals[i];

            if (state.KeptCondition is null && !state.IsConfigurationDependent && !state.IsBranchActive)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>今の領域を並べられない理由を求める。</summary>
    /// <param name="layout">領域の形。</param>
    /// <param name="end">領域の終了位置 (この位置は含まない)。</param>
    /// <param name="directive">指令名のトークン。</param>
    /// <returns>並べられない理由。並べてよければ <see langword="null"/>。</returns>
    private BothBranchDeclineReason? FindDeclineReason(ConditionalRegionLayout layout, int end, HlslSyntaxToken directive)
    {
        if (!CurrentSource.IsFile)
        {
            return BothBranchDeclineReason.NotInFile;
        }

        // 構成によらない外側の条件は、どの構成でも同じ分岐を通る。並べる妨げにならない。
        if (_conditionals.Any(state => state.KeptCondition is null && state.IsConfigurationDependent))
        {
            return BothBranchDeclineReason.OuterNotMerged;
        }

        if (!CanMerge(layout, CurrentSource.Index, end))
        {
            return layout switch
            {
                ConditionalRegionLayout.DefinesMacros => BothBranchDeclineReason.RegionDefinesMacros,
                ConditionalRegionLayout.SwitchesIncludes => BothBranchDeclineReason.RegionSwitchesIncludes,
                ConditionalRegionLayout.Unterminated => BothBranchDeclineReason.RegionUnterminated,
                _ => BothBranchDeclineReason.RegionNotSelfContained,
            };
        }

        return CallsConfigurationDependentMacro(CurrentSource.Index, end)
            ? BothBranchDeclineReason.CallsConfigurationDependentMacro
            : null;
    }

    /// <summary>
    /// 今の位置に至る条件。宣言されたシンボルで表す。
    /// </summary>
    /// <remarks>
    /// 読み飛ばしている分岐の中でも求まる。<c>#define</c> を条件付きで覚えるのに使う
    /// (<see cref="RecordConditionalDefinition"/>)。
    /// </remarks>
    private SymbolCondition CurrentPathCondition
    {
        get
        {
            SymbolCondition path = SymbolCondition.Always;

            foreach (ConditionalState state in _conditionals)
            {
                path = path.And(state.PathBranch);
            }

            return path;
        }
    }

    /// <summary>
    /// その形の領域を並べてよいかを判定する。
    /// </summary>
    /// <param name="layout">領域の形。</param>
    /// <param name="start">領域の開始位置。</param>
    /// <param name="end">領域の終了位置 (この位置は含まない)。</param>
    /// <returns>並べてよければ <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>分岐の中で定義したマクロは、並べるとどの構成でも定義されたことになる。</b>
    /// マクロ表は 1 つしかなく、展開も 1 回しか行わないためである。
    /// 条件の中でしか使わないマクロなら、その定義がある条件として読めるので害が無い
    /// (<see cref="GetDefinedCondition"/>)。
    /// </para>
    /// <para>
    /// コードの中で使われていれば、その展開はどれか 1 つの構成のものになる。
    /// 取り込むヘッダが使っているかどうかは、この時点では分からない。
    /// 実際に展開されたら、そのときに後戻りする (<see cref="NoteMergedMacroUse"/>)。
    /// </para>
    /// </remarks>
    private bool CanMerge(ConditionalRegionLayout layout, int start, int end)
        => layout switch
        {
            ConditionalRegionLayout.Mergeable => true,
            ConditionalRegionLayout.DefinesMacros => DefinesOnlyConditionMacros(start, end),

            // 取り込みの中身は、並べるかを決める時点では読んでいない。
            // ヘッダが定義したマクロがコードとして使われたら、そのときに並べ直す。
            ConditionalRegionLayout.SwitchesIncludes =>
                _options.MergeSwitchedIncludes && DefinesOnlyConditionMacros(start, end),
            _ => false,
        };

    /// <summary>
    /// その領域が定義するマクロが、条件の中でしか使われていないかを判定する。
    /// </summary>
    /// <param name="start">領域の開始位置。</param>
    /// <param name="end">領域の終了位置 (この位置は含まない)。</param>
    /// <returns>条件の中でしか使われていなければ <see langword="true"/>。</returns>
    private bool DefinesOnlyConditionMacros(int start, int end)
    {
        ImmutableArray<HlslSyntaxToken> tokens = CurrentSource.Tokens;
        HashSet<string> usedInCode = GetNamesUsedInCode(tokens);
        Dictionary<string, int> defineCounts = GetDefineCounts(tokens);

        for (int i = start; i < end && i < tokens.Length; i++)
        {
            if (tokens[i].Kind != HlslSyntaxKind.HashToken
                || !tokens[i].IsAtLineStart
                || i + 2 >= tokens.Length
                || tokens[i + 1].Kind != HlslSyntaxKind.IdentifierToken
                || tokens[i + 1].Text is not ("define" or "undef")
                || tokens[i + 2].Kind != HlslSyntaxKind.IdentifierToken)
            {
                continue;
            }

            // コードで使うマクロでも、このファイルで 1 度しか定義していなければ並べてみる。
            // 定義した分岐の中でしか使わないなら、並べても展開は変わらない。
            // そうでない使い方が見つかれば、展開のときに並べ直す (NoteMergedMacroUse)。
            if (usedInCode.Contains(tokens[i + 2].Text)
                && (tokens[i + 1].Text == "undef" || defineCounts.GetValueOrDefault(tokens[i + 2].Text) != 1))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>そのトークン列の中で、名前ごとに <c>#define</c> した回数を数える。</summary>
    /// <param name="tokens">対象のトークン列。</param>
    /// <returns>名前と回数。</returns>
    private Dictionary<string, int> GetDefineCounts(ImmutableArray<HlslSyntaxToken> tokens)
    {
        if (_defineCounts.TryGetValue(tokens, out Dictionary<string, int>? counts))
        {
            return counts;
        }

        counts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i].Kind == HlslSyntaxKind.HashToken
                && tokens[i].IsAtLineStart
                && tokens[i + 1].Kind == HlslSyntaxKind.IdentifierToken
                && tokens[i + 1].Text == "define"
                && tokens[i + 2].Kind == HlslSyntaxKind.IdentifierToken)
            {
                counts[tokens[i + 2].Text] = counts.GetValueOrDefault(tokens[i + 2].Text) + 1;
            }
        }

        _defineCounts[tokens] = counts;
        return counts;
    }

    /// <summary>
    /// そのトークン列の中で、条件以外の場所に現れる名前を集める。
    /// </summary>
    /// <param name="tokens">対象のトークン列。</param>
    /// <returns>コードやマクロの本体に現れる名前。</returns>
    /// <remarks>
    /// <para>
    /// <c>#if</c> 系の行に現れる名前は、条件としての使用である。
    /// <c>#define</c> / <c>#undef</c> の直後の名前は、定義する名前そのものである。
    /// それ以外はすべてコードとして数える。マクロの本体は展開されてコードになるためである。
    /// </para>
    /// <para>1 つのトークン列につき 1 回だけ数える。領域ごとに数え直すと走査が二乗になる。</para>
    /// </remarks>
    private HashSet<string> GetNamesUsedInCode(ImmutableArray<HlslSyntaxToken> tokens)
    {
        if (_namesUsedInCode.TryGetValue(tokens, out HashSet<string>? names))
        {
            return names;
        }

        names = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == HlslSyntaxKind.HashToken
                && tokens[i].IsAtLineStart
                && i + 1 < tokens.Length
                && tokens[i + 1].Kind == HlslSyntaxKind.IdentifierToken)
            {
                string directive = tokens[i + 1].Text;

                if (directive is "if" or "ifdef" or "ifndef" or "elif" or "else" or "endif" or "undef" or "pragma")
                {
                    // 行の終わりまで、コードとしての使用ではない。
                    i = SkipToNextLine(tokens, i) - 1;
                    continue;
                }

                if (directive == "define")
                {
                    // 定義する名前そのものは使用ではない。本体はコードとして数える。
                    i += 2;
                    continue;
                }
            }

            if (tokens[i].Kind == HlslSyntaxKind.IdentifierToken)
            {
                names.Add(tokens[i].Text);
            }
        }

        _namesUsedInCode[tokens] = names;
        return names;
    }

    /// <summary>次の行の先頭の位置を返す。</summary>
    /// <param name="tokens">対象のトークン列。</param>
    /// <param name="index">今の位置。</param>
    /// <returns>次の行の先頭の位置。</returns>
    private static int SkipToNextLine(ImmutableArray<HlslSyntaxToken> tokens, int index)
    {
        for (int i = index + 1; i < tokens.Length; i++)
        {
            if (tokens[i].IsAtLineStart)
            {
                return i;
            }
        }

        return tokens.Length;
    }

    /// <summary>
    /// 並べる分岐が、外側の並べた分岐と合わせて成り立つ構成を持つかを判定する。
    /// </summary>
    /// <param name="branch">その分岐に付ける条件。</param>
    /// <param name="enclosingCount">外側として数える条件ブロックの数 (<c>_conditionals</c> の先頭から)。</param>
    /// <returns>持つなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <c>#pragma multi_compile _ _X _Y</c> の <c>#ifdef _X</c> の中の <c>#ifdef _Y</c> は、
    /// 条件の代数では <c>_X ∧ _Y</c> として成り立つが、Unity はその構成を作らない
    /// (<see cref="PreprocessorOptions.SymbolConstraints"/>)。
    /// </remarks>
    private bool IsPossibleBranch(SymbolCondition branch, int enclosingCount)
    {
        if (_options.SymbolConstraints.IsEmpty)
        {
            return true;
        }

        SymbolCondition path = branch;

        for (int i = 0; i < enclosingCount; i++)
        {
            if (_conditionals[i].KeptCondition is { } kept)
            {
                path = path.And(kept);
            }
        }

        return _options.SymbolConstraints.IsPossible(path);
    }

    /// <summary>
    /// 分岐に入ったので、その分岐を通る条件を求め、要る組を記録する。
    /// </summary>
    /// <param name="state">対象の条件ブロック。</param>
    /// <param name="directive">分岐を始めた指令の名前のトークン。</param>
    /// <param name="condition">
    /// 指令に書かれた条件。<c>#else</c> では <see cref="SymbolCondition.Always"/>。
    /// 読まない場合や読めなかった場合は <see langword="null"/>。
    /// </param>
    /// <remarks>
    /// <c>#elif</c> と <c>#else</c> は「それまでの分岐がすべて外れたとき」に通る。
    /// 読めなかった条件は、この分岐の制約にも、残りの分岐の制約にもしない。
    /// </remarks>
    private void EnterPathBranch(ConditionalState state, HlslSyntaxToken directive, SymbolCondition? condition)
    {
        state.PathBranch = state.PathRemaining.And(condition ?? SymbolCondition.Always);

        if (condition is { } written)
        {
            state.PathRemaining = state.PathRemaining.And(written.Negate());
        }

        RecordRequiredCombinations(state, directive);
    }

    /// <summary>
    /// 今の分岐を通すために、同時に定義されている必要があるシンボルの組を記録する。
    /// </summary>
    /// <param name="state">今の分岐の条件ブロック。</param>
    /// <param name="directive">分岐を始めた指令の名前のトークン。</param>
    /// <remarks>
    /// <para>
    /// <b>入れ子は、外側の分岐の条件をすべて掛け合わせる。</b>
    /// <c>#ifdef _A</c> の中の <c>#ifdef _B</c> は、<c>_A</c> だけ・<c>_B</c> だけの構成では通らない。
    /// 外側を並べられず読み飛ばした場合、覚えないとこの分岐の中はどの構成にも現れず、
    /// 上限に達したわけでもないので <c>SL0003</c> も出ない。
    /// </para>
    /// <para>
    /// 並べた分岐は記録しない。外側もすべて並べているので、
    /// 既定の構成の木に条件付きで載っている。
    /// </para>
    /// <para>
    /// <b>取り込んだヘッダに書かれた条件も記録する。</b>
    /// ヘッダの分岐が切り替えるのは、解析しているファイルから見える宣言である。
    /// その分岐を一度も読まなければ、そこでしか宣言されていない関数の呼び出しは
    /// 「どこにも無い」ことにされ、誤りを見逃す。
    /// 並び順で後ろに置くので、上限に達したときに落ちるのはヘッダ側からになる
    /// (<c>ShaderCompilationBuilder.SelectVariantCombinations</c>)。
    /// </para>
    /// </remarks>
    private void RecordRequiredCombinations(ConditionalState state, HlslSyntaxToken directive)
    {
        if (state.KeptCondition is not null || _options.DeclaredSymbols.IsEmpty || directive.IsFromMacroExpansion)
        {
            return;
        }

        SymbolCondition path = SymbolCondition.Always;

        foreach (ConditionalState enclosing in _conditionals)
        {
            path = path.And(enclosing.PathBranch);
        }

        // 条件を記号として読めなかった分岐は、どの構成でも同じように通る。組にするものが無い。
        // ヘッダの条件はほとんどがこれになるので、ここで早く抜けることが効く。
        if (path.IsAlways)
        {
            return;
        }

        // 実在しない構成を求める項は、どの構成でも通らないので組にしない。
        // どれか 1 つが必ず有効な行の先頭が無いことを求める項には、同じ行の別のシンボルを足す。
        // 巻き上げで補えるかもしれない領域の組は、補えたかが決まるまで記録しない (RestoreHoistedDeclines)。
        _hoistableRegions.TryGetValue(state.Directive, out ImmutableArray<string> hoistable);

        foreach (ImmutableArray<string> combination in _options.SymbolConstraints.EnumerateRequiredCombinations(path))
        {
            if (!hoistable.IsDefault)
            {
                _deferredCombinations.Add((hoistable, combination, directive));
                continue;
            }

            AddRequiredCombination(combination, directive);
        }
    }

    /// <summary>
    /// <c>#ifdef</c> / <c>#ifndef</c> の名前を、宣言されたシンボルの条件として読む。
    /// </summary>
    /// <param name="symbol">条件に書かれた名前。読み取れなかった場合は <see langword="null"/>。</param>
    /// <param name="expectDefined"><c>#ifdef</c> であれば <see langword="true"/>。</param>
    /// <param name="directive">指令名のトークン。</param>
    /// <returns>読んだ条件。読まない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// シンボルでない名前は、<c>defined()</c> と同じくその場で値を解いて定数にする
    /// (<see cref="TryReadPathCondition"/>)。
    /// </para>
    /// <para>
    /// 取り込んだファイルの <c>#ifdef</c> は、そのヘッダ自身が宣言したシンボルだけ読む
    /// (<see cref="PreprocessorOptions.IncludedDeclaredSymbols"/>)。
    /// </para>
    /// </remarks>
    private SymbolCondition? ReadPathSymbol(string? symbol, bool expectDefined, HlslSyntaxToken directive)
    {
        if (symbol is null || _options.DeclaredSymbols.IsEmpty || directive.IsFromMacroExpansion)
        {
            return null;
        }

        if (!IsInRootFile(directive))
        {
            return _options.IncludedDeclaredSymbols.Contains(symbol)
                ? SymbolCondition.Symbol(symbol, expectDefined)
                : null;
        }

        if (_options.DeclaredSymbols.Contains(symbol))
        {
            SymbolCondition present = SymbolOrDefinition(symbol);
            return expectDefined ? present : present.Negate();
        }

        // 条件付きで覚えたマクロなら、その定義がある条件として読む。
        if (GetDefinedCondition(symbol) is { } defined)
        {
            return expectDefined ? defined : defined.Negate();
        }

        return _macros.ContainsKey(symbol) == expectDefined ? SymbolCondition.Always : SymbolCondition.Never;
    }

    // --------------------------------------------------------------------
    // 条件式の評価
    // --------------------------------------------------------------------

    /// <summary>
    /// <c>#if</c> / <c>#elif</c> の条件式を評価する。
    /// </summary>
    /// <param name="line">条件式の行。</param>
    /// <returns>評価結果。</returns>
    /// <remarks>
    /// <para>
    /// 行は呼び出し側が読む。<c>#elif</c> では評価の前に
    /// 「記号として扱える形か」を見る必要があるためである。
    /// </para>
    /// <para>
    /// 手順の順序が重要である。
    /// <b><c>defined X</c> をマクロ展開より先に処理しなければならない。</b>
    /// 先に展開すると <c>X</c> が値へ置き換わり、
    /// <c>defined</c> が「定義されているか」を判定できなくなる。
    /// </para>
    /// <para>
    /// <b>展開の後にもう一度処理する必要がある。</b>
    /// マクロの本体が <c>defined</c> を含む場合があるためである。Unity の
    /// <c>#define UNITY_SHOULD_SAMPLE_SH (defined(LIGHTPROBE_SH) &amp;&amp; ...)</c> がその例で、
    /// 展開後の処理を省くと <c>defined</c> が識別子として評価され、
    /// 条件式の構造が壊れる。C 標準では未定義の動作だが、
    /// 主要なコンパイラはいずれもこれを受け付けており、Unity はそれに依存している。
    /// </para>
    /// </remarks>
    private bool EvaluateCondition(ImmutableArray<HlslSyntaxToken> line)
    {
        if (line.IsEmpty)
        {
            return false;
        }

        ImmutableArray<HlslSyntaxToken> withoutDefined = ResolveDefinedOperators(line);
        ImmutableArray<HlslSyntaxToken> expanded = ExpandTokenList(withoutDefined, depth: 0);
        ImmutableArray<HlslSyntaxToken> resolved = ResolveDefinedOperators(expanded);

        bool result = ConditionalExpressionEvaluator.Evaluate(resolved, out string? error);

        if (error is not null)
        {
            Report(HlslDescriptors.PreprocessorError, line[0].GetLocation(), error);
        }

        return result;
    }

    /// <summary>条件式の一部を、診断を出さずに評価する。</summary>
    /// <param name="tokens">評価する式。</param>
    /// <param name="value">評価結果。</param>
    /// <returns>評価できれば <see langword="true"/>。</returns>
    /// <remarks>
    /// 行全体は別に評価し、誤りはそちらで報告する。一部を評価して同じ誤りを重ねて出さない。
    /// </remarks>
    private bool TryEvaluateSilently(ImmutableArray<HlslSyntaxToken> tokens, out bool value)
    {
        ImmutableArray<HlslSyntaxToken> withoutDefined = ResolveDefinedOperators(tokens);
        ImmutableArray<HlslSyntaxToken> expanded = ExpandTokenList(withoutDefined, depth: 0);
        ImmutableArray<HlslSyntaxToken> resolved = ResolveDefinedOperators(expanded);

        value = ConditionalExpressionEvaluator.Evaluate(resolved, out string? error);
        return error is null;
    }

    /// <summary>
    /// <c>#ifdef</c> / <c>#ifndef</c> を評価する。
    /// </summary>
    /// <param name="directiveToken">指令名のトークン。</param>
    /// <param name="expectDefined">定義されていることを期待する場合は <see langword="true"/>。</param>
    /// <param name="symbol">条件に書かれたシンボル。読み取れなかった場合は <see langword="null"/>。</param>
    /// <returns>評価結果。</returns>
    /// <remarks>
    /// シンボルの名前も返すのは、両方の分岐を残すかどうかの判定に要るためである。
    /// </remarks>
    private bool EvaluateDefinedLine(HlslSyntaxToken directiveToken, bool expectDefined, out string? symbol)
    {
        ImmutableArray<HlslSyntaxToken> line = ReadDirectiveLine();
        symbol = null;

        if (line.IsEmpty || line[0].Kind != HlslSyntaxKind.IdentifierToken)
        {
            Report(HlslDescriptors.PreprocessorError, GetDirectiveLocation(line),
                "#ifdef / #ifndef にはマクロ名が必要です。");
            return false;
        }

        RecordConditionalIdentifier(line[0]);
        symbol = line[0].Text;

        // 後ろの記述は評価に使わない。コンパイラと同じく、名前 1 つだけで決める。
        if (line.Length > 1)
        {
            ReportExtraTokensAfterConditionName(directiveToken, line, expectDefined);
        }

        bool defined = _macros.ContainsKey(line[0].Text);
        return defined == expectDefined;
    }

    /// <summary>
    /// <c>#ifdef</c> / <c>#ifndef</c> の名前の後ろに続く記述を報告する。
    /// </summary>
    /// <param name="directiveToken">指令名のトークン。</param>
    /// <param name="line">指令の行のトークン。先頭が条件の名前で、2 つ以上ある。</param>
    /// <param name="expectDefined"><c>#ifdef</c> であれば <see langword="true"/>。</param>
    /// <remarks>
    /// <para>
    /// <b>書き手はたいてい <c>#if</c> の式を書いたつもりでいる。</b>
    /// だから <c>#if defined(...)</c> への書き直し方をメッセージで示す。
    /// </para>
    /// <para>
    /// <b>直しを添えるのは、<c>#ifdef</c> の名前を <c>||</c> / <c>&amp;&amp;</c> だけでつないだ形に限る。</b>
    /// この形なら、それぞれの名前を <c>defined()</c> で囲むのが書き手の意図だと一意に読める。
    /// <c>#ifndef A || B</c> は「どちらも無い」のか「どちらかが無い」のかが読み取れないので、
    /// 例を示すだけにする (<see cref="Diagnostic.WithSuggestedReplacement"/>)。
    /// </para>
    /// </remarks>
    private void ReportExtraTokensAfterConditionName(
        HlslSyntaxToken directiveToken,
        ImmutableArray<HlslSyntaxToken> line,
        bool expectDefined)
    {
        // 指令名から行の終わりまでを指す。直しはこの範囲をそのまま置き換える。
        Location location = Location.Create(
            directiveToken.Source,
            TextSpan.FromBounds(directiveToken.Span.Start, line[^1].Span.End));

        string ignored = string.Join(" ", line[1..].Select(token => token.Text));
        string? expression = expectDefined ? TryJoinAsDefinedExpression(line) : null;

        string guidance = expression is not null
            ? $"'#if {expression}' と書いてください。"
            : expectDefined
                ? "複数の名前を条件にするときは '#if defined(A) || defined(B)' のように書きます。"
                : "複数の名前を条件にするときは '#if !defined(A) && !defined(B)' のように書きます。";

        Diagnostic diagnostic = Diagnostic.Create(
            HlslDescriptors.ExtraTokensAfterConditionName, location, directiveToken.Text, ignored, guidance);

        _diagnostics.Add(expression is not null ? diagnostic.WithSuggestedReplacement($"if {expression}") : diagnostic);
    }

    /// <summary>
    /// 名前を <c>||</c> / <c>&amp;&amp;</c> でつないだ行を、<c>defined()</c> の式に書き直す。
    /// </summary>
    /// <param name="line">指令の行のトークン。</param>
    /// <returns>書き直した式。その形でなければ <see langword="null"/>。</returns>
    private static string? TryJoinAsDefinedExpression(ImmutableArray<HlslSyntaxToken> line)
    {
        if (line.Length < 3 || line.Length % 2 == 0)
        {
            return null;
        }

        System.Text.StringBuilder expression = new();

        for (int i = 0; i < line.Length; i++)
        {
            HlslSyntaxToken token = line[i];

            if (i % 2 == 0)
            {
                if (token.Kind != HlslSyntaxKind.IdentifierToken)
                {
                    return null;
                }

                expression.Append("defined(").Append(token.Text).Append(')');
            }
            else
            {
                if (token.Kind is not (HlslSyntaxKind.BarBarToken or HlslSyntaxKind.AmpersandAmpersandToken))
                {
                    return null;
                }

                expression.Append(' ').Append(token.Text).Append(' ');
            }
        }

        return expression.ToString();
    }

    /// <summary>
    /// 条件で参照された名前を記録する。
    /// </summary>
    /// <param name="token">参照された名前のトークン。</param>
    /// <remarks>
    /// <para>
    /// <b>これはシンボルの検査に要る。</b>
    /// <c>#pragma shader_feature</c> で宣言されていないシンボルを
    /// <c>#ifdef</c> で見ている場合、その分岐は決して有効にならない。
    /// 綴りを 1 文字誤っただけで、その機能が丸ごと入らないまま出荷されうる。
    /// </para>
    /// <para>
    /// 位置ごと覚えておく。名前だけでは、どこを直せばよいかを示せない。
    /// </para>
    /// </remarks>
    private void RecordConditionalIdentifier(HlslSyntaxToken token)
    {
        if (token.Kind == HlslSyntaxKind.IdentifierToken)
        {
            _conditionalIdentifiers.Add(token);
        }
    }

    /// <summary>
    /// <c>defined X</c> と <c>defined(X)</c> を 1 か 0 のリテラルへ置き換える。
    /// </summary>
    /// <param name="tokens">条件式のトークン列。</param>
    /// <returns>置き換え後のトークン列。</returns>
    private ImmutableArray<HlslSyntaxToken> ResolveDefinedOperators(ImmutableArray<HlslSyntaxToken> tokens)
    {
        ImmutableArray<HlslSyntaxToken>.Builder result = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        for (int i = 0; i < tokens.Length; i++)
        {
            HlslSyntaxToken token = tokens[i];

            if (token.Kind != HlslSyntaxKind.IdentifierToken || !token.TextIs("defined"))
            {
                result.Add(token);
                continue;
            }

            int index = i + 1;
            bool parenthesized = index < tokens.Length && tokens[index].Kind == HlslSyntaxKind.OpenParenToken;
            if (parenthesized)
            {
                index++;
            }

            if (index >= tokens.Length || tokens[index].Kind != HlslSyntaxKind.IdentifierToken)
            {
                Report(HlslDescriptors.PreprocessorError, token.GetLocation(),
                    "defined の後にはマクロ名が必要です。");
                result.Add(CreateNumericToken(token, 0));
                i = tokens.Length;
                continue;
            }

            RecordConditionalIdentifier(tokens[index]);

            bool defined = _macros.ContainsKey(tokens[index].Text);
            index++;

            if (parenthesized)
            {
                if (index < tokens.Length && tokens[index].Kind == HlslSyntaxKind.CloseParenToken)
                {
                    index++;
                }
                else
                {
                    Report(HlslDescriptors.PreprocessorError, token.GetLocation(),
                        "defined の括弧が閉じられていません。");
                }
            }

            result.Add(CreateNumericToken(token, defined ? 1 : 0));
            i = index - 1;
        }

        return result.ToImmutable();
    }
}
