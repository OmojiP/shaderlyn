using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Core.Analysis;

/// <summary>
/// 解析 1 回分の入力。1 つのシェーダーファイルとその構文木、字句・構文解析の段階で出た診断を保持する。
/// </summary>
/// <remarks>
/// <para>
/// 構文エラーがあっても解析は続行できなければならないため、<see cref="Root"/> は
/// 部分的に壊れた木であることを前提としてよい。パーサは欠落トークンを合成し、
/// 解釈不能な区間を読み飛ばしたうえで、可能な限り木を組み上げて返す。
/// </para>
/// <para>
/// <see cref="SyntaxDiagnostics"/> をアナライザの診断と分けて保持しているのは、
/// 構文エラーだけを先に報告して以降のルールを打ち切る、といった制御を
/// 呼び出し側で選べるようにするためである。構文が壊れたファイルに意味解析ルールを適用すると
/// 誤検出の山になるが、その判断はドライバではなく利用者の設定に委ねたい。
/// </para>
/// </remarks>
public sealed class AnalysisTarget
{
    private readonly ImmutableDictionary<Type, object> _models;

    /// <summary>
    /// 解析単位を生成する。
    /// </summary>
    /// <param name="text">対象のソーステキスト。</param>
    /// <param name="root">構文木の根。パースに至らなかった場合は <see langword="null"/>。</param>
    /// <param name="syntaxDiagnostics">字句・構文解析の段階で出た診断。</param>
    public AnalysisTarget(SourceText text, SyntaxNode? root, ImmutableArray<Diagnostic> syntaxDiagnostics)
        : this(text, root, syntaxDiagnostics, ImmutableDictionary<Type, object>.Empty)
    {
    }

    private AnalysisTarget(
        SourceText text,
        SyntaxNode? root,
        ImmutableArray<Diagnostic> syntaxDiagnostics,
        ImmutableDictionary<Type, object> models)
    {
        ArgumentNullException.ThrowIfNull(text);

        Text = text;
        Root = root;
        SyntaxDiagnostics = syntaxDiagnostics.IsDefault ? [] : syntaxDiagnostics;
        _models = models;
    }

    /// <summary>対象のソーステキスト。</summary>
    public SourceText Text { get; }

    /// <summary>
    /// 構文木の根。
    /// </summary>
    /// <remarks>
    /// パース自体が行われなかった場合は <see langword="null"/> になる。
    /// この場合ノードに登録されたアクションは一切実行されないが、
    /// <see cref="AnalysisContext.RegisterSyntaxTreeAction"/> で登録されたアクションは実行される。
    /// </remarks>
    public SyntaxNode? Root { get; }

    /// <summary>字句・構文解析の段階で出た診断。</summary>
    public ImmutableArray<Diagnostic> SyntaxDiagnostics { get; }

    /// <summary>対象ファイルのパス。</summary>
    public string FilePath => Text.FilePath;

    /// <summary>
    /// 意味解析の結果などの付随モデルを添えた複製を返す。
    /// </summary>
    /// <typeparam name="TModel">添えるモデルの型。取り出す際のキーになる。</typeparam>
    /// <param name="model">添えるモデル。</param>
    /// <returns>モデルを添えた新しい解析単位。</returns>
    /// <remarks>
    /// <b>これは層の依存方向を守るための仕組みである。</b>
    /// 依存方向は <c>Core ← ShaderLab / Hlsl ← Semantics ← Rules</c> の一方向であり、
    /// Core が意味解析層の型 (<c>ShaderCompilation</c> など) を直接知ることはできない。
    /// かといってルールが意味解析の結果を受け取れなければ、
    /// Properties と HLSL の対応のような横断的な検査が一切書けない。
    /// <para>
    /// そこで Core は「型をキーにした不透明な入れ物」だけを提供し、
    /// 中身が何であるかは詰める側 (CLI) と取り出す側 (ルール) だけが知る、という形にしている。
    /// 型そのものをキーにしているので、キー文字列のスペルミスは起こりえない。
    /// </para>
    /// </remarks>
    public AnalysisTarget WithModel<TModel>(TModel model)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(model);
        return new AnalysisTarget(Text, Root, SyntaxDiagnostics, _models.SetItem(typeof(TModel), model));
    }

    /// <summary>
    /// 添えられたモデルを取り出す。
    /// </summary>
    /// <typeparam name="TModel">取り出すモデルの型。</typeparam>
    /// <returns>添えられていればそのモデル。無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>ルールは <see langword="null"/> が返る場合を必ず扱わなければならない。</b>
    /// モデルの構築には include の解決など重いプリプロセスが必要であり、
    /// 呼び出し側の設定によっては用意されないことがある。
    /// モデルが無いときは「判断できない」のであって「問題が無い」のではないため、
    /// その場合は診断を出さずに何もしないのが正しい。
    /// </remarks>
    public TModel? GetModel<TModel>()
        where TModel : class
        => _models.TryGetValue(typeof(TModel), out object? model) ? (TModel)model : null;
}
