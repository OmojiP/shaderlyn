using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Shaderlyn.Core.Analysis;
using Shaderlyn.LanguageServer;
using Shaderlyn.LanguageServer.Protocol;

namespace Shaderlyn.Tests;

/// <summary>
/// 言語サーバを同じプロセスで動かし、やり取りを流すための足場。
/// </summary>
/// <remarks>
/// <b>実際のストリームを通す。</b>
/// ハンドラを直接呼ぶ形にすると、
/// メッセージの枠の組み立てと読み取りという最も壊れやすい部分を検証できない。
/// </remarks>
internal sealed class LanguageServerHarness : IAsyncDisposable
{
    /// <summary>やり取りに使うスクリプトの URI。</summary>
    public static string DocumentUri { get; } =
        LanguageServer.DocumentUri.FromFilePath(
            Path.Combine(Path.GetTempPath(), "shaderlyn-lsp-test", "Test.shader"));

    private readonly Pipe _toServer = new();
    private readonly Pipe _fromServer = new();
    private readonly Task<int> _server;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly LspConnection _client;
    private int _version;

    /// <summary>
    /// 足場を用意する。
    /// </summary>
    /// <param name="documentUri">
    /// やり取りに使う URI。省略した場合は <see cref="DocumentUri"/>。
    /// エディタごとの URI の書き方の違いを再現するために差し替える。
    /// </param>
    /// <param name="analyzers">
    /// 実行するアナライザ。省略した場合は組み込みルール。
    /// 自作ルールを足したサーバを再現するために差し替える。
    /// </param>
    public LanguageServerHarness(
        string? documentUri = null,
        IEnumerable<DiagnosticAnalyzer>? analyzers = null)
    {
        Uri = documentUri ?? DocumentUri;

        LspConnection connection = new(_toServer.Reader.AsStream(), _fromServer.Writer.AsStream());

        ShaderLanguageServer server = analyzers is null
            ? new ShaderLanguageServer(connection)
            : new ShaderLanguageServer(connection, analyzers);

        _client = new LspConnection(_fromServer.Reader.AsStream(), _toServer.Writer.AsStream());
        _server = Task.Run(() => server.RunAsync(_cancellation.Token));
    }

    /// <summary>この足場が使っているスクリプトの URI。</summary>
    public string Uri { get; }

    /// <summary>初期化を済ませる。</summary>
    /// <returns>完了を表すタスク。</returns>
    public Task InitializeAsync() => InitializeAsync(null);

