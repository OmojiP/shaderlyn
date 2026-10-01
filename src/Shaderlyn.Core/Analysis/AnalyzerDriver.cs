using System.Collections.Concurrent;
using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Core.Analysis;

/// <summary>
/// アナライザ群を構文木に適用し、診断を収集する実行エンジン。
/// </summary>
/// <remarks>
/// <para>
/// <b>設計の中心は「ルールが何個増えても構文木の走査は 1 回のまま」という点である。</b>
/// 各アナライザは関心のあるノード型を申告し、ドライバは
/// 「ノードの具象型 → 発火すべきアクション列」の対応表を作る。
/// 走査中は各ノードにつき辞書を 1 回引くだけで、アナライザの数に比例した処理は発生しない。
/// </para>
/// <para>
/// 対応表は具象型を初めて見た時点で構築して <see cref="_dispatchCache"/> に載せる。
/// 基底型での登録 (例: すべての式ノードに反応する) を許すため、
/// 登録型が具象型に代入可能かを <see cref="Type.IsAssignableFrom(Type)"/> で判定する必要があるが、
/// この判定は型ごとに 1 回だけで済む。
/// </para>
/// <para>
/// ドライバは生成後は不変であり、複数ファイルを並列に解析する用途でも共有できる。
/// そのためキャッシュには <see cref="ConcurrentDictionary{TKey,TValue}"/> を使っている。
/// </para>
/// </remarks>
internal sealed class AnalyzerDriver
{
    private readonly AnalyzerOptions _options;
    private readonly IAnalyzerTimingRecorder? _timings;
    private readonly ImmutableArray<NodeActionRegistration> _nodeActions;
    private readonly ImmutableArray<(DiagnosticAnalyzer Owner, Action<SyntaxTreeAnalysisContext> Action)> _syntaxTreeActions;
    private readonly ImmutableArray<(DiagnosticAnalyzer Owner, Action<AnalysisStartContext> Action)> _unitStartActions;

    /// <summary>ノードの具象型から、発火すべきアクション列への対応表。</summary>
    private readonly ConcurrentDictionary<Type, ImmutableArray<NodeActionRegistration>> _dispatchCache = new();

    /// <summary>
    /// アナライザ群を登録してドライバを生成する。
    /// </summary>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <param name="options">実行時設定。省略時は既定値を使う。</param>
    /// <remarks>
    /// <para>
    /// 設定によって全ルールが無効化されているアナライザは、ここで丸ごと除外する。
    /// 診断を作ってから捨てるのではなく、そもそも実行しないことで、
    /// ルール総数が増えても実行コストが「有効なルール数」にのみ比例するようにしている。
    /// </para>
    /// <para>
    /// <see cref="DiagnosticAnalyzer.Initialize(AnalysisContext)"/> はここで一度だけ呼ばれる。
    /// ファイルごとには呼ばれない。
    /// </para>
    /// </remarks>
    /// <param name="timings">実行時間を受け取る先。null なら測らない。</param>
    public AnalyzerDriver(
        IEnumerable<DiagnosticAnalyzer> analyzers,
        AnalyzerOptions? options = null,
        IAnalyzerTimingRecorder? timings = null)
    {
        ArgumentNullException.ThrowIfNull(analyzers);

        _options = options ?? AnalyzerOptions.Default;
        _timings = timings;

        List<NodeActionRegistration> nodeActions = [];
        List<(DiagnosticAnalyzer, Action<SyntaxTreeAnalysisContext>)> treeActions = [];
        List<(DiagnosticAnalyzer, Action<AnalysisStartContext>)> unitStartActions = [];
        ImmutableArray<DiagnosticAnalyzer>.Builder enabled = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

        foreach (DiagnosticAnalyzer analyzer in analyzers)
        {
            if (!analyzer.SupportedDiagnostics.Any(_options.IsEnabled))
            {
                continue;
            }

            enabled.Add(analyzer);
            analyzer.Initialize(new AnalysisContext(analyzer, nodeActions, treeActions, unitStartActions));
        }

        Analyzers = enabled.ToImmutable();
        _nodeActions = [.. nodeActions];
        _syntaxTreeActions = [.. treeActions];
        _unitStartActions = [.. unitStartActions];
    }

