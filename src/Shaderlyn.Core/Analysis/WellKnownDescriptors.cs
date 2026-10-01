using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Core.Analysis;

/// <summary>
/// 解析基盤そのものが報告するルール定義。
/// </summary>
/// <remarks>
/// <c>TOOL</c> 接頭辞はツール自身の問題を表し、シェーダーコードの問題を表す
/// <c>SL</c> / <c>HL</c> / <c>URP</c> / <c>USER</c> とは区別する。
/// </remarks>
internal static class WellKnownDescriptors
{
    /// <summary>
    /// アナライザの実行中に予期しない例外が発生したことを表す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// アナライザが投げた例外は捕捉してこの診断に変換し、他のアナライザの実行は継続する。
    /// 1 つのルールの不具合で解析全体が停止すると、CI が丸ごと落ちて
    /// 他の正しい指摘まで届かなくなるためである。
    /// </para>
    /// <para>
    /// 既定の重要度を警告にしているのは、この診断がシェーダーコードの問題ではなく
    /// ツール側の問題であり、利用者のビルドを止める筋合いが無いためである。
    /// ただし何も伝えずに握り潰すと不具合が発見されないので、必ず表には出す。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor AnalyzerThrew { get; } = new(
        id: "TOOL0001",
        title: "アナライザの実行中に例外が発生しました",
        messageFormat: "アナライザ '{0}' の実行中に例外が発生しました: {1}",
        category: "Tool",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "解析ツール自身の不具合です。シェーダーコードの問題ではありません。"
            + "このメッセージが出た場合は、対象ファイルとあわせて不具合として報告してください。");

    /// <summary>
    /// ファイルの読み込みに失敗したことを表す。
    /// </summary>
    public static DiagnosticDescriptor FileReadFailed { get; } = new(
        id: "TOOL0002",
        title: "ファイルを読み込めませんでした",
        messageFormat: "ファイル '{0}' を読み込めませんでした: {1}",
        category: "Tool",
        defaultSeverity: DiagnosticSeverity.Error,
        description: "対象ファイルが存在しないか、アクセス権限が無いか、他プロセスが排他ロックしています。");

    /// <summary>
    /// 設定ファイルの内容に問題があることを表す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>設定ファイルの誤りを、何も伝えずに無視してはならない。</b>
    /// キーの綴りを間違えただけで、設定したつもりのルールが効かないまま運用される。
    /// 「指摘が出ない」ことを「問題が無い」と受け取られる状態は、
    /// 静的解析の導入で最も避けたい失敗である。
    /// </para>
    /// <para>
    /// 設定ファイル自身の位置を指して報告するので、
    /// 他の指摘と同じように行番号つきで表示される。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor ConfigurationProblem { get; } = new(
        id: "TOOL0003",
        title: "設定ファイルの内容に問題があります",
        messageFormat: "{0}",
        category: "Tool",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "設定ファイルに解釈できない記述があります。"
            + "その部分の設定は適用されていません。");

    /// <summary>
    /// 取り込むヘッダが 1 つも解決できないことを表す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unity のヘッダは、あるものとして扱う。</b>
    /// <c>Packages/com.unity.render-pipelines.universal/...</c> を引けない状態は、
    /// 「シェーダーの誤り」ではなく「解析の前提が揃っていない」状態である。
    /// その状態で出した指摘は、ヘッダの中身を知らないまま出した指摘であり、
    /// 出ないことも当てにならない。だから続けずにエラーにする。
    /// </para>
    /// <para>
    /// <b>1 行ずつ報告しない。</b>
    /// 環境が揃っていなければパッケージ配下のヘッダは軒並み引けず、
    /// <c>HL0321</c> を 1 行ずつ並べると出力が埋まって直し方が読めなくなる。
    /// 原因は 1 つなので、ファイルごとに 1 件、直し方とともに報告する。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor IncludesUnavailable { get; } = new(
        id: "TOOL0004",
        title: "取り込むヘッダを 1 つも解決できません",
        messageFormat: "取り込むヘッダを 1 つも解決できません ({0})。{1}",
        category: "Tool",
        defaultSeverity: DiagnosticSeverity.Error,
        description: "そのファイルが書いた #include を 1 つも解決できませんでした。"
            + "Unity のパッケージ (Packages/...) なら Unity プロジェクトのルートを指定し、"
            + "CI では Library/PackageCache がキャッシュされているかを確かめてください。"
            + "自前のヘッダなら、パスの書き方と探索パスを確かめてください。"
            + "ヘッダを引けないまま行った検査は、根拠を欠いたものになります。",
        helpLinkUri: DocumentationLinks.For("TOOL0004"));

    /// <summary>
    /// アナライザが、申告していないルールを報告したことを表す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>何も伝えずに通してはならない。</b>
    /// <see cref="DiagnosticAnalyzer.SupportedDiagnostics"/> の一覧は
    /// 「設定でこのルールを無効にできるか」「<c>--list-rules</c> に出るか」を決めている。
    /// 申告漏れのルールは<b>設定ファイルから無効にできず、一覧にも出ない</b>。
    /// 利用者から見ると「消し方の分からない指摘」になる。
    /// </para>
    /// <para>
    /// <b>報告そのものは通す。</b>
    /// 指摘の中身は正しいかもしれないので、捨てると本当の問題を隠すことになる。
    /// 指摘は出したうえで、申告漏れも別に報告する。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor UndeclaredRuleReported { get; } = new(
        id: "TOOL0005",
        title: "申告していないルールが報告されました",
        messageFormat:
            "アナライザ '{0}' がルール '{1}' を報告しましたが、SupportedDiagnostics に含まれていません。",
        category: "Tool",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "アナライザの実装の誤りです。"
            + "SupportedDiagnostics へ加えてください。"
            + "申告されていないルールは設定ファイルから無効にできず、--list-rules にも出ません。",
        helpLinkUri: DocumentationLinks.For("TOOL0005"));

    /// <summary>
    /// メッセージの書式と、渡された引数の数が食い違っていることを表す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>書式のまま出力されるのを、何も伝えずに見過ごさない。</b>
    /// 引数が足りないと <c>string.Format</c> は失敗し、
    /// 利用者の画面には <c>'{0}' は宣言されていません</c> のような
    /// <b>穴の空いたままのメッセージ</b>が出る。
    /// 出力としては成立してしまうので、テストが無ければ気づかれない。
    /// </para>
    /// <para>
    /// 実装の誤りなので、シェーダーの問題を表す重要度は与えない。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor MessageArgumentMismatch { get; } = new(
        id: "TOOL0006",
        title: "メッセージの引数の数が合っていません",
        messageFormat:
            "ルール '{0}' のメッセージは引数を {1} 個必要としますが、{2} 個渡されました。",
        category: "Tool",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "アナライザの実装の誤りです。"
            + "書式の穴の数と、Diagnostic.Create へ渡す引数の数を合わせてください。"
            + "合っていないと、書式指定子が埋まらないまま利用者へ表示されます。",
        helpLinkUri: DocumentationLinks.For("TOOL0006"));
}
