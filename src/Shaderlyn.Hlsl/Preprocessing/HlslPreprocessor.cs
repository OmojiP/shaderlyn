using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// HLSL のプリプロセッサ。指令を解釈し、マクロを展開し、include を取り込む。
/// </summary>
/// <remarks>
/// <para>
/// <b>これが無いと実用的な Unity シェーダーは 1 つも解析できない。</b>
/// URP のシェーダーは <c>TEXTURE2D(_BaseMap)</c> や <c>CBUFFER_START(UnityPerMaterial)</c> のような
/// 関数形式マクロに依存しており、展開しなければ構文として成立しないためである。
/// </para>
/// <para>
/// 実装はトークンソースのスタックを中心に据えている。
/// ファイルの取り込みもマクロの展開も「新しいトークン列をスタックへ積む」操作として表現され、
/// 積まれた列を読み終えたら 1 つ外側へ戻る。
/// 指令の解釈をファイル由来のトークンに限定できるのがこの構成の要点で、
/// マクロ展開の結果に現れた <c>#</c> を指令と誤認しない。
/// </para>
/// <para>
/// <b>このクラスは入力がどれだけ壊れていても例外を投げない。</b>
/// 未終端の条件分岐も、循環 include も、解決できないマクロも診断として報告し、
/// 可能な限り処理を継続する。
/// </para>
/// </remarks>
internal sealed partial class HlslPreprocessor
{
    private readonly PreprocessorOptions _options;
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
    private readonly ImmutableArray<PragmaDirective>.Builder _pragmas = ImmutableArray.CreateBuilder<PragmaDirective>();

    /// <summary>条件で参照された名前。シンボルの検査に使う。</summary>
    private readonly List<HlslSyntaxToken> _conditionalIdentifiers = [];
    private readonly ImmutableArray<HlslSyntaxToken>.Builder _output = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

    private readonly TrackedTable<MacroDefinition> _macros = new();

    /// <summary>本体での位置を付けたマクロの本体。展開のたびに作り直さないために覚える。</summary>
    private readonly Dictionary<MacroDefinition, ImmutableArray<HlslSyntaxToken>> _markedBodies = [];

    /// <summary>
    /// マクロ表の内容を表すハッシュ。定義を足し引きするたびに差分で更新する。
    /// </summary>
    /// <remarks>
    /// <b>取り込みの展開結果を使い回してよいかの鍵である (<see cref="HlslIncludeCache"/>)。</b>
    /// 1 つの定義の追加・削除を排他的論理和で足し引きするので、
    /// 表がどれだけ大きくても更新も比較も定数時間で済む。
    /// </remarks>
    private ulong _macroTableHash;

    /// <summary>マクロ 1 つ分のハッシュ。<see cref="_macros"/> と同じ名前を持つ。</summary>
    /// <remarks>
    /// 読んだ名前で展開結果を照合するたびに本体を数え直すと、照合が本体の長さに比例して高くつく。
    /// </remarks>
    private readonly Dictionary<string, ulong> _macroHashes = new(StringComparer.Ordinal);

    /// <summary>
    /// 条件の中で定義されたマクロの名前。
    /// </summary>
    /// <remarks>
    /// <b>この名前は、構成が変われば別のものになりうる。</b>
    /// URP の <c>SAMPLE_GI</c> は 8 通りに定義され、引数の数まで違う。
    /// こういう名前を呼んでいる領域は、両方の分岐を並べられない
    /// (<see cref="ChooseKeptCondition(SymbolCondition?, HlslSyntaxToken)"/>)。
    /// </remarks>
    private readonly TrackedNameSet _conditionallyDefinedMacros = new();

    /// <summary>
    /// <see cref="_conditionallyDefinedMacros"/> の中身のハッシュ。取り込みの展開結果を使い回す鍵に入れる。
    /// </summary>
    /// <remarks>
    /// 同じマクロ表でも、どのマクロが構成によって変わるかが違えば、並べる分岐が変わる。
    /// </remarks>
    private ulong _conditionallyDefinedHash;
    private readonly List<TokenSource> _sources = [];
    private readonly List<ConditionalState> _conditionals = [];
    private readonly HashSet<string> _activeIncludePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _resolvedIncludes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _unresolvedIncludes = new(StringComparer.Ordinal);

