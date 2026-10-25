using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

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
/// 派生クラスは <see cref="InitializeShader"/> で HLSL のノード型に対して登録するだけでよい。
/// </para>
/// <example>
/// <code>
/// public sealed class NoHalfAnalyzer : ShaderRuleAnalyzer
/// {
///     public override ImmutableArray&lt;DiagnosticDescriptor&gt; SupportedDiagnostics =&gt; [Rule];
///
///     protected override void InitializeShader(ShaderAnalysisContext context)
///         =&gt; context.RegisterNodeAction&lt;TypeSyntax&gt;(c =&gt;
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
/// (<see cref="ShaderCompilation.EnumerateRuleNodes()"/>)。
/// <see cref="ShaderNodeAnalysisContext{TNode}.Program"/> はそのノードが属する木であり、
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
/// そのノードが存在する条件は <see cref="ShaderNodeAnalysisContext{TNode}.Condition"/> で分かる。
/// 構成の条件が付く報告は <see cref="ShaderNodeAnalysisContext{TNode}.ReportWhen(SymbolCondition, DiagnosticDescriptor, object?[])"/> で出す。
/// 木の構成を掛け、ありえない構成を除き、同じ位置の報告は Pass と構成をまたいで条件の和をとって 1 件にする
/// (<see cref="ConditionalReports"/>)。
/// </para>
/// </remarks>
public abstract class ShaderRuleAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    /// <remarks>
    /// <b>派生クラスはこれを上書きしない。</b>
    /// 上書きすると、セマンティックモデルが無い場合に報告しない仕組みが働かなくなる。
    /// 登録は <see cref="InitializeShader"/> で行うこと。
    /// </remarks>
    public sealed override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ShaderAnalysisContext registrations = new();
        InitializeShader(registrations);

        if (registrations.IsEmpty)
        {
            return;
        }

        context.RegisterSyntaxTreeAction(treeContext =>
        {
            // セマンティックモデルが無いのは「問題が無い」ではなく「調べていない」である。
            // 報告しないのが正しい。
            if (treeContext.Unit.GetModel<ShaderCompilation>() is not { } compilation
                || (compilation.IsGlsl && !AnalyzesGlsl))
            {
                return;
            }

            Walk(registrations, compilation, treeContext);
        });
    }

    /// <summary>
    /// GLSL のモデルも調べるか。既定は <see langword="false"/> (HLSL のモデルだけを調べる)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>HLSL を前提に書いたルールを、GLSL に当てない。</b>
    /// GLSL は型の名前も組み込み関数も違うので、HLSL のつもりの判断は誤検出になる。
    /// 組み込みのルールは、GLSL で確かめたものだけがこれを立てる。
    /// </para>
    /// <para>
    /// 自作のルールからはまだ立てられない。GLSL のモデルのどこを公開するかは、GLSL の検査ができてから決める。
    /// </para>
    /// </remarks>
    internal virtual bool AnalyzesGlsl => false;

    /// <summary>
    /// HLSL のノードに対する関心を登録する。
    /// </summary>
    /// <param name="context">登録先。</param>
    /// <remarks>
    /// <see cref="Initialize"/> と同じく、ドライバの生成時に一度だけ呼ばれる。
    /// ファイルごとには呼ばれない。
    /// </remarks>
    protected abstract void InitializeShader(ShaderAnalysisContext context);

    /// <summary>
    /// 自分で書いたコードのノードを歩き、登録されたアクションを発火させる。
    /// </summary>
    /// <param name="registrations">登録内容。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="treeContext">報告先。</param>
    private void Walk(
        ShaderAnalysisContext registrations,
        ShaderCompilation compilation,
        SyntaxTreeAnalysisContext treeContext)
    {
        // 同じ報告を 2 度出さないための記録。
        // HLSLINCLUDE のコードは各 Pass の木に、どのコードも各構成の木に現れる。
        // 歩くのはすべての木で、それぞれの文脈で渡す。Pass や構成が違えば意味が違いうるためである。
        // まとめるのは報告のほうにする。
        HashSet<(string Id, string FilePath, TextSpan Span, string Arguments)> reported = [];
        ConditionalReports conditional = new(compilation);

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            registrations.Invoke(this, node, compilation, program, treeContext, reported, conditional);
        }

        // 条件付きの報告は、すべての木を歩いてから出す。同じ位置の報告の条件の和をとるためである。
        // 解析しているファイルの位置は、元のテキスト (マスクする前) に付ける。抑制コメントと周辺の表示はそちらにある。
        foreach (Diagnostic diagnostic in conditional.Build())
        {
            treeContext.ReportDiagnostic(
                string.Equals(diagnostic.Location.FilePath, treeContext.Unit.FilePath, StringComparison.Ordinal)
                    ? diagnostic.WithLocation(Location.Create(treeContext.Unit.Text, diagnostic.Location.Span))
                    : diagnostic);
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
public sealed class ShaderAnalysisContext
{
    private readonly List<(Type NodeType, NodeActionOptions? Options, Action<ShaderSyntaxNode, ShaderCompilation, AnalyzedProgram, SyntaxTreeAnalysisContext, HashSet<(string Id, string FilePath, TextSpan Span, string Arguments)>, ConditionalReports, SymbolCondition> Invoke)> _actions = [];

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
    public void RegisterNodeAction<TNode>(Action<ShaderNodeAnalysisContext<TNode>> action)
        where TNode : ShaderSyntaxNode
        => RegisterNodeAction(action, null);

    /// <summary>
    /// 指定した型の HLSL ノードのうち、指定に合うものに対して実行するアクションを登録する。
    /// </summary>
    /// <typeparam name="TNode">関心のあるノードの型。</typeparam>
    /// <param name="action">ノードごとに呼ばれるアクション。</param>
    /// <param name="options">どのノードで呼ぶか (エントリポイントから届く関数の中だけ、ある構成で存在しうるノードだけ)。<see langword="null"/> ならすべて。</param>
    /// <example>
    /// <code>
    /// context.RegisterNodeAction&lt;InvocationExpressionSyntax&gt;(Check,
    ///     new NodeActionOptions { ReachableFromEntryPoints = true, When = "defined(SHADER_API_MOBILE)" });
    /// </code>
    /// </example>
    public void RegisterNodeAction<TNode>(Action<ShaderNodeAnalysisContext<TNode>> action, NodeActionOptions? options)
        where TNode : ShaderSyntaxNode
    {
        ArgumentNullException.ThrowIfNull(action);

        _actions.Add((
            typeof(TNode),
            options,
            (node, compilation, program, treeContext, reported, conditional, when) =>
                action(new ShaderNodeAnalysisContext<TNode>((TNode)node, compilation, program, treeContext, reported, conditional, when))));
    }

    /// <summary>登録されたアクションのうち、そのノードで発火すべきものを呼ぶ。</summary>
    /// <param name="owner">呼び出し元のアナライザ。</param>
    /// <param name="node">対象のノード。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">そのノードが属するコードブロック。</param>
    /// <param name="treeContext">報告先。</param>
    /// <param name="reported">報告済みの内容。同じ報告を構成の数だけ並べないために使う。</param>
    /// <param name="conditional">条件付きの報告を集める先。</param>
    internal void Invoke(
        ShaderRuleAnalyzer owner,
        ShaderSyntaxNode node,
        ShaderCompilation compilation,
        AnalyzedProgram program,
        SyntaxTreeAnalysisContext treeContext,
        HashSet<(string Id, string FilePath, TextSpan Span, string Arguments)> reported,
        ConditionalReports conditional)
    {
        ArgumentNullException.ThrowIfNull(owner);

        Type nodeType = node.GetType();

        foreach ((Type registered, NodeActionOptions? options, var invoke) in _actions)
        {
            if (registered.IsAssignableFrom(nodeType) && Selects(options, node, compilation, program, out SymbolCondition when))
            {
                invoke(node, compilation, program, treeContext, reported, conditional, when);
            }
        }
    }

    /// <summary>指定に合うノードかを判定し、合えば <see cref="NodeActionOptions.When"/> の条件を返す。</summary>
    /// <param name="options">指定。</param>
    /// <param name="node">対象のノード。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">そのノードが属する木。</param>
    /// <param name="when">指定した構成の条件。指定が無ければ <see cref="SymbolCondition.Always"/>。</param>
    /// <returns>アクションを呼ぶなら <see langword="true"/>。</returns>
    private static bool Selects(NodeActionOptions? options, ShaderSyntaxNode node, ShaderCompilation compilation, AnalyzedProgram program, out SymbolCondition when)
    {
        when = SymbolCondition.Always;

        if (options is null)
        {
            return true;
        }

        // 関数の外のノードは選ばない (グローバルの宣言は、届くかどうかに関わらない)。
        if (options.ReachableFromEntryPoints
            && node.Ancestors().OfType<FunctionDeclarationSyntax>().LastOrDefault() is { } function
            && compilation.GetReachableFunctions(program) is { } reachable
            && !reachable.Contains(function))
        {
            return false;
        }

        if (options.When is { } condition)
        {
            when = condition.Resolve(program.Variants.ValueSymbols);
            SymbolCondition present = compilation.GetEffectiveCondition(node, program)
                .And(compilation.GetTreeConfiguration(program))
                .And(when);

            if (present.IsUnknown || !compilation.IsPossibleIn(program, present))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// HLSL のノード 1 つに対するアクションへ渡されるコンテキスト。
/// </summary>
/// <typeparam name="TNode">対象ノードの型。</typeparam>
public readonly struct ShaderNodeAnalysisContext<TNode>
    where TNode : ShaderSyntaxNode
{
    private readonly SyntaxTreeAnalysisContext _treeContext;
    private readonly HashSet<(string Id, string FilePath, TextSpan Span, string Arguments)> _reported;
    private readonly ConditionalReports _conditional;
    private readonly SymbolCondition _when;

    internal ShaderNodeAnalysisContext(
        TNode node,
        ShaderCompilation compilation,
        AnalyzedProgram program,
        SyntaxTreeAnalysisContext treeContext,
        HashSet<(string Id, string FilePath, TextSpan Span, string Arguments)> reported,
        ConditionalReports conditional,
        SymbolCondition when)
    {
        Node = node;
        Compilation = compilation;
        Program = program;
        _treeContext = treeContext;
        _reported = reported;
        _conditional = conditional;
        _when = when;
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
    /// 構成の条件が付く報告を、検査対象ノードの位置に出す。メッセージの <c>{0}</c> に「構成：○○ のとき、」(どの構成でもなら空) が入る。
    /// </summary>
    /// <param name="when">報告する構成。ノードがあればどの構成でも報告するなら <see cref="SymbolCondition.Always"/>。</param>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="messageArguments">メッセージの <c>{1}</c> 以降に入る引数。</param>
    /// <remarks>
    /// ノードの条件、木が表す構成、<see cref="NodeActionOptions.When"/> を掛け、ありえない構成を除いて報告する。
    /// 同じ位置・同じ引数の報告は、Pass と構成をまたいで条件の和をとって 1 件にし、解析の終わりに出す (<see cref="ConditionalReports"/>)。
    /// 条件を追えなかったときは報告しない。
    /// </remarks>
    public void ReportWhen(SymbolCondition when, DiagnosticDescriptor descriptor, params object?[] messageArguments)
        => ReportWhen(when, descriptor, Node.Span, messageArguments);

    /// <summary>
    /// 構成の条件が付く報告を、ノード内の指定範囲に出す (<see cref="ReportWhen(SymbolCondition, DiagnosticDescriptor, object?[])"/>)。
    /// </summary>
    /// <param name="when">報告する構成。</param>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="span">指摘する範囲。</param>
    /// <param name="messageArguments">メッセージの <c>{1}</c> 以降に入る引数。</param>
    public void ReportWhen(SymbolCondition when, DiagnosticDescriptor descriptor, TextSpan span, params object?[] messageArguments)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        SourceText source = Node.FirstToken?.Source ?? _treeContext.Unit.Text;
        _conditional.Add(Program, Node, when.And(_when), descriptor, Location.Create(source, span), messageArguments);
    }

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