    /// <summary>
    /// 設定によって有効と判定され、実際に実行されるアナライザ。
    /// </summary>
    public ImmutableArray<DiagnosticAnalyzer> Analyzers { get; }

    /// <summary>
    /// 1 ファイルを解析し、診断を返す。
    /// </summary>
    /// <param name="unit">解析対象のファイルと構文木。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>ソースコード上の出現順に整列した診断。字句・構文解析段階の診断も含む。</returns>
    /// <remarks>
    /// 実行順序は「ファイル全体アクション → 構文木の走査 → 走査完了アクション」である。
    /// 結果は最後に位置で整列されるため、この順序が出力順に影響することはない。
    /// </remarks>
    public ImmutableArray<Diagnostic> Analyze(AnalysisTarget unit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        DiagnosticCollector sink = new(_options);

        foreach (Diagnostic syntaxDiagnostic in unit.SyntaxDiagnostics)
        {
            sink.Report(syntaxDiagnostic);
        }

        // ファイル単位の状態を作るアナライザから、このファイルにのみ有効な登録を集める。
        List<NodeActionRegistration> perUnitNodeActions = [];
        List<(DiagnosticAnalyzer Owner, Action<AnalysisEndContext> Action)> unitEndActions = [];

        foreach ((DiagnosticAnalyzer owner, Action<AnalysisStartContext> action) in _unitStartActions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunGuarded(owner, sink, unit, () => action(new AnalysisStartContext(
                owner, unit, _options, perUnitNodeActions, unitEndActions, cancellationToken)));
        }

        foreach ((DiagnosticAnalyzer owner, Action<SyntaxTreeAnalysisContext> action) in _syntaxTreeActions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunGuarded(owner, sink, unit, () => action(new SyntaxTreeAnalysisContext(
                owner, unit, _options, sink, cancellationToken)));
        }

        if (unit.Root is not null && (_nodeActions.Length > 0 || perUnitNodeActions.Count > 0))
        {
            WalkTree(unit, perUnitNodeActions, sink, cancellationToken);
        }

        foreach ((DiagnosticAnalyzer owner, Action<AnalysisEndContext> action) in unitEndActions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunGuarded(owner, sink, unit, () => action(new AnalysisEndContext(
                owner, unit, _options, sink, cancellationToken)));
        }

        return ApplySuppressions(sink.ToSortedArray());
    }

    /// <summary>
    /// 抑制コメントで抑制するよう指示された診断を取り除く。
    /// </summary>
    /// <param name="diagnostics">取り除く前の診断。</param>
    /// <returns>抑制されなかった診断。</returns>
    /// <remarks>
    /// <para>
    /// 判定は<b>診断が指しているファイル</b>に対して行う。
    /// 診断の位置情報はソーステキストそのものを保持しているため、
    /// include されたファイルの中の指摘も、そのファイルに書かれた抑制コメントで抑制できる。
    /// </para>
    /// <para>
    /// アナライザではなくドライバで適用しているのは、
    /// ルールごとに抑制の実装を書かせないためである。
    /// 抑制に対応し忘れたルールが混ざると、
    /// 「抑制したはずの指摘が消えない」という形で利用者を混乱させる。
    /// </para>
    /// </remarks>
    private static ImmutableArray<Diagnostic> ApplySuppressions(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.IsEmpty)
        {
            return diagnostics;
        }

        ImmutableArray<Diagnostic>.Builder kept = ImmutableArray.CreateBuilder<Diagnostic>(diagnostics.Length);