    /// <summary>エディタからの設定を添えて初期化を済ませる。</summary>
    /// <param name="initializationOptions">initialize の要求に添える設定。</param>
    /// <returns>完了を表すタスク。</returns>
    public async Task InitializeAsync(object? initializationOptions)
    {
        await SendAsync(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { capabilities = new { }, initializationOptions },
        });
        (await ReceiveAsync()).Dispose();
    }

    /// <summary>スクリプトを開く。</summary>
    /// <param name="text">スクリプトの中身。</param>
    /// <returns>完了を表すタスク。</returns>
    public async Task OpenAsync(string text)
    {
        _version = 1;
        await SendAsync(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new
            {
                textDocument = new { uri = Uri, languageId = "shaderlab", version = _version, text },
            },
        });
    }

    /// <summary>スクリプトを編集する。</summary>
    /// <param name="text">新しい中身。</param>
    /// <returns>完了を表すタスク。</returns>
    public async Task ChangeAsync(string text)
    {
        _version++;
        await SendAsync(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didChange",
            @params = new
            {
                textDocument = new { uri = Uri, version = _version },
                contentChanges = new[] { new { text } },
            },
        });
    }

    /// <summary>メッセージを 1 件送る。</summary>
    /// <param name="message">送る内容。</param>
    /// <returns>完了を表すタスク。</returns>
    public async Task SendAsync(object message)
    {
        string json = JsonSerializer.Serialize(message);
        await _client.WriteAsync(
            writer => JsonDocument.Parse(json).RootElement.WriteTo(writer), CancellationToken.None);
    }

    /// <summary>メッセージを 1 件受け取る。</summary>
    /// <returns>受け取ったメッセージ。</returns>
    public async Task<JsonDocument> ReceiveAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        return await _client.ReadAsync(timeout.Token)
               ?? throw new InvalidOperationException("サーバとの接続が閉じました。");
    }

    /// <summary>
    /// 次に届く診断の通知を待つ。
    /// </summary>
    /// <returns>診断の配列。</returns>
    /// <remarks>応答と通知が混ざって届くため、診断の通知だけを拾う。</remarks>
    public async Task<JsonElement> ReceiveDiagnosticsAsync()
    {
        while (true)
        {
            JsonDocument message = await ReceiveAsync();

            if (message.RootElement.TryGetProperty("method", out JsonElement method)
                && method.GetString() == "textDocument/publishDiagnostics")
            {
                return message.RootElement.GetProperty("params").GetProperty("diagnostics").Clone();
            }

            message.Dispose();
        }
    }

    /// <summary>
    /// 指定した URI への、指定したルールの指摘を含む診断が届くまで待つ。
    /// </summary>
    /// <param name="uri">待つ診断の送り先。</param>
    /// <param name="ruleId">待つルールの ID。</param>
    /// <returns>診断の配列。</returns>
    /// <remarks>
    /// 取り込んだヘッダの指摘は、解析したスクリプトではなくヘッダの URI へ届く。
    /// 送り先を確かめずに受け取ると、どちらに届いたのかを検証できない。
    /// </remarks>
    public async Task<JsonElement> ReceiveDiagnosticsForAsync(string uri, string ruleId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        while (!timeout.IsCancellationRequested)
        {
            using JsonDocument message = await ReceiveAsync();

            if (message.RootElement.TryGetProperty("method", out JsonElement method)
                && method.GetString() == "textDocument/publishDiagnostics"
                && message.RootElement.GetProperty("params").GetProperty("uri").GetString() == uri)
            {
                JsonElement diagnostics = message.RootElement.GetProperty("params").GetProperty("diagnostics");

                if (diagnostics.EnumerateArray().Any(d => d.GetProperty("code").GetString() == ruleId))
                {
                    return diagnostics.Clone();
                }
            }
        }

        throw new InvalidOperationException($"{uri} へ {ruleId} を含む診断が届きませんでした。");
    }

    /// <summary>
    /// 指定したメソッドの通知が次に届くのを待つ。
    /// </summary>
    /// <param name="method">待つ通知のメソッド名。</param>
    /// <returns>通知の params。</returns>
    public async Task<JsonElement> ReceiveNotificationAsync(string method)
    {
        while (true)
        {
            using JsonDocument message = await ReceiveAsync();

            if (message.RootElement.TryGetProperty("method", out JsonElement received)
                && received.GetString() == method)
            {
                return message.RootElement.GetProperty("params").Clone();
            }
        }
    }

    /// <summary>
    /// 指定した文字列の上にカーソルを置いて説明を求める。
    /// </summary>
    /// <param name="source">スクリプトの中身。位置を数えるために使う。</param>
    /// <param name="target">カーソルを置く文字列。</param>
    /// <returns>返ってきた説明。</returns>
    public async Task<string> HoverAsync(string source, string target)
    {
        (int line, int character) = Locate(source, target);

        JsonDocument response = await RequestHoverAsync(line, character);

        return response.RootElement.TryGetProperty("result", out JsonElement result)
               && result.ValueKind == JsonValueKind.Object
            ? result.GetProperty("contents").GetProperty("value").GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>説明を求める要求を送り、応答を受け取る。</summary>
    /// <param name="line">0 始まりの行。</param>
    /// <param name="character">0 始まりの桁。</param>
    /// <returns>応答。</returns>
    public async Task<JsonDocument> RequestHoverAsync(int line, int character)
    {
        await SendAsync(new
        {
            jsonrpc = "2.0",
            id = 100,
            method = "textDocument/hover",
            @params = new
            {
                textDocument = new { uri = Uri },
                position = new { line, character },
            },
        });

        while (true)
        {
            JsonDocument message = await ReceiveAsync();

            if (message.RootElement.TryGetProperty("id", out JsonElement id) && id.GetInt32() == 100)
            {
                return message;
            }

            message.Dispose();
        }
    }

    /// <summary>
    /// 位置を指す要求を送り、応答を受け取る。
    /// </summary>
    /// <param name="method">LSP の要求の名前。</param>
    /// <param name="source">スクリプトの中身。位置を数えるために使う。</param>
    /// <param name="target">カーソルを置く文字列。</param>
    /// <param name="extra">要求に足す項目。</param>
    /// <returns>応答。</returns>
    public async Task<JsonDocument> RequestAtAsync(
        string method,
        string source,
        string target,
        object? extra = null)
    {
        (int line, int character) = Locate(source, target);

        Dictionary<string, object> parameters = new(StringComparer.Ordinal)
        {
            ["textDocument"] = new { uri = Uri },
            ["position"] = new { line, character },
        };

        if (extra is not null)
        {
            foreach (System.Reflection.PropertyInfo property in extra.GetType().GetProperties())
            {
                parameters[property.Name] = property.GetValue(extra)!;
            }
        }

        await SendAsync(new
        {
            jsonrpc = "2.0",
            id = 200,
            method,
            @params = parameters,
        });

        while (true)
        {
            JsonDocument message = await ReceiveAsync();

            if (message.RootElement.TryGetProperty("id", out JsonElement id) && id.GetInt32() == 200)
            {
                return message;
            }

            message.Dispose();
        }
    }

    /// <summary>
    /// 指定したルールの指摘を含む診断が届くまで待つ。
    /// </summary>
    /// <param name="ruleId">待つルールの ID。</param>
    /// <returns>診断の配列。</returns>
    /// <remarks>
    /// <b>診断は 2 回に分けて届く。</b>
    /// 構文だけを見た結果が先に、意味解析まで含めた結果が後から届く。
    /// 意味解析のルールを待つ場合、1 回目だけを見ていると必ず取りこぼす。
    /// </remarks>
    public async Task<JsonElement> ReceiveDiagnosticsForAsync(string ruleId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        while (!timeout.IsCancellationRequested)
        {
            JsonElement diagnostics = await ReceiveDiagnosticsAsync();

            if (diagnostics.EnumerateArray()
                .Any(d => d.GetProperty("code").GetString() == ruleId))
            {
                return diagnostics;
            }
        }

        throw new InvalidOperationException($"{ruleId} を含む診断が届きませんでした。");
    }

    /// <summary>
    /// 指定した文字列の上にカーソルを置いて定義を求める。
    /// </summary>
    /// <param name="source">スクリプトの中身。位置を数えるために使う。</param>
    /// <param name="target">カーソルを置く文字列。</param>
    /// <returns>返ってきた定義の配列。</returns>
    public async Task<JsonElement> DefinitionAsync(string source, string target)
    {
        (int line, int character) = Locate(source, target);

        return await RequestAsync("textDocument/definition", new
        {
            textDocument = new { uri = Uri },
            position = new { line, character },
        });
    }

    /// <summary>
    /// 文字列の位置を行と桁で表す。
    /// </summary>
    /// <param name="source">スクリプトの中身。</param>
    /// <param name="target">探す文字列。</param>
    /// <returns>0 始まりの行と桁。</returns>
    /// <remarks>
    /// <para>語の途中を指す。端はほかの要素と重なりうる。</para>
    /// <para>
    /// <b>目印に改行を含めてはならない。</b>
    /// 検証対象の文字列はソースに書かれた生文字列リテラルであり、
    /// 改行はチェックアウトの設定しだいで LF にも CRLF にもなる。
    /// 改行をまたぐ目印は、環境によって見つかったり見つからなかったりする。
    /// </para>
    /// </remarks>
    private static (int Line, int Character) Locate(string source, string target)
    {
        Assert.DoesNotContain('\n', target);
        Assert.DoesNotContain('\r', target);

        int offset = source.IndexOf(target, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{target}' が見つかりません。");

        offset += target.Length / 2;

        string before = source[..offset];
        return (before.Count(c => c == '\n'), offset - (before.LastIndexOf('\n') + 1));
    }

    /// <summary>
    /// 指摘に対する直しを求める。
    /// </summary>
    /// <param name="diagnostic">対象の指摘。</param>
    /// <returns>返ってきた直しの配列。</returns>
    public async Task<JsonElement> RequestCodeActionsAsync(JsonElement diagnostic)
    {
        return await RequestAsync("textDocument/codeAction", new
        {
            textDocument = new { uri = Uri },
            range = JsonSerializer.Deserialize<JsonElement>(diagnostic.GetProperty("range").GetRawText()),
            context = new { diagnostics = new[] { JsonSerializer.Deserialize<JsonElement>(diagnostic.GetRawText()) } },
        });
    }

    /// <summary>
    /// 範囲を指定して直しを求める。
    /// </summary>
    /// <param name="startLine">範囲の開始行 (0 始まり)。</param>
    /// <param name="startCharacter">範囲の開始桁 (0 始まり)。</param>
    /// <param name="endLine">範囲の終端行 (0 始まり)。</param>
    /// <param name="endCharacter">範囲の終端桁 (0 始まり)。</param>
    /// <param name="ruleId">要求に添えるルール ID。</param>
    /// <returns>返ってきた直しの配列。</returns>
    /// <remarks>
    /// 指摘 1 件の範囲ではなく、利用者が行を選んだ状態を再現するために使う。
    /// </remarks>
    public async Task<JsonElement> RequestCodeActionsAsync(
        int startLine, int startCharacter, int endLine, int endCharacter, string ruleId)
    {
        return await RequestAsync("textDocument/codeAction", new
        {
            textDocument = new { uri = Uri },
            range = new
            {
                start = new { line = startLine, character = startCharacter },
                end = new { line = endLine, character = endCharacter },
            },
            context = new { diagnostics = new[] { new { code = ruleId } } },
        });
    }

    /// <summary>解析の中身を求める。</summary>
    /// <returns>返ってきた HTML。</returns>
    public async Task<string> RequestInspectAsync()
    {
        JsonElement result = await RequestAsync(
            "shaderlyn/inspect", new { textDocument = new { uri = Uri } });

        return result.ValueKind == JsonValueKind.Object
            ? result.GetProperty("html").GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>利用者が選べる条件のシンボルを求める。</summary>
    /// <returns>シンボルの名前、定義済みにしているかどうか、シンボルかどうか。</returns>
    public async Task<(string Name, bool Defined, bool Keyword)[]> RequestConditionSymbolsAsync()
    {
        JsonElement result = await RequestAsync(
            "shaderlyn/conditionSymbols", new { textDocument = new { uri = Uri } });

        return result.ValueKind == JsonValueKind.Object
            ? [.. result.GetProperty("symbols").EnumerateArray()
                .Select(s => (
                    s.GetProperty("name").GetString() ?? string.Empty,
                    s.GetProperty("defined").GetBoolean(),
                    s.GetProperty("declaredByPragma").GetBoolean()))]
            : [];
    }

    /// <summary>条件のシンボルを、同じ <c>#pragma</c> の行のまとまりとともに求める。</summary>
    /// <returns>シンボルの名前、まとまりの番号 (無ければ -1)、その行のどれかが必ず有効かどうか。</returns>
    public async Task<(string Name, int Group, bool RequiresOne)[]> RequestConditionSymbolGroupsAsync()
    {
        JsonElement result = await RequestAsync(
            "shaderlyn/conditionSymbols", new { textDocument = new { uri = Uri } });

        return result.ValueKind == JsonValueKind.Object
            ? [.. result.GetProperty("symbols").EnumerateArray()
                .Select(s => (
                    s.GetProperty("name").GetString() ?? string.Empty,
                    s.TryGetProperty("group", out JsonElement group) ? group.GetInt32() : -1,
                    s.TryGetProperty("requiresOne", out JsonElement requires) && requires.GetBoolean()))]
            : [];
    }

    /// <summary>効いていない範囲をどちらの構成で表示しているかを求める。</summary>
    /// <returns><c>allConfigurations</c> か <c>selectedConfiguration</c>。</returns>
    public async Task<string> RequestConditionSymbolsDisplayAsync()
    {
        JsonElement result = await RequestAsync(
            "shaderlyn/conditionSymbols", new { textDocument = new { uri = Uri } });

        return result.ValueKind == JsonValueKind.Object
            ? result.GetProperty("display").GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>定義済みにするシンボルを送る。</summary>
    /// <param name="symbols">定義済みにするシンボル。空なら定義を足さない状態に戻す。</param>
    /// <returns>完了を表すタスク。</returns>
    public async Task SetDefinedSymbolsAsync(params string[] symbols)
        => await RequestAsync(
            "shaderlyn/setDefinedSymbols", new { textDocument = new { uri = Uri }, symbols });

    /// <summary>シンボルを選び、効いていない範囲をその構成で表示させる。</summary>
    /// <param name="symbols">有効にするシンボル。選ばなかったシンボルは無効として扱われる。</param>
    /// <returns>完了を表すタスク。</returns>
    public async Task SetSelectedConfigurationAsync(params string[] symbols)
        => await RequestAsync(
            "shaderlyn/setDefinedSymbols",
            new { textDocument = new { uri = Uri }, symbols, display = "selectedConfiguration" });

    /// <summary>要求を送り、その応答の結果を受け取る。</summary>
    /// <param name="method">メソッド名。</param>
    /// <param name="parameters">引数。</param>
    /// <returns>応答の結果。</returns>
    private async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        const int id = 200;
        await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters });

        while (true)
        {
            JsonDocument message = await ReceiveAsync();

            if (message.RootElement.TryGetProperty("id", out JsonElement received) && received.GetInt32() == id)
            {
                return message.RootElement.GetProperty("result").Clone();
            }

            message.Dispose();
        }
    }

    /// <summary>サーバの終了を待つ。</summary>
    /// <returns>終了コード。</returns>
    public async Task<int> WaitForExitAsync() => await _server.WaitAsync(TimeSpan.FromSeconds(30));

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        await _toServer.Writer.CompleteAsync();
        await _fromServer.Writer.CompleteAsync();

        try
        {
            await _server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }

        _cancellation.Dispose();
    }
}
