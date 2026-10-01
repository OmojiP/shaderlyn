using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Core.Analysis;

/// <summary>
/// 構文ノードに対するアクションの型消去済み呼び出し口。
/// </summary>
/// <param name="node">対象のノード。登録された型へのキャストは呼び出し先が行う。</param>
/// <param name="unit">解析中のファイル。</param>
/// <param name="options">実行時設定。</param>
/// <param name="sink">診断の受け皿。</param>
/// <param name="cancellationToken">キャンセル用トークン。</param>
internal delegate void NodeActionInvoker(
    SyntaxNode node,
    AnalysisTarget unit,
    AnalyzerOptions options,
    DiagnosticCollector sink,
    CancellationToken cancellationToken);

/// <summary>
/// ノードアクション 1 件の登録内容。
/// </summary>
/// <param name="NodeType">アクションが関心を持つノードの型。この型に代入可能なノードで発火する。</param>
/// <param name="Invoke">型消去済みの呼び出し口。</param>
/// <param name="Owner">登録元のアナライザ。例外が起きた際の原因特定に使う。</param>
internal sealed record NodeActionRegistration(Type NodeType, NodeActionInvoker Invoke, DiagnosticAnalyzer Owner);

/// <summary>
/// アナライザがアクションを登録するためのコンテキスト。ドライバの構築時に一度だけ渡される。
/// </summary>
/// <remarks>
/// ここで登録されたアクションは以降すべてのファイルに適用される。
/// ファイル単位の状態が必要な場合は <see cref="RegisterAnalysisStartAction"/> を使うこと。
/// </remarks>
public sealed class AnalysisContext
{
    private readonly DiagnosticAnalyzer _owner;
    private readonly List<NodeActionRegistration> _nodeActions;
    private readonly List<(DiagnosticAnalyzer Owner, Action<SyntaxTreeAnalysisContext> Action)> _syntaxTreeActions;
    private readonly List<(DiagnosticAnalyzer Owner, Action<AnalysisStartContext> Action)> _unitStartActions;

    internal AnalysisContext(
        DiagnosticAnalyzer owner,
        List<NodeActionRegistration> nodeActions,
        List<(DiagnosticAnalyzer, Action<SyntaxTreeAnalysisContext>)> syntaxTreeActions,
        List<(DiagnosticAnalyzer, Action<AnalysisStartContext>)> unitStartActions)
    {
        _owner = owner;
        _nodeActions = nodeActions;
        _syntaxTreeActions = syntaxTreeActions;
        _unitStartActions = unitStartActions;
    }

    /// <summary>
    /// 指定した型の構文ノードに対して実行するアクションを登録する。
    /// </summary>
    /// <typeparam name="TNode">関心のあるノードの型。</typeparam>
    /// <param name="action">ノードごとに呼ばれるアクション。</param>
    /// <remarks>
    /// <para>
    /// 基底型を指定すると、それに代入可能なすべての派生ノードで発火する。
    /// 例えば式の基底型を指定すれば、二項演算・関数呼び出しなど全ての式で呼ばれる。
    /// </para>
    /// <para>
    /// 何個アクションを登録しても構文木の走査回数は 1 回のままである。
    /// ドライバが「ノードの具象型 → 発火すべきアクション列」の対応表を作り、
    /// 走査中はその表を引くだけにしているため。
    /// </para>
    /// </remarks>
    public void RegisterNodeAction<TNode>(Action<NodeAnalysisContext<TNode>> action)
        where TNode : SyntaxNode
    {
        ArgumentNullException.ThrowIfNull(action);

        _nodeActions.Add(new NodeActionRegistration(
            typeof(TNode),
            (node, unit, options, sink, ct) =>
                action(new NodeAnalysisContext<TNode>(_owner, (TNode)node, unit, options, sink, ct)),
            _owner));
    }

    /// <summary>
    /// ファイル全体に対して 1 回だけ実行するアクションを登録する。
    /// </summary>
    /// <param name="action">ファイルごとに呼ばれるアクション。</param>
    /// <remarks>
    /// 構文木を必要としない検査 (ファイル全体のテキストに対する検査など) や、
    /// 構文木の根から独自に走査したい場合に使う。
    /// </remarks>
    public void RegisterSyntaxTreeAction(Action<SyntaxTreeAnalysisContext> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _syntaxTreeActions.Add((_owner, action));
    }

