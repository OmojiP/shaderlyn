using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Semantics.Analysis;

/// <summary>
/// 埋め込み HLSL のノードを見るルールの基底クラス。
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="AnalysisContext.RegisterNodeAction{TNode}"/> が歩くのは
/// ShaderLab の構文木だけである。</b>
/// 埋め込み HLSL はコードブロックごとに別の構文木になっており、
/// マクロ展開と <c>#include</c> の解決を経て初めて出来上がる。
/// ShaderLab の木からは辿れない。
/// </para>
/// <para>
/// この基底クラスは、その間を埋める。
/// 派生クラスは <see cref="InitializeHlsl"/> で HLSL のノード型に対して登録するだけでよい。
/// </para>
/// <example>
/// <code>
/// public sealed class NoHalfAnalyzer : HlslRuleAnalyzer
/// {
///     public override ImmutableArray&lt;DiagnosticDescriptor&gt; SupportedDiagnostics =&gt; [Rule];
///
///     protected override void InitializeHlsl(HlslAnalysisContext context)
///         =&gt; context.RegisterNodeAction&lt;HlslTypeSyntax&gt;(c =&gt;
///         {
///             if (c.Node.Name == "half4")
///             {
///                 c.ReportDiagnostic(Rule, c.Node.Name);
///             }
///         });
/// }
/// </code>
/// </example>
/// <para>
/// <b>歩くのは、そのシェーダーが自分で書いたコードだけである。</b>
/// 取り込んだヘッダの中身は歩かない。他人のコードを指摘しても直しようがなく、
/// URP のヘッダは 1 ブロックあたり 1 万ノードを超えるため、
/// 歩くだけで解析時間の大半を使ってしまう。
/// </para>
/// <para>
/// <b>ノードは、それが属する構成の文脈で渡ってくる。</b>
/// 既定の構成の木に加えて、キーワードを有効にした構成 (バリアント) の木も歩く
/// (<see cref="ShaderCompilation.EnumerateRuleNodes"/>)。
/// <see cref="HlslNodeAnalysisContext{TNode}.Program"/> はそのノードが属する木であり、
/// そこから <see cref="ShaderCompilation.GetExpressionTypeBinder"/> を引けば、その構成の型が得られる。
/// 形は同じでも構成によって型が変わるコード (マクロの中身を切り替えている場合など) を、構成ごとに検査できる。
/// </para>
/// <para>
/// <b>同じ位置のコードは、Pass と構成の数だけ渡ってくる。</b>
/// <c>HLSLINCLUDE</c> に書いたコードは各 Pass の木に現れ、どのコードも各構成の木に現れる。
/// Pass や構成が違えば、同じ位置でも意味 (有効なマクロや型) が違いうるので、それぞれの文脈で渡す。
/// 渡ってきた回数を数えて集計するルールは、この重複を自分で除くこと。
/// </para>
/// <para>
/// <b>同じルール・同じ位置・同じ引数の報告は 1 度にまとめる。</b>
/// どの Pass・どの構成でも同じ誤りなら、その数だけ並べない。
/// 文面が変わる報告 (構成ごとの型の名前を載せるなど) は、それぞれ残る。
/// </para>
/// <para>
/// そのノードが存在する条件は <see cref="HlslNodeAnalysisContext{TNode}.Condition"/> で分かる。
/// </para>
/// </remarks>
public abstract class HlslRuleAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    /// <remarks>
    /// <b>派生クラスはこれを上書きしない。</b>
    /// 上書きすると、セマンティックモデルが無い場合に報告しない仕組みが働かなくなる。
    /// 登録は <see cref="InitializeHlsl"/> で行うこと。
    /// </remarks>
    public sealed override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HlslAnalysisContext registrations = new();
        InitializeHlsl(registrations);

        if (registrations.IsEmpty)
        {
            return;
        }

        context.RegisterSyntaxTreeAction(treeContext =>
        {
            // セマンティックモデルが無いのは「問題が無い」ではなく「調べていない」である。
            // 報告しないのが正しい。
            if (treeContext.Unit.GetModel<ShaderCompilation>() is not { } compilation)
            {
                return;
            }

            Walk(registrations, compilation, treeContext);
        });
    }

    /// <summary>
    /// HLSL のノードに対する関心を登録する。
    /// </summary>
    /// <param name="context">登録先。</param>
    /// <remarks>
    /// <see cref="Initialize"/> と同じく、ドライバの生成時に一度だけ呼ばれる。
    /// ファイルごとには呼ばれない。
    /// </remarks>
    protected abstract void InitializeHlsl(HlslAnalysisContext context);

    /// <summary>
    /// 自分で書いたコードのノードを歩き、登録されたアクションを発火させる。
    /// </summary>
    /// <param name="registrations">登録内容。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="treeContext">報告先。</param>
    private void Walk(
        HlslAnalysisContext registrations,
        ShaderCompilation compilation,
        SyntaxTreeAnalysisContext treeContext)
    {
        // 同じ報告を 2 度出さないための記録。
        // HLSLINCLUDE のコードは各 Pass の木に、どのコードも各構成の木に現れる。
        // 歩くのはすべての木で、それぞれの文脈で渡す。Pass や構成が違えば意味が違いうるためである。
        // まとめるのは報告のほうにする。
        HashSet<(string Id, string FilePath, TextSpan Span, string Arguments)> reported = [];

        foreach ((HlslSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            registrations.Invoke(this, node, compilation, program, treeContext, reported);
        }
    }
}