    /// <summary>現れた #include と、その解決結果。</summary>
    /// <remarks>
    /// 集合ではなく並びで持つ。同じヘッダを 2 か所で取り込んでいる場合、
    /// どちらの行の話かを示せなければ利用者は直しようがない。
    /// </remarks>
    private readonly List<IncludeReference> _includes = [];

    /// <summary>
    /// 解析しているファイルそのもの。
    /// </summary>
    /// <remarks>
    /// 取り込んだヘッダの書き方は利用者に直しようがない。
    /// ヘッダだけを見て報告する検査は、このファイルかどうかで絞る。
    /// </remarks>
    private SourceText? _rootSource;

    /// <summary>条件付きで残したトークンの範囲。</summary>
    private readonly List<ConditionalTokenRange> _conditionalRegions = [];

    /// <summary>
    /// 今 1 つの取り込みを記録している最中なら、その記録。
    /// </summary>
    /// <remarks>
    /// 入れ子の取り込みは、外側のものだけを記録する。
    /// 外側の記録に内側の展開結果も丸ごと入るので、取りこぼしにはならない。
    /// </remarks>
    private readonly List<IncludeRecording> _recordings = [];

    /// <summary>取り込みを 1 つ記録している最中の状態。</summary>
    /// <param name="Key">覚えるときの鍵。</param>
    /// <param name="FilePath">記録しているファイルのパス。</param>
    /// <param name="OutputStart">記録を始めたときの出力の長さ。</param>
    /// <param name="PragmaStart">記録を始めたときの <c>#pragma</c> の数。</param>
    /// <param name="DiagnosticStart">記録を始めたときの診断の数。</param>
    /// <param name="ConditionalRegionStart">記録を始めたときの条件付き範囲の数。</param>
    /// <param name="ConditionalIdentifierStart">記録を始めたときの条件で参照された名前の数。</param>
    /// <param name="IncludeStart">記録を始めたときの <c>#include</c> の数。</param>
    /// <param name="SkippedIdentifiers">記録を始めたときの非活性領域の名前。</param>
    /// <param name="ExpandedRootMacros">
    /// この展開の中で使われた、解析しているファイルが定義したマクロの名前。
    /// 取り込みの結果を使い回したときも、並べてよかったかを判断できるようにするために覚える。
    /// </param>
    /// <param name="DeclinedBothBranchSymbols">記録を始めたときの残せなかったシンボル。</param>
    /// <param name="BothBranchDeclines">この展開の中で残せなかった理由。</param>
    /// <param name="MergedSymbols">この展開の中で並べた条件のシンボル。</param>
    /// <param name="MergedRegions">この展開の中で並べた領域。</param>
    /// <param name="KeywordRegions">この展開の中で閉じた、構成によって結果が変わる連なり。</param>
    /// <param name="MacroAffectingSymbols">この展開の中で、分岐でマクロ表か取り込みを変えたキーワード。</param>
    /// <param name="MacroKeywordChanges">この展開の中で覚えた、マクロとそれを分岐で定義・削除したキーワードの組。</param>
    /// <param name="UnknownMacroSources">この展開の中で、どのキーワードで変わるのか分からない連なりで定義・削除されたマクロ。</param>
    /// <param name="CodeIdentifiers">この展開の中で、コードに現れた識別子。</param>
    /// <param name="HeaderDefinitionChanges">この展開の中で変えた、ヘッダの定義がある条件。<see langword="null"/> は削除。</param>
    /// <param name="MergedMacros">この展開の中で、並べた分岐で定義したマクロと、その条件のシンボル。</param>
    /// <param name="MergedMacroConflicts">この展開の中で、並べた分岐のマクロがコードで使われたシンボル。</param>
    /// <param name="ResolvedIncludes">記録を始めたときの解決できた取り込み。</param>
    /// <param name="UnresolvedIncludes">記録を始めたときの解決できなかった取り込み。</param>
    /// <param name="ConditionallyDefined">記録を始めたときの条件の中で定義された名前。</param>
    /// <param name="MacroChanges">この取り込みが変えたマクロ。</param>
    private sealed record IncludeRecording(
        IncludeCacheKey Key,
        string FilePath,
        int OutputStart,
        int PragmaStart,
        int DiagnosticStart,
        int ConditionalRegionStart,
        int ConditionalIdentifierStart,
        int IncludeStart,
        HashSet<string> SkippedIdentifiers,
        HashSet<string> ExpandedRootMacros,
        HashSet<string> DeclinedBothBranchSymbols,
        HashSet<BothBranchDecline> BothBranchDeclines,
        HashSet<string> MergedSymbols,
        HashSet<MergedRegion> MergedRegions,
        List<KeywordRegion> KeywordRegions,
        HashSet<string> MacroAffectingSymbols,
        List<KeyValuePair<string, string>> MacroKeywordChanges,
        HashSet<string> UnknownMacroSources,
        HashSet<string> CodeIdentifiers,
        List<KeyValuePair<string, SymbolCondition?>> HeaderDefinitionChanges,
        Dictionary<string, ImmutableArray<string>> MergedMacros,
        HashSet<string> MergedMacroConflicts,
        HashSet<string> ResolvedIncludes,
        HashSet<string> UnresolvedIncludes,
        HashSet<string> ConditionallyDefined,
        Dictionary<string, MacroDefinition?> MacroChanges)
    {
        /// <summary>この展開が、書く前に読んだマクロの名前と、読んだときの状態。</summary>
        public Dictionary<string, ulong> MacroReads { get; } = new(StringComparer.Ordinal);

        /// <summary>この展開が、書く前に読んだ「条件の中で定義された名前」と、読んだときの状態。</summary>
        public Dictionary<string, ulong> ConditionallyDefinedReads { get; } = new(StringComparer.Ordinal);

        /// <summary>この展開が、書く前に読んだヘッダの定義の名前と、読んだときの状態。</summary>
        public Dictionary<string, ulong> HeaderDefinitionReads { get; } = new(StringComparer.Ordinal);

        /// <summary>この展開が書いたマクロの名前。以後の読みは外の状態によらない。</summary>
        public HashSet<string> MacroWrites { get; } = new(StringComparer.Ordinal);

        /// <summary>この展開が書いた「条件の中で定義された名前」。</summary>
        public HashSet<string> ConditionallyDefinedWrites { get; } = new(StringComparer.Ordinal);

        /// <summary>この展開が書いたヘッダの定義の名前。</summary>
        public HashSet<string> HeaderDefinitionWrites { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>今の条件付き範囲が、出力のどこから始まっているか。</summary>
    private int _conditionalRangeStart;

    /// <summary>残すつもりだったが、領域の形のせいで残せなかったシンボル。</summary>
    /// <remarks>
    /// 1 つのシンボルが複数の領域を守っていることがある。
    /// 1 か所でも残せなかったなら、そのシンボルはまだ構成ごとの展開が要る。
    /// </remarks>
    private readonly SortedSet<string> _declinedBothBranchSymbols = new(StringComparer.Ordinal);

    /// <summary>残せなかった理由。計測のために残す。</summary>
    private readonly HashSet<BothBranchDecline> _bothBranchDeclines = [];

    /// <summary>
    /// 1 つずつ有効にしただけでは通らない、条件に書かれたシンボルの組。
    /// </summary>
    /// <remarks>
    /// <c>#if defined(_A) &amp;&amp; defined(_B)</c> の中は、どちらか一方だけの構成には現れない。
    /// 構成ごとの展開が要る領域では、この組をそのまま 1 つの構成として作る必要がある。
    /// 鍵で重複を除く。同じ組が何度書かれていても構成は 1 つでよい。
    /// </remarks>
    private readonly Dictionary<string, SymbolCombination> _requiredSymbolCombinations =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 解析しているファイルで定義されたマクロの、名前ごとの定義。
    /// </summary>
    /// <remarks>
    /// <b>読み飛ばした分岐の <c>#define</c> も覚える。</b>
    /// 別々の分岐で同じ名前を違う中身で定義すると、両方が有効な構成では再定義になる。
    /// 単一構成の展開ではどちらか一方しか通らないので、条件を持たないと気づけない
    /// (<see cref="RecordConditionalDefinition"/>)。
    /// </remarks>
    private readonly Dictionary<string, List<ConditionalMacro>> _conditionalMacros = new(StringComparer.Ordinal);

    /// <summary>解析しているファイルに書かれた <c>#define</c> と、その条件。<c>#undef</c> されても消さない。</summary>
    private readonly List<(MacroDefinition Definition, SymbolCondition Condition)> _writtenDefinitions = [];

    /// <summary>トークン列ごとに、条件以外の場所に現れる名前。</summary>
    /// <remarks>マクロを定義する分岐を並べてよいかの判定に使う。</remarks>
    private readonly Dictionary<ImmutableArray<HlslSyntaxToken>, HashSet<string>> _namesUsedInCode = [];

    /// <summary>トークン列ごとの、<c>#define</c> した回数を名前ごとに数えたもの。</summary>
    private readonly Dictionary<ImmutableArray<HlslSyntaxToken>, Dictionary<string, int>> _defineCounts = [];

    /// <summary>
    /// 並べた分岐で定義したマクロの、定義がある条件と定義の数。
    /// </summary>
    /// <remarks>
    /// 定義が 1 つだけで、使う位置の条件がその条件を含んでいれば、並べたままでもその位置の展開は正しい。
    /// 取り込みの結果を使い回したときは当て直さない。そのときは使う位置の条件が分からないので、並べ直す側へ倒す。
    /// </remarks>
    private readonly Dictionary<string, (SymbolCondition Condition, int Count)> _mergedMacroDefinitions = new(StringComparer.Ordinal);

    /// <summary>並べた分岐の中で定義したマクロと、その分岐の条件が見ているシンボル。</summary>
    /// <remarks>
    /// 並べた分岐の <c>#define</c> はどの構成でも効く。条件の中でしか使われない前提で並べている。
    /// コードとして展開されたら前提が崩れるので、そのシンボルを並べ直しの対象として記録する。
    /// </remarks>
    private readonly Dictionary<string, ImmutableArray<string>> _mergedMacros = new(StringComparer.Ordinal);

    /// <summary>
    /// 取り込んだヘッダが、並べた分岐の中で定義したマクロと、その定義がある条件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ヘッダの分岐も、条件の中でしか使わないマクロを定義しているだけなら並べられる。</b>
    /// 並べるとマクロ表ではどの構成でも定義されたことになるので、定義がある条件をここに覚え、
    /// 後の <c>#if defined(...)</c> をその条件として読む (<see cref="GetDefinedCondition"/>)。
    /// </para>
    /// <para>
    /// 解析しているファイルの定義は <see cref="_conditionalMacros"/> が持つ。
    /// そちらは再定義の食い違いの報告にも使い、ヘッダの定義を入れるとヘッダ同士の差し替えまで報告してしまう。
    /// </para>
    /// </remarks>
    private readonly TrackedTable<SymbolCondition> _headerDefinitions = new();

    /// <summary><see cref="_headerDefinitions"/> の中身のハッシュ。取り込みの展開結果を使い回す鍵に入れる。</summary>
    private ulong _headerDefinitionsHash;

    /// <summary>並べた分岐で定義したマクロがコードとして展開されたときの、そのシンボル。</summary>
    private readonly SortedSet<string> _mergedMacroConflicts = new(StringComparer.Ordinal);

    /// <summary>両方の分岐を並べた領域の、指令名のトークンの位置。</summary>
    private readonly HashSet<MergedRegion> _mergedRegions = [];

    /// <summary>構成によって結果が変わった <c>#if</c> の連なりの範囲。</summary>
    private readonly List<KeywordRegion> _keywordRegions = [];

    /// <summary>分岐の中でマクロを定義・削除するか、ヘッダを取り込むキーワード。</summary>
    private readonly SortedSet<string> _macroAffectingSymbols = new(StringComparer.Ordinal);

    /// <summary>マクロの名前と、そのマクロを分岐の中で定義・削除したキーワード。読み飛ばした分岐の分も含む。</summary>
    private readonly Dictionary<string, HashSet<string>> _macroKeywords = new(StringComparer.Ordinal);

    /// <summary>どのキーワードで変わるのか分からない連なりの中で定義・削除されたマクロの名前。</summary>
    private readonly HashSet<string> _unknownMacroSources = new(StringComparer.Ordinal);

    /// <summary>両方の分岐を並べた条件に現れたシンボル。中身が空の分岐も含む。</summary>
    private readonly SortedSet<string> _mergedSymbols = new(StringComparer.Ordinal);

    /// <summary>非活性領域にのみ現れた識別子。誤検出を防ぐためにルール側が参照する。</summary>
    private readonly HashSet<string> _skippedIdentifiers = new(StringComparer.Ordinal);

    /// <summary>取り込んだファイルの非活性領域に現れた識別子。</summary>
    private readonly HashSet<string> _skippedIncludedIdentifiers = new(StringComparer.Ordinal);

    /// <summary>解析しているファイル自身の非活性領域に現れた識別子。位置を持つ。</summary>
    private readonly List<HlslSyntaxToken> _skippedRootIdentifiers = [];

    /// <summary>このファイルの中で、条件が外れて読み飛ばした分岐。エディタの表示に使う。</summary>
    private readonly List<InactiveRegion> _inactiveRegions = [];

    /// <summary>
    /// プリプロセッサを生成する。
    /// </summary>
    /// <param name="options">実行時設定。</param>
    public HlslPreprocessor(PreprocessorOptions? options = null)
    {
        _options = options ?? PreprocessorOptions.Default;

        if (_options.IncludeCache is not null)
        {
            _macros.OnRead = NoteMacroRead;
            _conditionallyDefinedMacros.OnRead = NoteConditionallyDefinedRead;
            _headerDefinitions.OnRead = NoteHeaderDefinitionRead;
        }
    }

    /// <summary>
    /// ソーステキストをプリプロセスする。
    /// </summary>
    /// <param name="text">対象のソーステキスト。</param>
    /// <returns>展開済みトークン列と、検出した問題。</returns>
    /// <remarks>
    /// 先頭に差し込むコード (<see cref="PreprocessorOptions.Prelude"/>) が指定されている場合、
    /// それを対象ファイルより先に処理する。
    /// トークンソースはスタックであり後挿入先出しで読まれるため、
    /// <b>対象ファイルを先に積み、先頭に差し込むコードを後から積む</b>ことで先頭に差し込むコードが先に処理される。
    /// </remarks>
    public PreprocessResult Preprocess(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        _rootSource = text;

        DefinePredefinedMacros();
        PushFileSource(text);

        if (_options.Prelude is not null)
        {
            PushFileSource(_options.Prelude);
        }

        Run();

        ReportUnterminatedConditionals();
        RestoreHoistedDeclines();

        return new PreprocessResult(
            _output.ToImmutable(),
            _diagnostics.ToImmutable(),
            _pragmas.ToImmutable(),
            [.. _resolvedIncludes],
            [.. _unresolvedIncludes],
            _macros.ToImmutableDictionary(),
            [.. _skippedIdentifiers],
            [.. _conditionalIdentifiers],
            [.. _includes],
            [.. _conditionalRegions],
            [.. _declinedBothBranchSymbols])
        {
            InactiveRegions = [.. _inactiveRegions],
            BothBranchDeclines =
            [
                .. _bothBranchDeclines
                    .OrderBy(d => d.FilePath, StringComparer.Ordinal)
                    .ThenBy(d => d.Symbol, StringComparer.Ordinal)
                    .ThenBy(d => d.Reason)
                    .ThenBy(d => d.DirectiveSpan?.Start ?? -1),
            ],
            RequiredSymbolCombinations = [.. _requiredSymbolCombinations.Values],
            SkippedIncludedIdentifiers = [.. _skippedIncludedIdentifiers],
            SkippedRootIdentifiers = [.. _skippedRootIdentifiers],
            MergedMacroConflicts = [.. _mergedMacroConflicts],
            MergedRegions = [.. _mergedRegions.OrderBy(r => r.FilePath, StringComparer.Ordinal).ThenBy(r => r.Start)],
            KeywordRegions = [.. _keywordRegions],
            MacroAffectingSymbols = [.. _macroAffectingSymbols],
            MergedSymbols = [.. _mergedSymbols],
            HoistedUnits = _hoistedUnits,
            HoistRetries = _hoistRetries,
            HoistGiveUps = _hoistGiveUps,
            WrittenDefinitions = [.. _writtenDefinitions],
            NeverTrueConditions = [.. _neverTrueConditions],
        };
    }

    /// <summary>
    /// 主ループ。トークンを 1 つずつ取り出し、指令・マクロ・通常のトークンへ振り分ける。
    /// </summary>
    private void Run()
    {
        while (ProcessNextToken())
        {
        }
    }

    /// <summary>
    /// トークンを 1 つ処理する。
    /// </summary>
    /// <returns>処理するトークンがあれば <see langword="true"/>。</returns>
    /// <remarks>巻き上げは、積んだソースを読み終えるまでこれを回す (<see cref="ExpandTokensAsUnit"/>)。</remarks>
    private bool ProcessNextToken()
    {
        {
            HlslSyntaxToken? token = PeekToken();
            if (token is null)
            {
                return false;
            }

            // 指令として扱ってよいのはファイル由来のトークンだけである。
            // マクロ展開の結果に現れた # を指令と誤認すると、
            // 文字列化演算子を含むマクロで解析が崩れる。
            if (token.Kind == HlslSyntaxKind.HashToken && token.IsAtLineStart && CurrentSource.IsFile)
            {
                ProcessDirective();
                MoveUnitAnchorPastDirective();
                return true;
            }

            if (IsSkipping)
            {
                HlslSyntaxToken skipped = TakeToken();

                // 非活性領域の識別子を覚えておく。
                // 単一構成での展開では「別の構成でのみ宣言される uniform」が見えないため、
                // 「宣言が見つからない」ことを根拠に診断を出すルールが誤検出を起こす。
                // ここで名前を残しておけば、ルール側がその誤検出を避けられる。
                if (skipped.Kind == HlslSyntaxKind.IdentifierToken)
                {
                    AddSkippedIdentifier(skipped);
                }

                return true;
            }

            // キーワードで定義の変わるマクロがコードに現れたら、そのキーワードは連なりの外のトークンも変える。
            // 取り込みの記録には名前を残す。使い回したときに、その時点のマクロの記録で数え直す。
            if (token.Kind == HlslSyntaxKind.IdentifierToken)
            {
                NoteMacroUseInCode(token.Text);

                if (_recordings.Count > 0)
                {
                    string used = token.Text;
                    Record(recording => recording.CodeIdentifiers.Add(used));
                }
            }

            if (token.Kind == HlslSyntaxKind.IdentifierToken && TryHoistConditionalMacro())
            {
                return true;
            }

            if (token.Kind == HlslSyntaxKind.IdentifierToken && TryExpandMacro())
            {
                return true;
            }

            NoteUnitBoundary(token);

            // キーワードの名前がコードに残るなら、そのキーワードは連なりの外のトークンも変える
            // (有効にすると 1 に展開される)。まとめた構成に入れない (MacroAffectingSymbols)。
            if (token.Kind == HlslSyntaxKind.IdentifierToken && _options.ConfigurationSymbols.Contains(token.Text))
            {
                _macroAffectingSymbols.Add(token.Text);
                Record(recording => recording.MacroAffectingSymbols.Add(token.Text));
            }

            _output.Add(TakeToken());
            return true;
        }
    }

    // --------------------------------------------------------------------
    // 補助
    // --------------------------------------------------------------------

    /// <summary>2 つのトークンが空白を挟まず隣接しているかを判定する。</summary>
    /// <param name="left">左のトークン。</param>
    /// <param name="right">右のトークン。</param>
    /// <returns>隣接している場合は <see langword="true"/>。</returns>
    private static bool IsAdjacent(HlslSyntaxToken left, HlslSyntaxToken right)
        => ReferenceEquals(left.Source, right.Source) && left.Span.End == right.Span.Start;

    /// <summary>利用者のファイル (解析しているファイルと、利用者が書いたヘッダ) かを判定した結果。</summary>
    private readonly Dictionary<string, bool> _userFiles = new(StringComparer.Ordinal);

    /// <summary>そのパスが、利用者が書いて直せるファイルかを判定する。</summary>
    /// <param name="filePath">判定するファイルのパス。</param>
    /// <returns>解析しているファイルか、利用者のヘッダなら <see langword="true"/>。</returns>
    /// <remarks>
    /// 判定は <see cref="PreprocessorOptions.IsUserInclude"/> に任せる。
    /// 展開ではトークンごとに呼ぶので、パスごとに 1 度だけ判定する。
    /// </remarks>
    private bool IsUserFile(string filePath)
    {
        if (_rootSource is not null && string.Equals(filePath, _rootSource.FilePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (_options.IsUserInclude is not { } isUserInclude)
        {
            return false;
        }

        if (!_userFiles.TryGetValue(filePath, out bool user))
        {
            user = isUserInclude(filePath);
            _userFiles[filePath] = user;
        }

        return user;
    }

    /// <summary>トークンが、利用者のファイルにそのまま書かれたものかを判定する。</summary>
    /// <param name="token">判定するトークン。</param>
    /// <returns>マクロの展開でなく、利用者のファイルにあれば <see langword="true"/>。</returns>
    private bool IsWrittenInUserFile(HlslSyntaxToken token)
        => !token.IsFromMacroExpansion && IsUserFile(token.Source.FilePath);

    /// <summary>合成トークンを作る。</summary>
    /// <param name="kind">トークンの種別。</param>
    /// <param name="text">トークンのテキスト。</param>
    /// <param name="location">位置の基準とするトークン。</param>
    /// <returns>作成したトークン。</returns>
    private HlslSyntaxToken CreateSyntheticToken(HlslSyntaxKind kind, string text, HlslSyntaxToken? location = null)
    {
        SourceText source = location?.Source ?? SourceText.From(string.Empty, "<合成>");
        TextSpan span = location?.Span ?? new TextSpan(0, 0);
        return new HlslSyntaxToken(kind, source, span, text, isMissing: true);
    }

    /// <summary>数値リテラルのトークンを作る。</summary>
    /// <param name="location">位置の基準とするトークン。</param>
    /// <param name="value">数値。</param>
    /// <returns>作成したトークン。</returns>
    private static HlslSyntaxToken CreateNumericToken(HlslSyntaxToken location, int value)
        => new(HlslSyntaxKind.NumericLiteralToken, location.Source, location.Span,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>文字列リテラルのトークンを作る。</summary>
    /// <param name="value">文字列の中身。</param>
    /// <param name="location">位置の基準とするトークン。</param>
    /// <returns>作成したトークン。</returns>
    private static HlslSyntaxToken CreateStringToken(string value, HlslSyntaxToken location)
        => new(HlslSyntaxKind.StringLiteralToken, location.Source, location.Span, $"\"{value}\"",
            valueText: value);

    /// <summary>指令の行から診断の位置を求める。</summary>
    /// <param name="line">指令の行のトークン。</param>
    /// <returns>診断の位置。</returns>
    private Location GetDirectiveLocation(ImmutableArray<HlslSyntaxToken> line)
        => line.IsEmpty
            ? Location.Create(SourceText.From(string.Empty, "<不明>"), new TextSpan(0, 0))
            : line[0].GetLocation();

    /// <summary>診断を記録する。</summary>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="location">報告位置。</param>
    /// <param name="arguments">メッセージ書式へ埋め込む引数。</param>
    private void Report(DiagnosticDescriptor descriptor, Location location, params object?[] arguments)
        => _diagnostics.Add(Diagnostic.Create(descriptor, location, arguments));
}