        foreach (Diagnostic diagnostic in diagnostics)
        {
            SuppressionLookup index = SuppressionLookup.GetOrCreate(diagnostic.Location.Source);

            if (index.IsEmpty || !index.IsSuppressed(diagnostic.Id, diagnostic.Location.LineSpan.Start.Line))
            {
                kept.Add(diagnostic);
            }
        }

        return kept.Count == diagnostics.Length ? diagnostics : kept.ToImmutable();
    }

    /// <summary>
    /// 構文木を 1 回だけ走査し、各ノードに対応するアクションを発火させる。
    /// </summary>
    /// <param name="unit">解析対象。</param>
    /// <param name="perUnitNodeActions">このファイルにのみ有効なノードアクション。</param>
    /// <param name="sink">診断の受け皿。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    private void WalkTree(
        AnalysisTarget unit,
        List<NodeActionRegistration> perUnitNodeActions,
        DiagnosticCollector sink,
        CancellationToken cancellationToken)
    {
        // ファイル単位の登録はキャッシュできない (ファイルごとに中身が変わる) ため、
        // グローバル分だけをキャッシュ経由で引き、ファイル単位分はその場で線形に絞り込む。
        // ファイル単位の登録は通常 1 ファイルあたり数個なので、この線形走査は問題にならない。
        foreach (SyntaxNode node in unit.Root!.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();

            Type nodeType = node.GetType();

            foreach (NodeActionRegistration registration in GetGlobalActionsFor(nodeType))
            {
                RunGuarded(registration.Owner, sink, unit,
                    () => registration.Invoke(node, unit, _options, sink, cancellationToken));
            }

            foreach (NodeActionRegistration registration in perUnitNodeActions)
            {
                if (registration.NodeType.IsAssignableFrom(nodeType))
                {
                    RunGuarded(registration.Owner, sink, unit,
                        () => registration.Invoke(node, unit, _options, sink, cancellationToken));
                }
            }
        }
    }

    /// <summary>
    /// 指定した具象ノード型で発火すべきグローバル登録の一覧を、キャッシュ経由で取得する。
    /// </summary>
    /// <param name="nodeType">ノードの具象型。</param>
    /// <returns>発火すべき登録の配列。該当が無ければ空配列。</returns>
    private ImmutableArray<NodeActionRegistration> GetGlobalActionsFor(Type nodeType)
        => _dispatchCache.GetOrAdd(
            nodeType,
            static (type, actions) => [.. actions.Where(a => a.NodeType.IsAssignableFrom(type))],
            _nodeActions);

    /// <summary>
    /// アナライザのコードを例外から保護しつつ実行する。
    /// </summary>
    /// <param name="owner">実行するアナライザ。例外時の原因表示に使う。</param>
    /// <param name="sink">診断の受け皿。</param>
    /// <param name="unit">解析対象。例外を報告する位置の算出に使う。</param>
    /// <param name="action">実行する処理。</param>
    /// <remarks>
    /// <para>
    /// 1 つのアナライザが投げた例外で解析全体を止めないための防壁である。
    /// リンタは壊れた入力・想定外の構文を日常的に食わされる立場にあり、
    /// 個別ルールの不具合で CI が丸ごと落ちると、他の正しい指摘まで届かなくなる。
    /// </para>
    /// <para>
    /// <see cref="OperationCanceledException"/> だけは再スローする。
    /// これは不具合ではなく正常なキャンセル要求であり、握り潰すと
    /// タイムアウトやユーザーの中断が効かなくなるためである。
    /// </para>
    /// </remarks>
    private void RunGuarded(DiagnosticAnalyzer owner, DiagnosticCollector sink, AnalysisTarget unit, Action action)
    {
        long start = _timings is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sink.Report(Diagnostic.Create(
                WellKnownDescriptors.AnalyzerThrew,
                Location.Create(unit.Text, new TextSpan(0, 0)),
                owner.GetType().Name,
                ex.Message));
        }
        finally
        {
            _timings?.Record(
                owner.GetType().Name, System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
    }
}