/// <summary>
/// HLSL のノードに対するアクションを登録するためのコンテキスト。
/// </summary>
/// <remarks>
/// <see cref="AnalysisContext"/> と同じ考え方で、
/// 何個登録しても構文木の走査は 1 回のままである。
/// </remarks>
public sealed class HlslAnalysisContext
{
    private readonly List<(Type NodeType, Action<HlslSyntaxNode, ShaderCompilation, AnalyzedProgram, SyntaxTreeAnalysisContext, HashSet<(string Id, string FilePath, TextSpan Span, string Arguments)>> Invoke)> _actions = [];

    /// <summary>登録が 1 つも無いかどうか。</summary>
    internal bool IsEmpty => _actions.Count == 0;

    /// <summary>
    /// 指定した型の HLSL ノードに対して実行するアクションを登録する。
    /// </summary>
    /// <typeparam name="TNode">関心のあるノードの型。</typeparam>
    /// <param name="action">ノードごとに呼ばれるアクション。</param>
    /// <remarks>
    /// 基底型を指定すると、それに代入可能なすべての派生ノードで発火する。
    /// 式の基底型を指定すれば、二項演算・関数呼び出しなど全ての式で呼ばれる。
    /// </remarks>
    public void RegisterNodeAction<TNode>(Action<HlslNodeAnalysisContext<TNode>> action)
        where TNode : HlslSyntaxNode
    {
        ArgumentNullException.ThrowIfNull(action);

        _actions.Add((
            typeof(TNode),
            (node, compilation, program, treeContext, reported) =>
                action(new HlslNodeAnalysisContext<TNode>((TNode)node, compilation, program, treeContext, reported))));
    }

    /// <summary>登録されたアクションのうち、そのノードで発火すべきものを呼ぶ。</summary>
    /// <param name="owner">呼び出し元のアナライザ。</param>
    /// <param name="node">対象のノード。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">そのノードが属するコードブロック。</param>
    /// <param name="treeContext">報告先。</param>
    /// <param name="reported">報告済みの内容。同じ報告を構成の数だけ並べないために使う。</param>
    internal void Invoke(
        HlslRuleAnalyzer owner,
        HlslSyntaxNode node,
        ShaderCompilation compilation,
        AnalyzedProgram program,
        SyntaxTreeAnalysisContext treeContext,
        HashSet<(string Id, string FilePath, TextSpan Span, string Arguments)> reported)
    {
        ArgumentNullException.ThrowIfNull(owner);

        Type nodeType = node.GetType();

        foreach ((Type registered, var invoke) in _actions)
        {
            if (registered.IsAssignableFrom(nodeType))
            {
                invoke(node, compilation, program, treeContext, reported);
            }
        }
    }
}

