using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Shaderlyn.Cli;
using Shaderlyn.Cli.Inspection;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.LanguageServer.Protocol;
using Shaderlyn.Semantics;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// エディタからの要求を受け取り、診断を返し続ける。
/// </summary>
/// <remarks>
/// <para>
/// <b>入力のたびに走らせる段と、保存時に走らせる段を分けている</b>。
/// ShaderLab 単体の検査は 0〜2ms で終わるが、
/// 意味解析を伴う検査は Pass の数に比例して数百 ms かかる。
/// </para>
/// <para>
/// <b>打っている最中に、前回の意味解析の結果を残さない。</b>
/// 残せば「打っている最中だけ指摘が消える」ちらつきは避けられるが、
/// 残した指摘の位置は編集によってずれている。
/// 位置のずれた指摘は、指摘が消えるより悪い。
/// </para>
/// </remarks>
public sealed partial class ShaderLanguageServer
{
    private readonly LspConnection _connection;
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);

    /// <summary>
    /// 開いている文書ごとの、最後に組み立てたセマンティックモデル。指摘・効いていない範囲・カーソルの下を答えるのに共有する。
    /// 受信のループだけが読み書きする (組み立てと使用は <see cref="DocumentModel"/> が受信のループの外で行う)。
    /// </summary>
    private readonly Dictionary<string, DocumentModel> _models = new(StringComparer.Ordinal);

    /// <summary>
    /// ファイルごとに、利用者が定義済みにしたシンボル。
    /// </summary>
    /// <remarks>
    /// <b>ファイルを閉じても消さない。</b>
    /// 消すと、同じファイルを開き直すたびに選び直させることになる。
    /// サーバごと起動し直したときは、エディタ側が覚えておいたものを送り直す。
    /// </remarks>
    private readonly Dictionary<string, ImmutableArray<string>> _definedSymbols =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 「効いていない範囲」を、利用者が選んだ構成で表示するファイル。
    /// </summary>
    /// <remarks>
    /// 含まれないファイルは、すべての構成を合わせて表示する。
    /// <see cref="_definedSymbols"/> と同じく、ファイルを閉じても消さない。
    /// </remarks>
    private readonly HashSet<string> _selectedConfigurationDisplays = new(StringComparer.Ordinal);

    /// <summary>選んだ構成で表示することを表す、<c>display</c> の値。</summary>
    private const string SelectedConfigurationDisplay = "selectedConfiguration";

    /// <summary>すべての構成を合わせて表示することを表す、<c>display</c> の値。</summary>
    private const string AllConfigurationsDisplay = "allConfigurations";

    /// <summary><c>window/logMessage</c> の <c>type</c> で、誤りを表す値 (LSP の <c>MessageType.Error</c>)。</summary>
    private const int LogMessageTypeError = 1;

    /// <summary>window/logMessage の記録 (Log)。</summary>
    private const int LogMessageTypeLog = 4;

    /// <summary><c>window/showMessage</c> の <c>type</c> で、警告を表す値 (LSP の <c>MessageType.Warning</c>)。</summary>
    private const int MessageTypeWarning = 2;

    /// <summary>古くなっていないかを確かめた、Unreal の生成物のフォルダー (<see cref="CheckUnrealGeneration"/>)。</summary>
    private readonly HashSet<string> _checkedGeneratedDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 直近に送った指摘。修正の提示に使う。
    /// </summary>
    /// <remarks>
    /// 修正を求められたときに解析し直すと、
    /// 画面に出ている指摘と食い違う修正を出しうる。送ったものをそのまま覚えておく。
    /// </remarks>
    private readonly Dictionary<string, ImmutableArray<Diagnostic>> _lastDiagnostics =
        new(StringComparer.Ordinal);

    /// <summary>
    /// スクリプトごとの、そのスクリプト自身を指す指摘。そのスクリプトの解析で出たものである。
    /// </summary>
    private readonly Dictionary<string, ImmutableArray<Diagnostic>> _ownDiagnostics =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 利用者が書いたヘッダの URI ごとの、それを取り込むスクリプトの解析で出た指摘。取り込むスクリプトの URI で分けて持つ。
    /// </summary>
    /// <remarks>
    /// 共通の <c>.hlsl</c> の指摘は、取り込む <c>.shader</c> の文脈で見つかる。
    /// 同じヘッダを開いている複数のスクリプトが取り込んでいれば、それぞれの分を合わせて送る。
    /// </remarks>
    private readonly Dictionary<string, Dictionary<string, ImmutableArray<Diagnostic>>> _includedDiagnostics =
        new(StringComparer.Ordinal);

    /// <summary>指摘の記録 (<see cref="_lastDiagnostics"/> ほか) を守る。解析は別のスレッドで終わる。</summary>
    private readonly Lock _diagnosticsGate = new();
    /// <summary>実行するアナライザ。ワークスペースが決まるたびに新しい設定へ渡す。</summary>
    private readonly ImmutableArray<DiagnosticAnalyzer> _analyzers;

    /// <summary>自前のホストが足した環境。</summary>
    private readonly ImmutableArray<HostEnvironment> _environments;

    /// <summary>設定ファイルが見つからないファイルに使う設定。ワークスペースのルートから作る。</summary>
    private AnalysisSession _session;

    /// <summary>設定ファイルごとの設定。鍵は設定ファイルのパスで、書き換えられたら作り直す。</summary>
    private readonly ConcurrentDictionary<string, (DateTime Written, AnalysisSession Session)> _sessionsByConfiguration =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _shutdownRequested;

    /// <summary>
    /// 組み込みルールでサーバを生成する。
    /// </summary>
    /// <param name="connection">エディタとの接続。</param>
    public ShaderLanguageServer(LspConnection connection)
        : this(connection, BuiltInAnalyzers.All)
    {
    }

    /// <summary>
    /// サーバを生成する。
    /// </summary>
    /// <param name="connection">エディタとの接続。</param>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <remarks>
    /// 自作ルールを足したサーバを作るための入口である。
    /// CLI の <c>Program.RunAsync</c> と同じ形で一覧を渡す。
    /// </remarks>
    public ShaderLanguageServer(LspConnection connection, IEnumerable<DiagnosticAnalyzer> analyzers)
        : this(connection, analyzers, [])
    {
    }

    /// <summary>
    /// 独自エンジンの環境を足したサーバを生成する。
    /// </summary>
    /// <param name="connection">エディタとの接続。</param>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <param name="environments">足す環境。CLI の <c>Program.RunAsync</c> に渡すものと同じ。</param>
    /// <exception cref="ArgumentException">環境の名前が組み込みの環境やほかの環境と重なっている。</exception>
    public ShaderLanguageServer(
        LspConnection connection,
        IEnumerable<DiagnosticAnalyzer> analyzers,
        IEnumerable<HostEnvironment> environments)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(analyzers);
        ArgumentNullException.ThrowIfNull(environments);

        _connection = connection;
        _analyzers = [.. analyzers];
        _environments = [.. environments];
        Cli.Environments.AnalysisEnvironments.Validate(_environments);
        _session = new AnalysisSession(workspaceDirectory: null, _analyzers, _environments);
    }

    /// <summary>
    /// 接続が閉じるまで要求を処理し続ける。
    /// </summary>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>終了コード。</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using JsonDocument? message = await _connection.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (message is null)
            {
                break;
            }

            if (!message.RootElement.TryGetProperty("method", out JsonElement method))
            {
                continue;
            }

            string name = method.GetString() ?? string.Empty;

            if (name == "exit")
            {
                return _shutdownRequested ? 0 : 1;
            }

            await HandleAsync(name, message.RootElement, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>
    /// 要求を 1 件処理する。
    /// </summary>
    /// <param name="method">メソッド名。</param>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>処理の完了を表すタスク。</returns>
    /// <remarks>
    /// 知らないメソッドは、何も伝えずに捨てる。
    /// エディタは対応していない機能も送ってくるため、
    /// 応答が要るもの (id を持つ要求) だけを取りこぼさなければよい。
    /// </remarks>
    private async Task HandleAsync(string method, JsonElement message, CancellationToken cancellationToken)
    {
        // 構文だけを読む環境 (GLSL など) の文書は、意味解析のモデルを作らない。モデルを使う要求には空で答える。
        if (UsesSemanticModel(method) && !HasSemanticModel(message))
        {
            await RespondNullAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (method)
        {
            case "initialize":
                _workspaceRoot = ReadWorkspaceDirectory(message);
                _session = CreateWorkspaceSession();
                _inactiveRegionsRequested = ReadInactiveRegionsOption(message);
                _languageFeatures = ReadLanguageFeaturesOption(message);
                _hoverCodeBlocks = ReadHoverCodeBlocksOption(message);
                _hoverMarkdown = AcceptsMarkdown(message, "hover", "contentFormat");
                _completionMarkdown = AcceptsMarkdown(message, "completion", "completionItem", "documentationFormat");
                await RespondInitializeAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "shutdown":
                _shutdownRequested = true;
                await RespondNullAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "textDocument/didOpen":
                await OnDidOpenAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "textDocument/didChange":
                await OnDidChangeAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "textDocument/didSave":
                await OnDidSaveAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "textDocument/didClose":
                await OnDidCloseAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "textDocument/hover":
                StartModelRequest(message, OnHoverAsync, cancellationToken);
                break;

            case "textDocument/definition":
                StartModelRequest(message, OnDefinitionAsync, cancellationToken);
                break;

            case "textDocument/completion":
                StartModelRequest(message, OnCompletionAsync, cancellationToken);
                break;

            case "textDocument/signatureHelp":
                StartModelRequest(message, OnSignatureHelpAsync, cancellationToken);
                break;

            case "textDocument/references":
                StartModelRequest(message, OnReferencesAsync, cancellationToken);
                break;

            case "textDocument/rename":
                StartModelRequest(message, OnRenameAsync, cancellationToken);
                break;

            case "textDocument/codeAction":
                await OnCodeActionAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "shaderlyn/inspect":
                await OnInspectAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "shaderlyn/conditionSymbols":
                await OnConditionSymbolsAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case "shaderlyn/setDefinedSymbols":
                await OnSetDefinedSymbolsAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            default:
                if (message.TryGetProperty("id", out _))
                {
                    await RespondNullAsync(message, cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    /// <summary>意味解析のモデルを使う要求か。</summary>
    /// <param name="method">メソッドの名前。</param>
    /// <returns>使うなら <see langword="true"/>。</returns>
    private static bool UsesSemanticModel(string method) => method is
        "textDocument/hover" or "textDocument/definition" or "textDocument/completion" or "textDocument/signatureHelp"
        or "textDocument/references" or "textDocument/rename" or "shaderlyn/inspect" or "shaderlyn/conditionSymbols";

    /// <summary>要求の文書を解析する環境が、意味解析のモデルを作るか。文書が分からなければ作るものとする。</summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <returns>作るなら <see langword="true"/>。</returns>
    private bool HasSemanticModel(JsonElement message)
        => !(message.TryGetProperty("params", out JsonElement parameters)
             && parameters.TryGetProperty("textDocument", out JsonElement textDocument)
             && textDocument.TryGetProperty("uri", out JsonElement uri)
             && uri.GetString() is { } value
             && !SessionFor(value).HasSemanticModel);

    /// <summary>スクリプトが開かれたときの処理。</summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>処理の完了を表すタスク。</returns>
    /// <remarks>
    /// 開いた時点では保存済みの中身であり、意味解析まで走らせてよい。
    /// 開いた直後に指摘が出ないと、動いているかどうかが利用者に伝わらない。
    /// </remarks>
    private async Task OnDidOpenAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!TryReadTextDocument(message, out JsonElement document)
            || document.GetProperty("uri").GetString() is not { } uri
            || DocumentUri.ToFilePath(uri) is not { } filePath)
        {
            return;
        }

        string content = document.TryGetProperty("text", out JsonElement text) ? text.GetString() ?? string.Empty : string.Empty;
        int version = document.TryGetProperty("version", out JsonElement v) ? v.GetInt32() : 0;

        OpenDocument opened = new(uri, filePath, version, SourceText.From(content, filePath));
        _documents[uri] = opened;

        await PublishAsync(opened, AnalysisDepth.Full, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>スクリプトが編集されたときの処理。</summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>処理の完了を表すタスク。</returns>
    private async Task OnDidChangeAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!TryReadTextDocument(message, out JsonElement document)
            || document.GetProperty("uri").GetString() is not { } uri
            || !_documents.TryGetValue(uri, out OpenDocument? existing)
            || !message.GetProperty("params").TryGetProperty("contentChanges", out JsonElement changes)
            || changes.ValueKind != JsonValueKind.Array
            || changes.GetArrayLength() == 0)
        {
            return;
        }

        // 全文で受け取るよう申告しているので、最後の変更が現在の中身そのものである。
        string content = changes[changes.GetArrayLength() - 1].TryGetProperty("text", out JsonElement text)
            ? text.GetString() ?? string.Empty
            : string.Empty;

        int version = document.TryGetProperty("version", out JsonElement v) ? v.GetInt32() : existing.Version + 1;

        OpenDocument updated = existing.WithText(version, content);
        _documents[uri] = updated;

        await PublishAsync(updated, AnalysisDepth.Syntax, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>スクリプトが保存されたときの処理。</summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>処理の完了を表すタスク。</returns>
    private async Task OnDidSaveAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!TryReadTextDocument(message, out JsonElement document)
            || document.GetProperty("uri").GetString() is not { } uri
            || !_documents.TryGetValue(uri, out OpenDocument? saved))
        {
            return;
        }

        // 保存したのが取り込まれるヘッダかもしれない。その場合は取り込みの読み込み結果と展開結果を捨て、
        // 開いているスクリプトをすべて新しい中身で解析し直す。
        // 捨てずにいると、ヘッダを書き換えても古い中身で解析し、ヘッダへの指摘が書き換える前の行に出ていた。
        // 読み込んだファイルが変わっていなければ捨てない (IncludeReadLog)。
        if (ReasonToForgetIncludedFiles(DocumentUri.ToFilePath(uri)) is { } reason)
        {
            await LogAsync($"取り込みのキャッシュを捨てます ({reason})。").ConfigureAwait(false);
            ForgetIncludedFiles();
        }

        await PublishAsync(saved, AnalysisDepth.Full, cancellationToken).ConfigureAwait(false);

        foreach (OpenDocument other in _documents.Values.Where(d => !string.Equals(d.Uri, uri, StringComparison.Ordinal)).ToList())
        {
            await PublishAsync(other, AnalysisDepth.Full, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>保存したファイルに合わせて、取り込んだファイルについて覚えていることを捨てるべきかを返す。</summary>
    /// <param name="savedPath">保存したファイル。</param>
    /// <returns>捨てるべきなら、その理由。捨てなくてよければ <see langword="null"/>。</returns>
    /// <remarks>どれか 1 つの解析の単位で捨てるべきなら、すべて捨てる。判断の条件は <see cref="IncludeReadLog"/> を参照。</remarks>
    private string? ReasonToForgetIncludedFiles(string? savedPath)
    {
        foreach (AnalysisSession session in _sessionsByConfiguration.Values.Select(entry => entry.Session).Prepend(_session))
        {
            if (session.Reads.ReasonToForget(savedPath) is { } reason)
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>
    /// 取り込んだファイルについて覚えていることを捨てる。
    /// </summary>
    /// <remarks>
    /// 取り込みの読み込み結果 (FileSystemIncludeResolver) と展開結果 (IncludeCache) は、解析の単位 (<see cref="AnalysisSession"/>) が持っている。
    /// 単位ごと作り直す。読み込んだファイルの記録 (<see cref="AnalysisSession.Reads"/>) も空になる。
    /// </remarks>
    private void ForgetIncludedFiles()
    {
        _session = CreateWorkspaceSession();
        _sessionsByConfiguration.Clear();
        _unrealByDirectory.Clear();
        _models.Clear();
    }

    /// <summary>スクリプトが閉じられたときの処理。</summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>処理の完了を表すタスク。</returns>
    /// <remarks>
    /// 閉じたファイルの指摘は消す。
    /// 残すと、開いていないファイルの指摘が一覧に居座り続ける。
    /// </remarks>
    private async Task OnDidCloseAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (TryReadTextDocument(message, out JsonElement document)
            && document.GetProperty("uri").GetString() is { } uri)
        {
            CancelRunning(uri);
            _documents.Remove(uri);
            _models.Remove(uri);

            // そのスクリプトが取り込んだヘッダへ送っていた指摘も消す。
            // 閉じたスクリプトがヘッダなら、それを取り込む開いたスクリプトの分は残る。
            List<(string Uri, ImmutableArray<Diagnostic> Diagnostics)> updates;

            lock (_diagnosticsGate)
            {
                _ownDiagnostics.Remove(uri);
                updates = [.. ReplaceIncludedDiagnostics(uri, []).Append(uri).Select(u => (u, MergedDiagnostics(u)))];
            }

            foreach ((string target, ImmutableArray<Diagnostic> diagnostics) in updates)
            {
                await PublishDiagnosticsAsync(target, diagnostics, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// カーソルの下の要素について答える。
    /// </summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="request">文書とモデル。文書が開かれていなければ <see langword="null"/>。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// <b>応答は必ず返す。</b>
    /// 要求に id がある以上、答えるものが無くても空で返さなければ
    /// エディタは待ち続ける。
    /// </remarks>
    private async Task OnHoverAsync(JsonElement message, ModelRequest? request, CancellationToken cancellationToken)
    {
        HoverResult? hover = null;

        try
        {
            if (request is not null)
            {
                hover = await request.UseAsync(HoverBuilder.Build, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // 答えられないだけで、常駐は続ける。
            await LogFailureAsync("ホバーの組み立て", e).ConfigureAwait(false);
        }

        if (hover is not { } result)
        {
            await RespondNullAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _connection.WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, message);

            writer.WriteStartObject("result");

            writer.WriteStartObject("contents");
            if (_hoverMarkdown)
            {
                writer.WriteString("kind", "markdown");
                writer.WriteString("value", _hoverCodeBlocks ? result.Markdown : HoverInlineCode.Convert(result.Markdown));
            }
            else
            {
                writer.WriteString("kind", "plaintext");
                writer.WriteString("value", MarkdownPlainText.Convert(result.Markdown));
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// カーソルの下の名前の定義を返す。
    /// </summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="request">文書とモデル。文書が開かれていなければ <see langword="null"/>。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// <para>
    /// 取り込んだヘッダの中も返す。
    /// エディタは別のファイルでも開いて飛べる。
    /// </para>
    /// <para>
    /// <b>開いているスクリプトを指すときは、受け取った URI をそのまま返す。</b>
    /// 同じファイルを指す URI の書き方は 1 つではない。
    /// VS Code は <c>file:///c%3A/...</c> と送ってくるが、
    /// パスから組み立て直すと <c>file:///c:/...</c> になる。
    /// 文字列が違えばエディタは別のスクリプトとして扱い、
    /// 同じファイルなのに新しいタブを開こうとして
    /// 「ファイルが見つからなかったため、エディターを開くことができませんでした」と出す。
    /// 診断の通知が受け取った URI をそのまま返しているのと同じ理由である。
    /// </para>
    /// </remarks>
    private async Task OnDefinitionAsync(JsonElement message, ModelRequest? request, CancellationToken cancellationToken)
    {
        IReadOnlyList<DefinitionTarget> targets = [];
        string? documentUri = request?.Document.Uri;
        string? documentPath = request?.Document.FilePath;

        try
        {
            if (request is not null)
            {
                targets = await request.UseAsync(DefinitionBuilder.Build, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // 飛べないだけで、常駐は続ける。
            await LogFailureAsync("定義の検索", e).ConfigureAwait(false);
        }

        await _connection.WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, message);
            writer.WriteStartArray("result");

            foreach (DefinitionTarget target in targets)
            {
                writer.WriteStartObject();
                writer.WriteString("uri", DescribeTargetUri(target, documentUri, documentPath));

                writer.WriteStartObject("range");
                WritePosition(writer, "start", target.Range.Start);
                WritePosition(writer, "end", target.Range.End);
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 定義の位置を指す URI を組み立てる。
    /// </summary>
    /// <param name="target">対象の定義。</param>
    /// <param name="documentUri">エディタから受け取った、開いているスクリプトの URI。</param>
    /// <param name="documentPath">開いているスクリプトのパス。</param>
    /// <returns>返す URI。</returns>
    /// <remarks>
    /// 同じファイルなら受け取った URI をそのまま返す。
    /// 別のファイルなら、受け取った URI と同じ書き方に合わせて組み立てる。
    /// </remarks>
    private static string DescribeTargetUri(
        DefinitionTarget target,
        string? documentUri,
        string? documentPath)
        => documentUri is not null
           && string.Equals(target.FilePath, documentPath, StringComparison.Ordinal)
            ? documentUri
            : DocumentUri.FromFilePath(target.FilePath, documentUri);

    /// <summary>
    /// 文書の今のバージョンのセマンティックモデルを返す。なければ組み立てを始める。受信のループから呼ぶ。
    /// </summary>
    /// <param name="uri">対象のファイルの URI。</param>
    /// <param name="document">対象のスクリプト。</param>
    /// <returns>セマンティックモデル (組み立て中のこともある)。</returns>
    /// <remarks>
    /// バージョンが変わっていれば組み立て直す。古いモデルを使うと、位置が編集前のものになる。
    /// 設定ファイルを書き換えた場合と、定義済みにするシンボルを選び直した場合も組み立て直す。
    /// </remarks>
    private DocumentModel ModelFor(string uri, OpenDocument document)
    {
        AnalysisSession session = SessionFor(uri);
        ImmutableArray<string> symbols = DefinedSymbolsOf(uri);

        if (_models.TryGetValue(uri, out DocumentModel? cached) && cached.Matches(document.Version, session, symbols))
        {
            return cached;
        }

        DocumentModel model = new(document.Version, session, symbols, () => session.Prepare(document.Text, symbols), cached);
        _models[uri] = model;
        return model;
    }

    /// <summary>
    /// セマンティックモデルを使う要求を、受信のループの外で処理する。
    /// </summary>
    /// <param name="message">受け取った要求。</param>
    /// <param name="handle">要求を処理して応答する処理。文書が開かれていなければ、モデルを渡さずに呼ぶ。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <remarks>
    /// <para>
    /// <b>モデルの組み立てを待つ間も、次のメッセージを受け取る。</b>
    /// 受信のループで待つと、編集の通知と構文の段の指摘が組み立ての終わりまで遅れる
    /// (URP の Lit.shader で、編集のたびに 1 秒)。応答の順序は要求の順と変わることがある (LSP では許されている)。
    /// </para>
    /// <para>
    /// 文書とカーソルの位置は、受け取った時点のものを使う。後から届いた編集の中身で位置を数えると、ずれる。
    /// </para>
    /// </remarks>
    private void StartModelRequest(
        JsonElement message,
        Func<JsonElement, ModelRequest?, CancellationToken, Task> handle,
        CancellationToken cancellationToken)
    {
        // 受け取ったメッセージは、受信のループが次を読む前に破棄する。
        JsonElement request = message.Clone();
        ModelRequest? target = ReadModelRequest(request);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await handle(request, target, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 新しいバージョンのモデルに置き換えられ、組み立てなかった (DocumentModel)。応答は返す。
                    await RespondNullAsync(request, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 終了する。
                }
                catch (Exception e)
                {
                    await LogFailureAsync("要求の処理", e).ConfigureAwait(false);
                }
            },
            CancellationToken.None);
    }

    /// <summary>要求の文書とカーソルの位置を読み、モデルを用意する。受信のループから呼ぶ。</summary>
    /// <param name="message">受け取った要求。</param>
    /// <returns>文書とモデル。文書が開かれていなければ <see langword="null"/>。</returns>
    private ModelRequest? ReadModelRequest(JsonElement message)
    {
        if (!TryReadTextDocument(message, out JsonElement textDocument)
            || textDocument.GetProperty("uri").GetString() is not { } uri
            || !_documents.TryGetValue(uri, out OpenDocument? document))
        {
            return null;
        }

        int offset;

        try
        {
            offset = message.GetProperty("params").TryGetProperty("position", out JsonElement position)
                ? document.Text.GetOffset(new LinePosition(
                    position.GetProperty("line").GetInt32(),
                    position.GetProperty("character").GetInt32()))
                : 0;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException)
        {
            // 読めない要求でも応答は返す。失敗は、それぞれの処理が記録して空で答える (ModelRequest.UseAsync が投げる)。
            return new ModelRequest(document, null, 0, e);
        }

        return new ModelRequest(document, ModelFor(uri, document), offset, null);
    }

    /// <summary>モデルを使う要求の、文書・モデル・カーソルの位置。</summary>
    /// <param name="Document">受け取った時点の文書。</param>
    /// <param name="Model">その文書のモデル。</param>
    /// <param name="Offset">カーソルのオフセット。</param>
    /// <param name="Failure">要求を読めなかった場合の例外。</param>
    private sealed record ModelRequest(OpenDocument Document, DocumentModel? Model, int Offset, Exception? Failure)
    {
        /// <summary>モデルの組み立てを待って使う (<see cref="DocumentModel.UseAsync"/>)。</summary>
        /// <typeparam name="T">結果の型。</typeparam>
        /// <param name="use">モデルとカーソルの位置を使う処理。</param>
        /// <param name="cancellationToken">キャンセル用トークン。</param>
        /// <returns>処理の結果。</returns>
        public Task<T> UseAsync<T>(Func<ShaderCompilation, int, T> use, CancellationToken cancellationToken)
            => Failure is not null || Model is null
                ? Task.FromException<T>(Failure ?? new InvalidOperationException("モデルがありません。"))
                : Model.UseAsync(
                model => use(model.Compilation ?? throw new InvalidOperationException("この環境はセマンティックモデルを作りません。"), Offset),
                cancellationToken);
    }

    /// <summary>
    /// Unreal の生成物を作った後に ini や C++ が変わっていたら、通知で伝える。
    /// </summary>
    /// <param name="session">解析に使う単位。</param>
    /// <remarks>
    /// 生成物のフォルダーごとに、サーバーが動いている間に 1 度だけ確かめる。
    /// C++ の一覧と更新日時を読むので約 2 秒かかる (<see cref="Unreal.UnrealGenerationStamp"/>)。解析を待たせないよう、裏で行う。
    /// </remarks>
    private void CheckUnrealGeneration(AnalysisSession session)
    {
        if (session.UnrealGeneratedDirectory is not { } directory || !_checkedGeneratedDirectories.Add(Path.GetFullPath(directory)))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (Unreal.UnrealGenerationStamp.Check(directory)?.Describe() is { } message)
                {
                    await ShowWarningAsync(message).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await LogFailureAsync("Unreal の生成物の確認", exception).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// <c>--unity-generate</c> で書き出したものが、今のプロジェクトと合っていなければ、通知で伝える。
    /// </summary>
    /// <param name="session">解析に使う単位。</param>
    /// <remarks>書き出したフォルダーごとに、サーバーが動いている間に 1 度だけ確かめる (<see cref="Unity.UnityIncludeGeneration.DescribeMismatch"/>)。</remarks>
    private void CheckUnityGeneration(AnalysisSession session)
    {
        if (session.UnityGeneratedDirectory is not { } directory
            || session.UnityProjectPath is not { } project
            || !_checkedGeneratedDirectories.Add(Path.GetFullPath(directory)))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (Unity.UnityIncludeGeneration.DescribeMismatch(Path.GetFullPath(directory), project) is { } message)
                {
                    await ShowWarningAsync(message).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await LogFailureAsync("--unity-generate で書き出したファイルの確認", exception).ConfigureAwait(false);
            }
        });
    }

    /// <summary>警告の通知 (<c>window/showMessage</c>) を送る。</summary>
    /// <param name="message">通知の文。</param>
    /// <returns>非同期操作。</returns>
    private Task ShowWarningAsync(string message)
        => _connection.WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", "window/showMessage");

            writer.WriteStartObject("params");
            writer.WriteNumber("type", MessageTypeWarning);
            writer.WriteString("message", message);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }, CancellationToken.None);

    /// <summary>
    /// 開いたファイルの解析に使う設定を返す。
    /// </summary>
    /// <param name="uri">ファイルの URI。</param>
    /// <returns>そのファイルから上へ辿って一番近い設定ファイルの設定。見つからなければワークスペースの設定。</returns>
    /// <remarks>
    /// <para>
    /// <b>CLI と同じく、ファイルの場所から設定ファイルを探す。</b>
    /// ワークスペースのルートからだけ探すと、サブフォルダーに置いた設定 (環境ごとに分けたフォルダーなど) が読まれず、
    /// 別の環境で解析してしまう (<c>environment: unreal</c> の <c>.usf</c> を Unity の環境で ShaderLab として読むなど)。
    /// </para>
    /// <para>
    /// 設定ファイルが書き換えられたら作り直す。更新日時で見分ける。
    /// </para>
    /// </remarks>
    private AnalysisSession SessionFor(string uri)
    {
        string? path = DocumentUri.ToFilePath(uri);
        AnalysisSession configured = ConfiguredSessionFor(path, out string? configuration, out DateTime written);

        // HLSL 以外の言語 (GLSL など) の拡張子は、CLI と同じく、設定の環境が読まなければその言語の環境で解析する。
        // 設定の環境 (Unity など) で読むと、GLSL を ShaderLab や HLSL として読んで構文エラーを並べる。
        // 環境を決めていなければ、.usf・.ush は Unreal Engine の環境で解析する。
        string? language = path is null
            ? null
            : Cli.Environments.AnalysisSettings.EnvironmentForOpenedFile(
                Path.GetExtension(path), configured.DefaultExtensions, configured.EnvironmentChosen);

        string key = (configuration ?? string.Empty) + "\n" + language;
        AnalysisSession session = language is null ? configured : Keyed(key, () => CreateSession(configuration, language, null));

        // 設定ファイルに書いていない Unreal Engine のエンジンとプロジェクトは、開いたファイルから探す (ワークスペースのルートで止める)。
        // 同じ設定ファイルの下でも、ファイルによってエンジン・プロジェクトが違えば別の単位にする。
        if (path is null || session.UnrealLocations is not { } shared || LocateUnreal(path) is not { } here
            || (SamePath(here.EngineDirectory, shared.EngineDirectory) && SamePath(here.ProjectDirectory, shared.ProjectDirectory)))
        {
            return session;
        }

        return Keyed(
            key + "\nunreal\n" + here.EngineDirectory + "\n" + here.ProjectDirectory,
            () => CreateSession(configuration, language, path));

        AnalysisSession Keyed(string sessionKey, Func<AnalysisSession> create)
            => _sessionsByConfiguration.AddOrUpdate(
                sessionKey,
                _ => (written, create()),
                (_, existing) => existing.Written == written ? existing : (written, create())).Session;
    }

    /// <summary>開いたファイルのフォルダーごとに、Unreal Engine のエンジンとプロジェクトを探した結果。保存のときに捨てる。</summary>
    private readonly ConcurrentDictionary<string, Unreal.UnrealLocations> _unrealByDirectory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>開いたファイルから、Unreal Engine のエンジンとプロジェクトを探す。ワークスペースのルートより上へはたどらない (利用者の決定)。</summary>
    /// <param name="path">開いたファイル。</param>
    /// <returns>探した結果。フォルダーが分からなければ <see langword="null"/>。</returns>
    private Unreal.UnrealLocations? LocateUnreal(string path)
        => Path.GetDirectoryName(path) is { Length: > 0 } directory
            ? _unrealByDirectory.GetOrAdd(directory, d => Unreal.UnrealLocator.Find(d, _workspaceRoot))
            : null;

    /// <summary>2 つのパスが同じフォルダーを指すか (どちらも <see langword="null"/> なら同じ)。</summary>
    private static bool SamePath(string? a, string? b)
        => string.Equals(
            a is null ? null : Path.TrimEndingDirectorySeparator(a),
            b is null ? null : Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>ワークスペースのルート (initialize で受け取ったもの)。設定ファイルのフォルダーで作った解析の単位の WorkspaceDirectory とは違う。</summary>
    private string? _workspaceRoot;

    /// <summary>ワークスペースのルートの解析の単位を作る。Unreal Engine の場所は、ワークスペースのルートから探し、上へはたどらない。</summary>
    private AnalysisSession CreateWorkspaceSession()
        => new(_workspaceRoot, _analyzers, _environments, unrealSearch: _workspaceRoot is null ? null : (_workspaceRoot, _workspaceRoot));

    /// <summary>ファイルから上へ辿って一番近い設定ファイルの設定を返す (<see cref="SessionFor"/>)。</summary>
    /// <param name="path">ファイルのパス。</param>
    /// <param name="configuration">見つかった設定ファイル。見つからなければ <see langword="null"/>。</param>
    /// <param name="written">設定ファイルの更新日時。</param>
    /// <returns>設定。見つからなければワークスペースの設定。</returns>
    private AnalysisSession ConfiguredSessionFor(string? path, out string? configuration, out DateTime written)
    {
        written = default;
        configuration = null;

        if (path is null
            || Path.GetDirectoryName(path) is not { Length: > 0 } directory
            || EffectiveSettings.FindConfigurationFile(directory) is not { } found)
        {
            return _session;
        }

        try
        {
            written = File.GetLastWriteTimeUtc(found);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return _session;
        }

        configuration = found;
        DateTime stamp = written;

        return _sessionsByConfiguration.AddOrUpdate(
            found,
            key => (stamp, CreateSession(key, environment: null, unrealStart: null)),
            (key, existing) => existing.Written == stamp ? existing : (stamp, CreateSession(key, environment: null, unrealStart: null))).Session;
    }

    /// <summary>設定ファイルのフォルダーで解析の設定を作る。</summary>
    /// <param name="configurationPath">設定ファイル。無ければワークスペースのルートで作る。</param>
    /// <param name="environment">設定の環境の代わりに使う環境の名前。</param>
    /// <param name="unrealStart">Unreal Engine のエンジンとプロジェクトを探し始めるファイル。<see langword="null"/> なら設定のフォルダーから探す。</param>
    /// <returns>設定。</returns>
    private AnalysisSession CreateSession(string? configurationPath, string? environment, string? unrealStart)
    {
        string? directory = configurationPath is null ? _session.WorkspaceDirectory : Path.GetDirectoryName(configurationPath);
        string? start = unrealStart ?? directory;

        return new(directory, _analyzers, _environments, environment, start is null ? null : (start, _workspaceRoot));
    }

    /// <summary>
    /// 指摘に対する修正を返す。
    /// </summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// <para>
    /// エディタが要求に添えてくる指摘をそのまま使う。
    /// こちらで解析し直すと、画面に出ている指摘と食い違う修正を出しうる。
    /// </para>
    /// <para>
    /// <b>要求された範囲に重なる指摘だけを見る。</b>
    /// スクリプト全体の指摘を見ると、カーソルから遠い場所を直す候補が並ぶ。
    /// 抑制コメントは ID だけが見出しに出るので、どれがどこの行のものか区別できない。
    /// </para>
    /// <para>
    /// 同じ修正は 1 つにまとめる。
    /// 1 行に同じ ID の指摘が複数あると、抑制コメントの差し込みはすべて同じ編集になる。
    /// </para>
    /// </remarks>
    private async Task OnCodeActionAsync(JsonElement message, CancellationToken cancellationToken)
    {
        ImmutableArray<Diagnostic> diagnostics = default;

        if (!TryReadTextDocument(message, out JsonElement textDocument)
            || textDocument.GetProperty("uri").GetString() is not { } uri
            || !_documents.TryGetValue(uri, out OpenDocument? document)
            || !TryGetLastDiagnostics(uri, out diagnostics)
            || ReadRequestedRange(message) is not { } range)
        {
            await RespondEmptyArrayAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        HashSet<string> requested = ReadRequestedDiagnosticIds(message);
        List<CodeActionEdit> actions = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (requested.Count > 0 && !requested.Contains(diagnostic.Id))
            {
                continue;
            }

            if (!Overlaps(diagnostic.Location.LineSpan, range))
            {
                continue;
            }

            foreach (CodeActionEdit action in CodeActionBuilder.Build(document.Text, diagnostic))
            {
                if (seen.Add(action.Key))
                {
                    actions.Add(action);
                }
            }
        }

        await _connection.WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, message);
            writer.WriteStartArray("result");

            foreach (CodeActionEdit action in actions)
            {
                WriteCodeAction(writer, uri, document.Text, action);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>要求された範囲を読む。</summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <returns>読めた範囲。読めない場合は <see langword="null"/>。</returns>
    private static LinePositionSpan? ReadRequestedRange(JsonElement message)
    {
        if (!message.TryGetProperty("params", out JsonElement parameters)
            || !parameters.TryGetProperty("range", out JsonElement range)
            || !range.TryGetProperty("start", out JsonElement start)
            || !range.TryGetProperty("end", out JsonElement end))
        {
            return null;
        }

        return new LinePositionSpan(ReadPosition(start), ReadPosition(end));

        static LinePosition ReadPosition(JsonElement position) => new(
            position.GetProperty("line").GetInt32(),
            position.GetProperty("character").GetInt32());
    }

    /// <summary>2 つの範囲が重なっているかどうかを返す。</summary>
    /// <param name="diagnostic">指摘の範囲。</param>
    /// <param name="requested">要求された範囲。</param>
    /// <returns>重なっている場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// 端どうしが接しているだけでも重なりとみなす。
    /// 要求はカーソル位置だけの幅 0 の範囲で来ることが多く、
    /// 名前の直後にカーソルを置いた状態でも修正を出せないと使いにくい。
    /// </remarks>
    private static bool Overlaps(LinePositionSpan diagnostic, LinePositionSpan requested) =>
        diagnostic.Start.CompareTo(requested.End) <= 0 && requested.Start.CompareTo(diagnostic.End) <= 0;

    /// <summary>要求に添えられた指摘の ID を集める。</summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <returns>ID の集合。添えられていない場合は空。</returns>
    private static HashSet<string> ReadRequestedDiagnosticIds(JsonElement message)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);

        if (message.TryGetProperty("params", out JsonElement parameters)
            && parameters.TryGetProperty("context", out JsonElement context)
            && context.TryGetProperty("diagnostics", out JsonElement diagnostics)
            && diagnostics.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement diagnostic in diagnostics.EnumerateArray())
            {
                if (diagnostic.TryGetProperty("code", out JsonElement code) && code.GetString() is { } id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>修正 1 つを書き出す。</summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="uri">対象のファイルの URI。</param>
    /// <param name="text">対象のソーステキスト。</param>
    /// <param name="action">書き出す修正。</param>
    private static void WriteCodeAction(
        Utf8JsonWriter writer,
        string uri,
        SourceText text,
        CodeActionEdit action)
    {
        writer.WriteStartObject();
        writer.WriteString("title", action.Title);
        writer.WriteString("kind", "quickfix");
        writer.WriteBoolean("isPreferred", action.IsPreferred);

        writer.WriteStartObject("edit");
        writer.WriteStartObject("changes");
        writer.WriteStartArray(uri);

        foreach (TextReplacement edit in action.Edits)
        {
            writer.WriteStartObject();
            writer.WriteStartObject("range");
            WritePosition(writer, "start", text.GetLinePosition(edit.Span.Start));
            WritePosition(writer, "end", text.GetLinePosition(edit.Span.End));
            writer.WriteEndObject();
            writer.WriteString("newText", edit.NewText);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>空の配列を返す。</summary>
    /// <param name="message">対応する要求。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    private async Task RespondEmptyArrayAsync(JsonElement message, CancellationToken cancellationToken)
    {
        await _connection.WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, message);
            writer.WriteStartArray("result");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 条件で参照されているのに定義されていないシンボルを返す。
    /// </summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// <para>
    /// エディタの「条件のシンボルを選ぶ」で使う。
    /// 利用者がすでに定義済みにしたものも候補に含め、選ばれているかどうかを添える。
    /// 含めないと、選んだシンボルを外す手段が無くなる。
    /// </para>
    /// <para>
    /// シンボルかどうかと、今どちらの表示にしているかも返す。
    /// シンボルを選ぶ意味は「その構成で表示する」ことなので、エディタはそれを分けて見せる。
    /// </para>
    /// </remarks>
    private async Task OnConditionSymbolsAsync(JsonElement message, CancellationToken cancellationToken)
    {
        ImmutableArray<ConditionSymbol> candidates = [];
        ImmutableArray<string> defined = [];
        bool selectedConfiguration = false;
        bool found = false;

        try
        {
            if (TryReadTextDocument(message, out JsonElement textDocument)
                && textDocument.GetProperty("uri").GetString() is { } uri
                && _documents.TryGetValue(uri, out OpenDocument? document))
            {
                defined = DefinedSymbolsOf(uri);
                selectedConfiguration = _selectedConfigurationDisplays.Contains(uri);

                // 定義を足していないモデルから集める。足したモデルでは、選んだシンボルが候補から消える。
                candidates = ConditionSymbols.Collect(SessionFor(uri).CreateModel(document.Text));
                found = true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            found = false;
            await LogFailureAsync("条件のシンボルの収集", e).ConfigureAwait(false);
        }

        await _connection.WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, message);

            if (!found)
            {
                writer.WriteNull("result");
            }
            else
            {
                HashSet<string> declared = [.. candidates.Where(c => c.Declared).Select(c => c.Name)];
                Dictionary<string, ConditionSymbol> byName =
                    candidates.ToDictionary(c => c.Name, StringComparer.Ordinal);

                writer.WriteStartObject("result");
                writer.WriteString(
                    "display",
                    selectedConfiguration ? SelectedConfigurationDisplay : AllConfigurationsDisplay);
                writer.WriteStartArray("symbols");

                foreach (string name in candidates.Select(c => c.Name).Union(defined).Order(StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    writer.WriteBoolean("defined", defined.Contains(name));
                    writer.WriteBoolean("declared", declared.Contains(name));

                    // v0.1 の名前。拡張の古い版のために残す。
                    writer.WriteBoolean("declaredByPragma", declared.Contains(name));

                    // 同じ行のシンボルは同時に有効にならない。
                    // 画面がそれを守れるよう、行の単位と「外せない行か」を添える。
                    // group / requiresOne は最初の組だけを表す (組を 1 つしか知らない古い拡張のため)。
                    // 1 つのシンボルが複数の組に入ることがあるので、groups にすべての組の番号を並べる。
                    if (byName.TryGetValue(name, out ConditionSymbol symbol) && symbol.Group >= 0)
                    {
                        writer.WriteNumber("group", symbol.Group);
                        writer.WriteBoolean("requiresOne", symbol.RequiresOne);
                        writer.WriteStartArray("groups");

                        foreach (ConditionGroup group in symbol.Groups)
                        {
                            writer.WriteNumberValue(group.Id);
                        }

                        writer.WriteEndArray();
                    }

                    // 値を持つシンボルの原子 (QUALITY==2) は、名前と値を分けて添える。画面は「QUALITY = 2」と見せる。
                    if (byName.TryGetValue(name, out ConditionSymbol valued) && valued.Variable is { } variable && valued.Value is { } value)
                    {
                        writer.WriteString("variable", variable);
                        writer.WriteNumber("value", value);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                // 組ごとの「どれか 1 つが必ず有効か」。番号は symbols の groups と同じ。
                writer.WriteStartArray("groups");

                foreach (ConditionGroup group in candidates
                             .SelectMany(c => c.Groups.IsDefault ? [] : c.Groups)
                             .Distinct()
                             .OrderBy(g => g.Id))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", group.Id);
                    writer.WriteBoolean("requiresOne", group.RequiresOne);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ファイルごとに定義済みにするシンボルを受け取り、解析し直す。
    /// </summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>処理の完了を表すタスク。</returns>
    /// <remarks>
    /// <para>
    /// <b>応答を返してから解析を始める。</b>
    /// 解析の結果は通知として後から届く。先に解析を始めると応答より前に診断が届きうるので、
    /// 要求を送った側が「応答のあとに結果が来る」と仮定できなくなる。
    /// </para>
    /// <para>
    /// まだ開かれていないファイルの分も受け取って覚える。
    /// エディタは起動し直したとき、開く前のファイルの選択も送り直すためである。
    /// </para>
    /// <para>
    /// <c>display</c> が無ければ、すべての構成を合わせて表示する。
    /// 表示の切り替えができる前にエディタが覚えた選択は、この形で送られてくる。
    /// </para>
    /// </remarks>
    private async Task OnSetDefinedSymbolsAsync(JsonElement message, CancellationToken cancellationToken)
    {
        string? uri = null;

        if (TryReadTextDocument(message, out JsonElement textDocument)
            && textDocument.GetProperty("uri").GetString() is { } documentUri)
        {
            uri = documentUri;

            ImmutableArray<string> symbols = ReadSymbols(message);

            if (symbols.IsEmpty)
            {
                _definedSymbols.Remove(uri);
            }
            else
            {
                _definedSymbols[uri] = symbols;
            }

            if (ReadsSelectedConfigurationDisplay(message))
            {
                _selectedConfigurationDisplays.Add(uri);
            }
            else
            {
                _selectedConfigurationDisplays.Remove(uri);
            }

            // 覚えているモデルは前の定義で組み立てたもので、次に使うときに組み立て直す (DocumentModel.Matches)。
        }

        if (message.TryGetProperty("id", out _))
        {
            await RespondNullAsync(message, cancellationToken).ConfigureAwait(false);
        }

        if (uri is not null && _documents.TryGetValue(uri, out OpenDocument? document))
        {
            await PublishAsync(document, AnalysisDepth.Full, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>要求からシンボルの一覧を読む。</summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <returns>重複を除き、名前順に並べたシンボル。</returns>
    private static ImmutableArray<string> ReadSymbols(JsonElement message)
    {
        if (!message.TryGetProperty("params", out JsonElement parameters)
            || !parameters.TryGetProperty("symbols", out JsonElement symbols)
            || symbols.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. symbols.EnumerateArray()
                .Where(symbol => symbol.ValueKind == JsonValueKind.String)
                .Select(symbol => symbol.GetString() ?? string.Empty)
                .Where(symbol => symbol.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>そのファイルで定義済みにするシンボルを返す。</summary>
    /// <param name="uri">対象のファイルの URI。</param>
    /// <returns>定義済みにするシンボル。選ばれていなければ空。</returns>
    private ImmutableArray<string> DefinedSymbolsOf(string uri)
        => _definedSymbols.TryGetValue(uri, out ImmutableArray<string> symbols) ? symbols : [];

    /// <summary>指摘やカーソルの下を答える解析で、定義済みにするシンボルを返す。</summary>
    /// <param name="uri">対象のファイルの URI。</param>
    /// <param name="document">対象のスクリプト。</param>
    /// <returns>選ばれたシンボルから、このファイルが宣言したシンボルを除いたもの。</returns>
    /// <remarks>シンボルを除く理由は <see cref="ConditionSymbols.ExcludeDeclaredSymbols"/> を参照。</remarks>
    private ImmutableArray<string> AnalysisSymbolsOf(string uri, OpenDocument document)
        => ConditionSymbols.ExcludeDeclaredSymbols(document.Text, DefinedSymbolsOf(uri), SessionFor(uri).ShaderEnvironment);

    /// <summary>要求が、選んだ構成での表示を求めているかを読む。</summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <returns>求めていれば <see langword="true"/>。</returns>
    private static bool ReadsSelectedConfigurationDisplay(JsonElement message)
        => message.TryGetProperty("params", out JsonElement parameters)
           && parameters.TryGetProperty("display", out JsonElement display)
           && display.ValueKind == JsonValueKind.String
           && display.GetString() == SelectedConfigurationDisplay;

    /// <summary>
    /// 解析の中身を表示する HTML を返す。
    /// </summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// CLI の <c>--inspect</c> と同じものを返す。
    /// 常駐しているのだから、一時ファイルへ書き出して読み直す必要はない。
    /// </remarks>
    private async Task OnInspectAsync(JsonElement message, CancellationToken cancellationToken)
    {
        string? html = null;

        try
        {
            if (TryReadTextDocument(message, out JsonElement textDocument)
                && textDocument.GetProperty("uri").GetString() is { } uri
                && _documents.TryGetValue(uri, out OpenDocument? document))
            {
                ImmutableArray<string> symbols = AnalysisSymbolsOf(uri, document);

                html = AnalysisInspector.BuildHtml(
                    SessionFor(uri).CreateModel(document.Text, symbols),
                    SessionFor(uri).Analyze(document.Text, AnalysisDepth.Full, symbols, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            html = null;
            await LogFailureAsync("解析の中身の書き出し", e).ConfigureAwait(false);
        }

        await _connection.WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, message);

            if (html is null)
            {
                writer.WriteNull("result");
            }
            else
            {
                writer.WriteStartObject("result");
                writer.WriteString("html", html);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>メッセージから対象のスクリプトを取り出す。</summary>
    /// <param name="message">受け取ったメッセージ。</param>
    /// <param name="document">取り出したスクリプト。</param>
    /// <returns>取り出せた場合は <see langword="true"/>。</returns>
    private static bool TryReadTextDocument(JsonElement message, out JsonElement document)
    {
        if (message.TryGetProperty("params", out JsonElement parameters)
            && parameters.TryGetProperty("textDocument", out document)
            && document.TryGetProperty("uri", out _))
        {
            return true;
        }

        document = default;
        return false;
    }

    /// <summary>
    /// スクリプトを解析して結果を送る。
    /// </summary>
    /// <param name="document">対象のスクリプト。</param>
    /// <param name="stage">走らせる段。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// <para>
    /// <b>解析で受信を止めない。</b>
    /// 意味解析は Pass の数に比例して数百 ms かかる。
    /// 受信の途中で待つと、その間に届いた打鍵がすべて後ろに詰まり、
    /// 速いはずの第 1 段まで遅れて届くことになる。
    /// </para>
    /// <para>
    /// <b>同じファイルの解析は 1 本だけにする。</b>
    /// 次の編集が来た時点で、前の解析の結果はもう古い。
    /// 打ち切らずに走らせ続けると、古い結果が新しい結果を上書きしうるうえ、
    /// 打鍵の数だけ解析が積み上がる。
    /// </para>
    /// <para>
    /// <b>ルールの例外で常駐を落とさない。</b>
    /// 解析ドライバは個別のルールの例外を握るが、
    /// セマンティックモデルの組み立てで落ちる余地は残っている。
    /// 1 ファイルの解析が失敗しても、エディタは動き続けなければならない。
    /// </para>
    /// </remarks>
    private Task PublishAsync(OpenDocument document, AnalysisDepth stage, CancellationToken cancellationToken)
    {
        CancelRunning(document.Uri);

        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _running[document.Uri] = cancellation;

        // 選んだシンボルは受信の側で読んでおく。
        // 解析のスレッドから辞書を読むと、次に届いた要求の書き込みと重なる。
        ImmutableArray<string> symbols = DefinedSymbolsOf(document.Uri);
        bool selectedConfiguration = _selectedConfigurationDisplays.Contains(document.Uri);
        bool inactiveRegionsRequested = _inactiveRegionsRequested;
        AnalysisSession session = SessionFor(document.Uri);
        CheckUnrealGeneration(session);
        CheckUnityGeneration(session);

        // 意味解析を伴う段は、ホバーなどと同じモデルを使う。モデルの用意は受信のループで行う (_models は受信のループだけが触る)。
        DocumentModel? model = stage == AnalysisDepth.Full ? ModelFor(document.Uri, document) : null;
        model?.Require();

        _ = Task.Run(
            async () =>
            {
                try
                {
                    // 効いていない範囲は、展開しなければ分からない。意味解析を伴う段でだけ送る。
                    bool sendsInactiveRegions = model is not null && inactiveRegionsRequested && session.HasSemanticModel;
                    ImmutableArray<InactiveLineRange>? regions = null;

                    ImmutableArray<Diagnostic> diagnostics = model is null
                        ? session.Analyze(document.Text, stage, cancellation.Token)
                        : await model.UseAsync(
                            prepared =>
                            {
                                ImmutableArray<Diagnostic> found = session.Analyze(prepared, cancellation.Token);

                                // すべての構成を合わせた範囲は、同じモデルから求める。選んだ構成の範囲は、別のモデルを組み立てる (下)。
                                if (sendsInactiveRegions && !selectedConfiguration && prepared.Compilation is { } compilation)
                                {
                                    regions = InactiveRegionCollector.Collect(compilation);
                                }

                                return found;
                            },
                            cancellation.Token).ConfigureAwait(false);

                    // 解析の間に次の編集が届いていたら、この結果は古い。送ると、新しい中身の上で行がずれて見える。
                    cancellation.Token.ThrowIfCancellationRequested();

                    await PublishAnalysisAsync(document, stage, diagnostics, cancellation.Token)
                        .ConfigureAwait(false);

                    // 指摘より後に送る。範囲の計算に失敗しても、指摘は届いている。
                    if (sendsInactiveRegions)
                    {
                        regions ??= InactiveRegionCollector.CollectSelectedConfiguration(
                            session.CreateConfigurationModel(document.Text, symbols));

                        cancellation.Token.ThrowIfCancellationRequested();

                        await PublishInactiveRegionsAsync(document.Uri, regions.Value, cancellation.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 新しい編集に追い越された。結果は捨てる。
                }
                catch (Exception e)
                {
                    // このファイルの指摘は出せないが、常駐は続ける。
                    await LogFailureAsync($"'{document.Uri}' の解析", e).ConfigureAwait(false);
                }
            },
            CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>走っている解析があれば打ち切る。</summary>
    /// <param name="uri">対象のファイルの URI。</param>
    private void CancelRunning(string uri)
    {
        if (_running.Remove(uri, out CancellationTokenSource? running))
        {
            running.Cancel();
            running.Dispose();
        }
    }

    /// <summary>ワークスペースのルートを要求から取り出す。</summary>
    /// <param name="message">initialize の要求。</param>
    /// <returns>ルート。分からない場合は <see langword="null"/>。</returns>
    private static string? ReadWorkspaceDirectory(JsonElement message)
    {
        if (!message.TryGetProperty("params", out JsonElement parameters))
        {
            return null;
        }

        if (parameters.TryGetProperty("workspaceFolders", out JsonElement folders)
            && folders.ValueKind == JsonValueKind.Array
            && folders.GetArrayLength() > 0
            && folders[0].TryGetProperty("uri", out JsonElement folderUri))
        {
            return DocumentUri.ToFilePath(folderUri.GetString());
        }

        return parameters.TryGetProperty("rootUri", out JsonElement rootUri)
            ? DocumentUri.ToFilePath(rootUri.GetString())
            : null;
    }

    /// <summary>効いていない範囲をエディタが受け取るかどうか。</summary>
    /// <remarks>
    /// 範囲を求めるには、保存のたびにセマンティックモデルをもう 1 つ組み立てる。
    /// 表示しないエディタのために、その時間を払わない。
    /// </remarks>
    private bool _inactiveRegionsRequested;

    /// <summary>エディタが効いていない範囲を求めているかを、初期化の要求から読む。</summary>
    /// <param name="message">initialize の要求。</param>
    /// <returns>求めていれば <see langword="true"/>。</returns>
    private static bool ReadInactiveRegionsOption(JsonElement message)
        => message.TryGetProperty("params", out JsonElement parameters)
           && parameters.TryGetProperty("initializationOptions", out JsonElement options)
           && options.ValueKind == JsonValueKind.Object
           && options.TryGetProperty("inactiveRegions", out JsonElement requested)
           && requested.ValueKind == JsonValueKind.True;

    /// <summary>ホバー・補完などの言語の機能を名乗るかどうか。</summary>
    /// <remarks>
    /// Rider のように、エディタ自身がシェーダーの言語の機能を持つ場合がある。
    /// 同じ位置にホバーや補完が 2 つずつ出ないよう、そのエディタには指摘とコードアクションだけを名乗る。
    /// </remarks>
    private bool _languageFeatures = true;

    /// <summary>言語の機能を名乗るかを、初期化の要求から読む。</summary>
    /// <param name="message">initialize の要求。</param>
    /// <returns><c>languageFeatures</c> が <see langword="false"/> のときだけ <see langword="false"/>。指定が無ければ名乗る。</returns>
    private static bool ReadLanguageFeaturesOption(JsonElement message)
        => !(message.TryGetProperty("params", out JsonElement parameters)
             && parameters.TryGetProperty("initializationOptions", out JsonElement options)
             && options.ValueKind == JsonValueKind.Object
             && options.TryGetProperty("languageFeatures", out JsonElement requested)
             && requested.ValueKind == JsonValueKind.False);

    /// <summary>ホバーのコードをコードブロックで書くかどうか。</summary>
    /// <remarks>
    /// Visual Studio はコードブロックに「コードのコピー」の見出しを付けて大きく表示する。
    /// そのエディタには、コードブロックを行内のコードにして返す (<see cref="HoverInlineCode"/>)。
    /// </remarks>
    private bool _hoverCodeBlocks = true;

    /// <summary>ホバーのコードをコードブロックで書くかを、初期化の要求から読む。</summary>
    /// <param name="message">initialize の要求。</param>
    /// <returns><c>hoverCodeBlocks</c> が <see langword="false"/> のときだけ <see langword="false"/>。指定が無ければコードブロックで書く。</returns>
    private static bool ReadHoverCodeBlocksOption(JsonElement message)
        => !(message.TryGetProperty("params", out JsonElement parameters)
             && parameters.TryGetProperty("initializationOptions", out JsonElement options)
             && options.ValueKind == JsonValueKind.Object
             && options.TryGetProperty("hoverCodeBlocks", out JsonElement requested)
             && requested.ValueKind == JsonValueKind.False);

    /// <summary>ホバーを Markdown で送るかどうか。</summary>
    /// <remarks>Markdown を表示できないエディタ (Visual Studio 2022 など) には、プレーンテキストにして送る (<see cref="MarkdownPlainText"/>)。</remarks>
    private bool _hoverMarkdown = true;

    /// <summary>補完の説明を Markdown で送るかどうか。</summary>
    private bool _completionMarkdown = true;

    /// <summary>
    /// エディタが Markdown を受け取れるかを、初期化の要求の textDocument の機能から読む。
    /// </summary>
    /// <param name="message">initialize の要求。</param>
    /// <param name="path">textDocument から形式の配列までのプロパティの名前。</param>
    /// <returns>
    /// 形式の配列に <c>markdown</c> があれば <see langword="true"/>。
    /// 配列が無ければ、受け取れるものとして <see langword="true"/> (申告しないエディタの表示を変えない)。
    /// </returns>
    private static bool AcceptsMarkdown(JsonElement message, params string[] path)
    {
        if (!message.TryGetProperty("params", out JsonElement current)
            || !current.TryGetProperty("capabilities", out current)
            || !current.TryGetProperty("textDocument", out current))
        {
            return true;
        }

        foreach (string name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return true;
            }
        }

        return current.ValueKind != JsonValueKind.Array
               || current.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.String && f.GetString() == "markdown");
    }

    /// <summary>
    /// 処理が例外で終わったことを、エディタの出力へ記録する。
    /// </summary>
    /// <param name="operation">失敗した処理の名前。「ホバーの組み立て」のように書く。</param>
    /// <param name="exception">起きた例外。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// <para>
    /// <b>常駐は続けるが、何も伝えないままにはしない。</b>
    /// 1 つの要求で落ちても、サーバーを止めれば編集中のすべての機能が消える。
    /// だから例外は受け止める。ただ受け止めて捨てるだけだと、
    /// 利用者には「ホバーが出ない」としか見えず、不具合の報告を受けても原因を追えない。
    /// </para>
    /// <para>
    /// <c>window/logMessage</c> は通知を出さず、出力パネル (VS Code では「Shaderlyn」) に残るだけである。
    /// 打鍵のたびに走る処理で失敗が続いても、利用者の作業を遮らない。
    /// </para>
    /// </remarks>
    private Task LogFailureAsync(string operation, Exception exception)
        => LogAsync(LogMessageTypeError, $"{operation}に失敗しました: {exception}");

    /// <summary>記録 (window/logMessage の Log) を送る。エディタの出力の欄に出る。</summary>
    /// <param name="message">記録する文。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    private Task LogAsync(string message) => LogAsync(LogMessageTypeLog, message);

    /// <summary>記録を送る。</summary>
    /// <param name="type">記録の種類 (window/logMessage の type)。</param>
    /// <param name="message">記録する文。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    private async Task LogAsync(int type, string message)
    {
        try
        {
            await _connection.WriteAsync(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("method", "window/logMessage");

                writer.WriteStartObject("params");
                writer.WriteNumber("type", type);
                writer.WriteString("message", message);
                writer.WriteEndObject();

                writer.WriteEndObject();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 記録を送れないなら、接続そのものが壊れている。ここで投げても受け手がいない。
        }
    }

    /// <summary>
    /// 条件が外れて効いていない範囲をエディタへ送る。
    /// </summary>
    /// <param name="uri">対象のファイルの URI。</param>
    /// <param name="regions">送る範囲。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// LSP にこの通知は無いので独自に定める。
    /// 診断と同じく URI ごとに全件を置き換えるので、範囲が無くなったときも空で送る。
    /// </remarks>
    private async Task PublishInactiveRegionsAsync(
        string uri,
        ImmutableArray<InactiveLineRange> regions,
        CancellationToken cancellationToken)
    {
        await _connection.WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", "shaderlyn/inactiveRegions");

            writer.WriteStartObject("params");
            writer.WriteString("uri", uri);
            writer.WriteStartArray("regions");

            foreach (InactiveLineRange region in regions)
            {
                writer.WriteStartObject();
                WritePosition(writer, "start", new LinePosition(region.StartLine, 0));
                WritePosition(writer, "end", new LinePosition(region.EndLine, 0));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 対応している機能を返す。
    /// </summary>
    /// <param name="message">initialize の要求。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// <para>
    /// スクリプトの同期は全文で受け取る。差分で受け取っても、
    /// 解析は毎回全文を見るため組み立て直す手間が増えるだけである。
    /// </para>
    /// <para>
    /// 初期化の設定で <c>languageFeatures: false</c> を受け取ったら、指摘とコードアクションだけを名乗る (<see cref="_languageFeatures"/>)。
    /// </para>
    /// </remarks>
    private async Task RespondInitializeAsync(JsonElement message, CancellationToken cancellationToken)
    {
        await _connection.WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, message);

            writer.WriteStartObject("result");
            writer.WriteStartObject("capabilities");

            // 開く・閉じる・保存の通知を受け取り、変更は全文で受け取る (change: 1 = Full)。
            // 数値だけ (textDocumentSync: 1) で名乗ると、Visual Studio は開く・閉じるの通知 (didOpen・didClose) を送らない。
            writer.WriteStartObject("textDocumentSync");
            writer.WriteBoolean("openClose", true);
            writer.WriteNumber("change", 1);
            writer.WriteStartObject("save");
            writer.WriteBoolean("includeText", false);
            writer.WriteEndObject();
            writer.WriteEndObject();

            if (_languageFeatures)
            {
                writer.WriteBoolean("hoverProvider", true);
                writer.WriteBoolean("definitionProvider", true);
                writer.WriteBoolean("referencesProvider", true);
                writer.WriteBoolean("renameProvider", true);

                writer.WriteStartObject("completionProvider");
                writer.WriteStartArray("triggerCharacters");
                writer.WriteStringValue(".");

                // # を打った時点で指令の名前を出す (CompletionPlaces)。
                writer.WriteStringValue("#");
                writer.WriteEndArray();
                writer.WriteEndObject();

                writer.WriteStartObject("signatureHelpProvider");
                writer.WriteStartArray("triggerCharacters");
                writer.WriteStringValue("(");
                writer.WriteStringValue(",");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteStartObject("codeActionProvider");
            writer.WriteStartArray("codeActionKinds");
            writer.WriteStringValue("quickfix");
            writer.WriteEndArray();
            writer.WriteEndObject();

            // 指摘は textDocument/publishDiagnostics で送る。diagnosticProvider (要求で取りに来る方式) は名乗らない。
            // 名乗ると、Visual Studio は要求だけで指摘を取りに来て、通知の指摘を表示しない。
            writer.WriteEndObject();

            writer.WriteStartObject("serverInfo");
            writer.WriteString("name", "shaderlyn");
            writer.WriteEndObject();

            writer.WriteEndObject();
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>空の応答を返す。</summary>
    /// <param name="message">対応する要求。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    private async Task RespondNullAsync(JsonElement message, CancellationToken cancellationToken)
    {
        await _connection.WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, message);
            writer.WriteNull("result");
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 診断をエディタへ送る。
    /// </summary>
    /// <param name="uri">対象のファイルの URI。</param>
    /// <param name="diagnostics">送る診断。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// LSP は URI ごとに全件を置き換える。
    /// 空で送ることが「このファイルには指摘が無い」を意味するので、
    /// 指摘が消えたときも必ず送らなければならない。
    /// </remarks>
    private async Task PublishDiagnosticsAsync(
        string uri,
        ImmutableArray<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        lock (_diagnosticsGate)
        {
            _lastDiagnostics[uri] = diagnostics;
        }

        await _connection.WriteAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", "textDocument/publishDiagnostics");

            writer.WriteStartObject("params");
            writer.WriteString("uri", uri);
            writer.WriteStartArray("diagnostics");

            foreach (Diagnostic diagnostic in diagnostics)
            {
                WriteDiagnostic(writer, diagnostic);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 1 つのスクリプトを解析した結果を、指摘があるファイルごとに送る。
    /// </summary>
    /// <param name="document">解析したスクリプト。</param>
    /// <param name="stage">解析の深さ。</param>
    /// <param name="diagnostics">解析で出た指摘。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// <para>
    /// <b>指摘はそれがあるファイルの URI へ送る。</b>
    /// 利用者が書いて取り込んだヘッダの指摘は、そのヘッダを指している。
    /// 解析したスクリプトへ送ると、別のファイルの行番号のまま、そのスクリプトの同じ行に出てしまう。
    /// </para>
    /// <para>
    /// <b>ヘッダへの指摘を更新するのは、完全な解析のときだけである。</b>
    /// 入力のたびの解析は取り込みを展開しないので、ヘッダの指摘は 1 件も出ない。
    /// その結果で置き換えると、打つたびにヘッダの指摘が消える。
    /// </para>
    /// </remarks>
    private async Task PublishAnalysisAsync(
        OpenDocument document,
        AnalysisDepth stage,
        ImmutableArray<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        string ownPath = document.Text.FilePath;
        List<(string Uri, ImmutableArray<Diagnostic> Diagnostics)> updates;

        lock (_diagnosticsGate)
        {
            _ownDiagnostics[document.Uri] =
                [.. diagnostics.Where(d => string.Equals(d.Location.FilePath, ownPath, StringComparison.Ordinal))];

            List<string> targets = [document.Uri];

            if (stage == AnalysisDepth.Full)
            {
                ImmutableArray<(string Uri, ImmutableArray<Diagnostic> Diagnostics)> included =
                [
                    .. diagnostics
                        .Where(d => !string.Equals(d.Location.FilePath, ownPath, StringComparison.Ordinal))
                        .GroupBy(d => d.Location.FilePath, StringComparer.Ordinal)
                        .Select(g => (DocumentUri.FromFilePath(g.Key, document.Uri), g.ToImmutableArray())),
                ];

                targets.AddRange(ReplaceIncludedDiagnostics(document.Uri, included));
            }

            updates = [.. targets.Distinct(StringComparer.Ordinal).Select(u => (u, MergedDiagnostics(u)))];
        }

        foreach ((string target, ImmutableArray<Diagnostic> merged) in updates)
        {
            await PublishDiagnosticsAsync(target, merged, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>直近にそのファイルへ送った指摘を取り出す。</summary>
    /// <param name="uri">対象のファイルの URI。</param>
    /// <param name="diagnostics">送った指摘。</param>
    /// <returns>送ったことがあれば <see langword="true"/>。</returns>
    private bool TryGetLastDiagnostics(string uri, out ImmutableArray<Diagnostic> diagnostics)
    {
        lock (_diagnosticsGate)
        {
            return _lastDiagnostics.TryGetValue(uri, out diagnostics);
        }
    }

    /// <summary>あるスクリプトがヘッダへ送る指摘を置き換える。</summary>
    /// <param name="sourceUri">取り込む側のスクリプト。</param>
    /// <param name="included">ヘッダの URI ごとの、新しい指摘。</param>
    /// <returns>指摘が変わりうるヘッダの URI。前に送っていたものと、今回送るもの。</returns>
    /// <remarks><see cref="_diagnosticsGate"/> を取ったまま呼ぶこと。</remarks>
    private List<string> ReplaceIncludedDiagnostics(
        string sourceUri,
        ImmutableArray<(string Uri, ImmutableArray<Diagnostic> Diagnostics)> included)
    {
        List<string> affected = [];

        foreach ((string headerUri, Dictionary<string, ImmutableArray<Diagnostic>> bySource) in _includedDiagnostics)
        {
            if (bySource.Remove(sourceUri))
            {
                affected.Add(headerUri);
            }
        }

        foreach ((string headerUri, ImmutableArray<Diagnostic> found) in included)
        {
            if (!_includedDiagnostics.TryGetValue(headerUri, out Dictionary<string, ImmutableArray<Diagnostic>>? bySource))
            {
                bySource = new Dictionary<string, ImmutableArray<Diagnostic>>(StringComparer.Ordinal);
                _includedDiagnostics[headerUri] = bySource;
            }

            bySource[sourceUri] = found;
            affected.Add(headerUri);
        }

        foreach (string headerUri in affected.Where(u => _includedDiagnostics.TryGetValue(u, out var s) && s.Count == 0).ToList())
        {
            _includedDiagnostics.Remove(headerUri);
        }

        return affected;
    }

    /// <summary>そのファイルへ送る指摘を、そのファイル自身の解析と、取り込むスクリプトの解析から合わせる。</summary>
    /// <param name="uri">対象のファイルの URI。</param>
    /// <returns>同じ位置の同じ指摘を 1 件にまとめたもの。</returns>
    /// <remarks><see cref="_diagnosticsGate"/> を取ったまま呼ぶこと。</remarks>
    private ImmutableArray<Diagnostic> MergedDiagnostics(string uri)
    {
        IEnumerable<Diagnostic> all = _ownDiagnostics.TryGetValue(uri, out ImmutableArray<Diagnostic> own) ? own : [];

        if (_includedDiagnostics.TryGetValue(uri, out Dictionary<string, ImmutableArray<Diagnostic>>? bySource))
        {
            all = all.Concat(bySource.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => p.Value));
        }

        // まとめるときは、取り込みの経路を足し合わせる (どのシェーダーのどの Pass から取り込んだときの指摘か)。
        Dictionary<(string Id, TextSpan Span, string Message), int> seen = [];
        List<Diagnostic> merged = [];

        foreach (Diagnostic diagnostic in all)
        {
            (string, TextSpan, string) key = (diagnostic.Id, diagnostic.Location.Span, diagnostic.GetMessage());

            if (!seen.TryGetValue(key, out int index))
            {
                seen[key] = merged.Count;
                merged.Add(diagnostic);
            }
            else if (!diagnostic.IncludeRoutes.IsEmpty)
            {
                merged[index] = merged[index].WithIncludeRoutes(
                    [.. merged[index].IncludeRoutes.Concat(diagnostic.IncludeRoutes).DistinctBy(r => r.Key)]);
            }
        }

        return [.. merged];
    }

    /// <summary>
    /// 診断 1 件を LSP の形で書き出す。
    /// </summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="diagnostic">対象の診断。</param>
    /// <remarks>
    /// <b>ルールのドキュメントへの導線を必ず付ける。</b>
    /// 「なぜ問題なのか」が読めない指摘は、理解されないまま抑制されるか、
    /// 意味が分からないまま機械的に潰されて別の不具合を生む。
    /// </remarks>
    private static void WriteDiagnostic(Utf8JsonWriter writer, Diagnostic diagnostic)
    {
        LinePositionSpan span = diagnostic.Location.LineSpan;

        writer.WriteStartObject();

        writer.WriteStartObject("range");
        WritePosition(writer, "start", span.Start);
        WritePosition(writer, "end", span.End);
        writer.WriteEndObject();

        writer.WriteNumber("severity", ToLspSeverity(diagnostic.Severity));
        writer.WriteString("code", diagnostic.Id);
        writer.WriteString("source", "shaderlyn");
        writer.WriteString("message", diagnostic.GetMessage());

        if (diagnostic.Descriptor.HelpLinkUri is { Length: > 0 } helpLink)
        {
            writer.WriteStartObject("codeDescription");
            writer.WriteString("href", helpLink);
            writer.WriteEndObject();
        }

        // ヘッダの中の指摘は、取り込んだシェーダーの #include の行を関連する場所として添える (エディタでクリックして飛べる)。
        if (!diagnostic.IncludeRoutes.IsEmpty)
        {
            writer.WriteStartArray("relatedInformation");

            foreach (IncludeRoute route in diagnostic.IncludeRoutes)
            {
                for (int i = 0; i < route.Includes.Length; i++)
                {
                    Location step = route.Includes[i];
                    LinePositionSpan stepSpan = step.LineSpan;

                    writer.WriteStartObject();
                    writer.WriteStartObject("location");
                    writer.WriteString("uri", DocumentUri.FromFilePath(step.FilePath));
                    writer.WriteStartObject("range");
                    WritePosition(writer, "start", stepSpan.Start);
                    WritePosition(writer, "end", stepSpan.End);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteString(
                        "message",
                        i == 0
                            ? $"{Path.GetFileName(route.FilePath)}{(route.Origin is { } origin ? " の " + origin : string.Empty)} から取り込んでいます"
                            : "そこから取り込んでいます");
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    /// <summary>位置を書き出す。</summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="name">プロパティ名。</param>
    /// <param name="position">対象の位置。</param>
    /// <remarks>LSP の行と桁は 0 始まりであり、このツールの内部表現と同じである。</remarks>
    private static void WritePosition(Utf8JsonWriter writer, string name, LinePosition position)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("line", position.Line);
        writer.WriteNumber("character", position.Character);
        writer.WriteEndObject();
    }

    /// <summary>重要度を LSP の値へ直す。</summary>
    /// <param name="severity">このツールの重要度。</param>
    /// <returns>LSP の重要度。</returns>
    private static int ToLspSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => 1,
        DiagnosticSeverity.Warning => 2,
        DiagnosticSeverity.Info => 3,
        _ => 4,
    };

    /// <summary>要求の id をそのまま応答に入れる。</summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="message">対応する要求。</param>
    /// <remarks>id は数値と文字列のどちらもありうる。型を決め打ちすると応答が捨てられる。</remarks>
    private static void WriteId(Utf8JsonWriter writer, JsonElement message)
    {
        if (!message.TryGetProperty("id", out JsonElement id))
        {
            return;
        }

        writer.WritePropertyName("id");
        id.WriteTo(writer);
    }
}
