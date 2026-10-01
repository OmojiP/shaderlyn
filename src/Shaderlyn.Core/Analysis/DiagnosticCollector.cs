using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Core.Analysis;

/// <summary>
/// アナライザが報告した診断を受け取り、設定による重要度の上書きを適用して蓄積する。
/// </summary>
/// <remarks>
/// <para>
/// 重要度の上書きをこの一点に集約しているのが要点である。
/// ルール実装は常に <c>Diagnostic.Create(descriptor, location, args)</c> と書けばよく、
/// 設定ファイルを意識する必要がない。設定の適用漏れというルールごとのバグが
/// 構造的に起こりえなくなる。
/// </para>
/// <para>
/// 無効化されたルール (実効重要度が <see cref="DiagnosticSeverity.None"/>) の診断は
/// ここで捨てられる。ただしドライバはそもそもアナライザ自体を実行前に除外するため、
/// ここへ到達するのは「1 つのアナライザが複数ルールを報告し、その一部だけが無効」という場合に限られる。
/// </para>
/// </remarks>
internal sealed class DiagnosticCollector
{
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
    private readonly AnalyzerOptions _options;

    /// <summary>実装の誤りを既に報告した組み合わせ。同じことを何度も言わないために覚える。</summary>
    private readonly HashSet<(string Analyzer, string Rule, string Kind)> _reportedMistakes = [];

    /// <summary>診断の受け皿を生成する。</summary>
    /// <param name="options">重要度の上書きを含む実行時設定。</param>
    public DiagnosticCollector(AnalyzerOptions options) => _options = options;

    /// <summary>
    /// アナライザからの報告を 1 件受け取り、実装の誤りがあれば併せて報告する。
    /// </summary>
    /// <param name="owner">報告したアナライザ。</param>
    /// <param name="diagnostic">報告された診断。</param>
    /// <remarks>
    /// <para>
    /// <b>実装の誤りは、何も伝えずに直すことも捨てることもしない。</b>
    /// 指摘そのものは通したうえで、誤りを別の診断として並べる。
    /// 捨てると本当の問題が隠れ、何も伝えずに通すと誤りが発見されない。
    /// </para>
    /// <para>
    /// 見るのは 2 つである。どちらも Roslyn では、実行時に壊れても何も知らせない種類のものである。
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <see cref="DiagnosticAnalyzer.SupportedDiagnostics"/> に申告していないルールを報告した
    ///     (<c>TOOL0005</c>)。申告漏れは「設定から無効にできない指摘」になる
    ///   </description></item>
    ///   <item><description>
    ///     メッセージの引数の数が書式と合っていない
    ///     (<c>TOOL0006</c>)。穴が埋まらないまま利用者へ表示される
    ///   </description></item>
    /// </list>
    /// <para>
    /// 同じ誤りは 1 ファイルにつき 1 度だけ報告する。
    /// ノードごとに報告するルールで誤ると、出力が同じ行で埋まって元の指摘が読めなくなる。
    /// </para>
    /// </remarks>
    public void ReportFrom(DiagnosticAnalyzer owner, Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(diagnostic);

        VerifyDeclared(owner, diagnostic);
        VerifyMessageArguments(owner, diagnostic);

        Report(diagnostic);
    }

    /// <summary>
    /// 診断を 1 件受け取る。
    /// </summary>
    /// <param name="diagnostic">報告された診断。</param>
    /// <remarks>
    /// 報告元が重要度を明示している場合 (<see cref="Diagnostic.IsSeverityExplicit"/>) は
    /// それを尊重する。設定ファイルで 1 件ずつ重要度を指定できるルールのためである。
    /// ただし<b>ルールの無効化は常に優先される</b>。
    /// 無効化はその指摘を見たくないという意思表示であり、
    /// 個別の重要度指定で覆されてはならない。
    /// </remarks>
    public void Report(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        DiagnosticSeverity severity = _options.GetEffectiveSeverity(diagnostic.Descriptor);
        if (severity == DiagnosticSeverity.None)
        {
            return;
        }

        _diagnostics.Add(diagnostic.IsSeverityExplicit ? diagnostic : diagnostic.WithSeverity(severity));
    }

    /// <summary>申告されていないルールを報告していないかを確かめる。</summary>
    /// <param name="owner">報告したアナライザ。</param>
    /// <param name="diagnostic">報告された診断。</param>
    private void VerifyDeclared(DiagnosticAnalyzer owner, Diagnostic diagnostic)
    {
        foreach (DiagnosticDescriptor declared in owner.SupportedDiagnostics)
        {
            if (string.Equals(declared.Id, diagnostic.Id, StringComparison.Ordinal))
            {
                return;
            }
        }

        ReportMistake(
            owner,
            diagnostic,
            kind: "undeclared",
            WellKnownDescriptors.UndeclaredRuleReported,
            owner.GetType().Name,
            diagnostic.Id);
    }

    /// <summary>メッセージの引数の数が書式と合っているかを確かめる。</summary>
    /// <param name="owner">報告したアナライザ。</param>
    /// <param name="diagnostic">報告された診断。</param>
    /// <remarks>
    /// 多い分には書式が埋まるので害が無い。足りない場合だけを見る。
    /// </remarks>
    private void VerifyMessageArguments(DiagnosticAnalyzer owner, Diagnostic diagnostic)
    {
        int required = diagnostic.Descriptor.RequiredMessageArgumentCount;

        if (diagnostic.MessageArgumentCount >= required)
        {
            return;
        }

        ReportMistake(
            owner,
            diagnostic,
            kind: "arguments",
            WellKnownDescriptors.MessageArgumentMismatch,
            diagnostic.Id,
            required,
            diagnostic.MessageArgumentCount);
    }

    /// <summary>実装の誤りを、同じものは 1 度だけ報告する。</summary>
    /// <param name="owner">報告したアナライザ。</param>
    /// <param name="diagnostic">元の診断。位置を借りる。</param>
    /// <param name="kind">誤りの種類。同じ組み合わせを 2 度報告しないための鍵に使う。</param>
    /// <param name="descriptor">報告するルール。</param>
    /// <param name="messageArguments">メッセージ書式へ埋め込む引数。</param>
    private void ReportMistake(
        DiagnosticAnalyzer owner,
        Diagnostic diagnostic,
        string kind,
        DiagnosticDescriptor descriptor,
        params object?[] messageArguments)
    {
        if (!_reportedMistakes.Add((owner.GetType().Name, diagnostic.Id, kind)))
        {
            return;
        }

        Report(Diagnostic.Create(descriptor, diagnostic.Location, messageArguments));
    }

    /// <summary>
    /// 蓄積された診断を、ソースコード上の出現順に並べて返す。
    /// </summary>
    /// <returns>整列済みの診断の配列。</returns>
    /// <remarks>
    /// 出力順が実行ごとに揺れるとゴールデンテストが不安定になり、
    /// SARIF の差分も無意味に膨らむため、ここで決定的な順序を与える。
    /// </remarks>
    public ImmutableArray<Diagnostic> ToSortedArray()
    {
        _diagnostics.Sort(Diagnostic.DocumentOrderComparer);
        return _diagnostics.ToImmutable();
    }
}