/// <summary>
/// HLSL のノード 1 つに対するアクションへ渡されるコンテキスト。
/// </summary>
/// <typeparam name="TNode">対象ノードの型。</typeparam>
public readonly struct HlslNodeAnalysisContext<TNode>
    where TNode : HlslSyntaxNode
{
    private readonly SyntaxTreeAnalysisContext _treeContext;
    private readonly HashSet<(string Id, string FilePath, TextSpan Span, string Arguments)> _reported;

    internal HlslNodeAnalysisContext(
        TNode node,
        ShaderCompilation compilation,
        AnalyzedProgram program,
        SyntaxTreeAnalysisContext treeContext,
        HashSet<(string Id, string FilePath, TextSpan Span, string Arguments)> reported)
    {
        Node = node;
        Compilation = compilation;
        Program = program;
        _treeContext = treeContext;
        _reported = reported;
    }

    /// <summary>検査対象のノード。</summary>
    public TNode Node { get; }

    /// <summary>対象シェーダーのセマンティックモデル。</summary>
    public ShaderCompilation Compilation { get; }

    /// <summary>そのノードが属するコードブロック。</summary>
    /// <remarks>
    /// <para>
    /// <c>#pragma</c> や、そのブロックから見える宣言を引くのに使う。
    /// </para>
    /// <para>
    /// 既定の構成の木 (<see cref="ShaderCompilation.Programs"/>) か、
    /// キーワードを有効にした構成の木 (<see cref="ShaderCompilation.SymbolVariants"/>) のどちらかである。
    /// 型を求めるときは、ここから <see cref="ShaderCompilation.GetExpressionTypeBinder"/> を引く。
    /// </para>
    /// </remarks>
    public AnalyzedProgram Program { get; }

    /// <summary>そのノードが存在する条件。</summary>
    /// <remarks>
    /// その木を解析した構成も掛け合わせてある (<see cref="ShaderCompilation.GetEffectiveCondition"/>)。
    /// <see cref="SymbolCondition.IsUnknown"/> なら、条件を追えなかったので、それを根拠に報告しないこと。
    /// </remarks>
    public SymbolCondition Condition => Compilation.GetEffectiveCondition(Node, Program);

    /// <summary>実行時設定。</summary>
    public AnalyzerOptions Options => _treeContext.Options;

    /// <summary>キャンセル用トークン。</summary>
    public CancellationToken CancellationToken => _treeContext.CancellationToken;

    /// <summary>
    /// 検査対象ノードの位置に診断を報告する。
    /// </summary>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="messageArguments">メッセージ書式へ埋め込む引数。</param>
    public void ReportDiagnostic(DiagnosticDescriptor descriptor, params object?[] messageArguments)
        => ReportDiagnostic(descriptor, Node.Span, messageArguments);

    /// <summary>
    /// ノード内の指定範囲に診断を報告する。
    /// </summary>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="span">指摘する範囲。</param>
    /// <param name="messageArguments">メッセージ書式へ埋め込む引数。</param>
    /// <remarks>
    /// <para>
    /// ノード全体ではなく識別子だけを指したい場合に使う。
    /// 波線が広すぎる指摘は読み手が問題箇所を特定できない。
    /// </para>
    /// <para>
    /// 同じルール・同じファイルの同じ範囲・同じ引数の報告は、構成の数だけ呼ばれても 1 度しか出ない。
    /// </para>
    /// </remarks>
    public void ReportDiagnostic(
        DiagnosticDescriptor descriptor,
        TextSpan span,
        params object?[] messageArguments)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        string arguments = string.Join('\u0001', messageArguments.Select(a => a?.ToString() ?? string.Empty));

        // 範囲はノードを書いたファイルの中の位置である。利用者が書いて取り込んだヘッダのノードもここへ来る。
        // 解析しているファイルの位置は、元のテキスト (マスクする前) に付ける。抑制コメントと周辺の表示はそちらにある。
        SourceText source = Node.FirstToken?.Source ?? _treeContext.Unit.Text;
        bool own = string.Equals(source.FilePath, _treeContext.Unit.FilePath, StringComparison.Ordinal);

        if (!_reported.Add((descriptor.Id, source.FilePath, span, arguments)))
        {
            return;
        }

        if (own)
        {
            _treeContext.ReportDiagnostic(descriptor, span, messageArguments);
        }
        else
        {
            _treeContext.ReportDiagnostic(Diagnostic.Create(descriptor, Location.Create(source, span), messageArguments));
        }
    }
}