    /// <summary>
    /// ファイルの解析開始時に実行するアクションを登録する。ファイル単位の状態を作るために使う。
    /// </summary>
    /// <param name="action">ファイルの解析開始時に呼ばれるアクション。</param>
    /// <remarks>
    /// <para>
    /// 「宣言を集めてから、走査の最後にまとめて判定する」種類のルールのための仕組みである。
    /// 渡されるコンテキストに対してさらにノードアクションと終了アクションを登録でき、
    /// それらはこのファイルの解析中にのみ有効になる。
    /// </para>
    /// <para>
    /// 典型的な使い方:
    /// <code>
    /// context.RegisterAnalysisStartAction(start =>
    /// {
    ///     HashSet&lt;string&gt; declared = [];
    ///     start.RegisterNodeAction&lt;PropertySyntax&gt;(c => declared.Add(c.Node.Name));
    ///     start.RegisterAnalysisEndAction(end => { /* declared を使って判定・報告 */ });
    /// });
    /// </code>
    /// 状態をアナライザのインスタンスフィールドに置いてはならない。
    /// インスタンスは全ファイルで共有されるため、前のファイルの情報が混入する。
    /// </para>
    /// </remarks>
    public void RegisterAnalysisStartAction(Action<AnalysisStartContext> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _unitStartActions.Add((_owner, action));
    }
}

/// <summary>
/// ファイル 1 つ分の解析開始時に渡されるコンテキスト。このファイルにのみ有効なアクションを登録できる。
/// </summary>
public sealed class AnalysisStartContext
{
    private readonly DiagnosticAnalyzer _owner;
    private readonly List<NodeActionRegistration> _nodeActions;
    private readonly List<(DiagnosticAnalyzer Owner, Action<AnalysisEndContext> Action)> _unitEndActions;

    internal AnalysisStartContext(
        DiagnosticAnalyzer owner,
        AnalysisTarget unit,
        AnalyzerOptions options,
        List<NodeActionRegistration> nodeActions,
        List<(DiagnosticAnalyzer, Action<AnalysisEndContext>)> unitEndActions,
        CancellationToken cancellationToken)
    {
        _owner = owner;
        Unit = unit;
        Options = options;
        _nodeActions = nodeActions;
        _unitEndActions = unitEndActions;
        CancellationToken = cancellationToken;
    }

    /// <summary>解析中のファイル。</summary>
    public AnalysisTarget Unit { get; }

    /// <summary>実行時設定。</summary>
    public AnalyzerOptions Options { get; }

    /// <summary>キャンセル用トークン。</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// このファイルの走査中にのみ有効なノードアクションを登録する。
    /// </summary>
    /// <typeparam name="TNode">関心のあるノードの型。</typeparam>
    /// <param name="action">ノードごとに呼ばれるアクション。</param>
    public void RegisterNodeAction<TNode>(Action<NodeAnalysisContext<TNode>> action)
        where TNode : SyntaxNode
    {
        ArgumentNullException.ThrowIfNull(action);

        _nodeActions.Add(new NodeActionRegistration(
            typeof(TNode),
            (node, unit, options, sink, ct) =>
                action(new NodeAnalysisContext<TNode>(_owner, (TNode)node, unit, options, sink, ct)),
            _owner));
    }

    /// <summary>
    /// このファイルの走査が終わった時点で実行するアクションを登録する。
    /// </summary>
    /// <param name="action">走査完了後に呼ばれるアクション。</param>
    /// <remarks>走査中に集めた情報をもとに診断を報告するのはここで行う。</remarks>
    public void RegisterAnalysisEndAction(Action<AnalysisEndContext> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _unitEndActions.Add((_owner, action));
    }
}

/// <summary>
/// 構文ノード 1 つに対するアクションへ渡されるコンテキスト。
/// </summary>
/// <typeparam name="TNode">対象ノードの型。</typeparam>
public readonly struct NodeAnalysisContext<TNode>
    where TNode : SyntaxNode
{
    private readonly DiagnosticCollector _sink;
    private readonly DiagnosticAnalyzer _owner;

    internal NodeAnalysisContext(
        DiagnosticAnalyzer owner,
        TNode node,
        AnalysisTarget unit,
        AnalyzerOptions options,
        DiagnosticCollector sink,
        CancellationToken cancellationToken)
    {
        _owner = owner;
        Node = node;
        Unit = unit;
        Options = options;
        _sink = sink;
        CancellationToken = cancellationToken;
    }

    /// <summary>検査対象のノード。</summary>
    public TNode Node { get; }

    /// <summary>解析中のファイル。</summary>
    public AnalysisTarget Unit { get; }

    /// <summary>実行時設定。</summary>
    public AnalyzerOptions Options { get; }

    /// <summary>キャンセル用トークン。</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>診断を報告する。</summary>
    /// <param name="diagnostic">報告する診断。</param>
    public void ReportDiagnostic(Diagnostic diagnostic) => _sink.ReportFrom(_owner, diagnostic);

    /// <summary>
    /// 検査対象ノードの位置に診断を報告する。
    /// </summary>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="messageArguments">メッセージ書式へ埋め込む引数。</param>
    /// <remarks>ノード全体を指す最も一般的な報告形なので、短く書けるようにしている。</remarks>
    public void ReportDiagnostic(DiagnosticDescriptor descriptor, params object?[] messageArguments)
        => _sink.ReportFrom(_owner, Diagnostic.Create(descriptor, Location.Create(Unit.Text, Node.Span), messageArguments));

    /// <summary>
    /// ノード内の指定範囲に診断を報告する。
    /// </summary>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="span">指摘する範囲。</param>
    /// <param name="messageArguments">メッセージ書式へ埋め込む引数。</param>
    /// <remarks>
    /// ノード全体ではなく識別子だけを指したい場合に使う。
    /// 波線が広すぎる指摘は読み手が問題箇所を特定できないため、可能な限り狭い範囲を指すこと。
    /// </remarks>
    public void ReportDiagnostic(DiagnosticDescriptor descriptor, TextSpan span, params object?[] messageArguments)
        => _sink.ReportFrom(_owner, Diagnostic.Create(descriptor, Location.Create(Unit.Text, span), messageArguments));
}

/// <summary>
/// ファイル全体に対するアクションへ渡されるコンテキスト。
/// </summary>
public readonly struct SyntaxTreeAnalysisContext
{
    private readonly DiagnosticCollector _sink;
    private readonly DiagnosticAnalyzer _owner;

    internal SyntaxTreeAnalysisContext(
        DiagnosticAnalyzer owner,
        AnalysisTarget unit,
        AnalyzerOptions options,
        DiagnosticCollector sink,
        CancellationToken cancellationToken)
    {
        _owner = owner;
        Unit = unit;
        Options = options;
        _sink = sink;
        CancellationToken = cancellationToken;
    }

    /// <summary>解析中のファイル。</summary>
    public AnalysisTarget Unit { get; }

    /// <summary>実行時設定。</summary>
    public AnalyzerOptions Options { get; }

    /// <summary>キャンセル用トークン。</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>診断を報告する。</summary>
    /// <param name="diagnostic">報告する診断。</param>
    public void ReportDiagnostic(Diagnostic diagnostic) => _sink.ReportFrom(_owner, diagnostic);

    /// <summary>
    /// 指定範囲に診断を報告する。
    /// </summary>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="span">指摘する範囲。</param>
    /// <param name="messageArguments">メッセージ書式へ埋め込む引数。</param>
    public void ReportDiagnostic(DiagnosticDescriptor descriptor, TextSpan span, params object?[] messageArguments)
        => _sink.ReportFrom(_owner, Diagnostic.Create(descriptor, Location.Create(Unit.Text, span), messageArguments));
}

/// <summary>
/// ファイル 1 つ分の走査完了後に渡されるコンテキスト。
/// </summary>
public readonly struct AnalysisEndContext
{
    private readonly DiagnosticCollector _sink;
    private readonly DiagnosticAnalyzer _owner;

    internal AnalysisEndContext(
        DiagnosticAnalyzer owner,
        AnalysisTarget unit,
        AnalyzerOptions options,
        DiagnosticCollector sink,
        CancellationToken cancellationToken)
    {
        _owner = owner;
        Unit = unit;
        Options = options;
        _sink = sink;
        CancellationToken = cancellationToken;
    }

    /// <summary>解析していたファイル。</summary>
    public AnalysisTarget Unit { get; }

    /// <summary>実行時設定。</summary>
    public AnalyzerOptions Options { get; }

    /// <summary>キャンセル用トークン。</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>診断を報告する。</summary>
    /// <param name="diagnostic">報告する診断。</param>
    public void ReportDiagnostic(Diagnostic diagnostic) => _sink.ReportFrom(_owner, diagnostic);

    /// <summary>
    /// 指定範囲に診断を報告する。
    /// </summary>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="span">指摘する範囲。</param>
    /// <param name="messageArguments">メッセージ書式へ埋め込む引数。</param>
    public void ReportDiagnostic(DiagnosticDescriptor descriptor, TextSpan span, params object?[] messageArguments)
        => _sink.ReportFrom(_owner, Diagnostic.Create(descriptor, Location.Create(Unit.Text, span), messageArguments));
}
