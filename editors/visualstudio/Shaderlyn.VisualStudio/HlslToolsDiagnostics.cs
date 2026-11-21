using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Languages;
using Microsoft.VisualStudio.RpcContracts.DiagnosticManagement;
using Microsoft.VisualStudio.RpcContracts.Documents;
using StreamJsonRpc;
using RpcRange = Microsoft.VisualStudio.RpcContracts.Utilities.Range;

namespace Shaderlyn.VisualStudio;

/// <summary>
/// HLSL Tools が自分の種類 (HLSL) として引き受けるファイルに、Shaderlyn の指摘を出す。
/// </summary>
/// <remarks>
/// <para>
/// HLSL Tools (「C++ によるゲーム開発」ワークロードに入る) は .hlsl・.hlsli・.cginc・.compute・.usf・.ush などを引き受ける。
/// Visual Studio の言語サーバーは、言語サーバー用の種類から派生したファイルにしか付かないので、これらのファイルには付けられない。
/// </para>
/// <para>
/// そこで、ファイルの内容を拡張機能が自分で起動した Shaderlyn のサーバーへ送り、
/// 返ってきた指摘を Visual Studio の指摘の報告 (<see cref="DiagnosticsReporter"/>) で波線とエラー一覧に出す。
/// 補完・ホバーなどは HLSL Tools のものを使い、サーバーには <c>languageFeatures: false</c> を渡す。
/// </para>
/// </remarks>
[VisualStudioContribution]
internal sealed class HlslToolsDiagnostics : ExtensionPart, ITextViewOpenClosedListener, ITextViewChangedListener
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Dictionary<Uri, int> _versions = [];
    private JsonRpc? _rpc;
    private Process? _process;
    private DiagnosticsReporter? _reporter;
    private IDisposable? _documentEvents;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlslToolsDiagnostics"/> class.
    /// </summary>
    /// <param name="extension">拡張機能。</param>
    /// <param name="extensibility">Visual Studio の拡張の入口。</param>
    public HlslToolsDiagnostics(Extension extension, VisualStudioExtensibility extensibility)
        : base(extension, extensibility)
    {
    }

    /// <inheritdoc/>
    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo = [DocumentFilter.FromDocumentType("HLSL")],
    };

    /// <inheritdoc/>
    public async Task TextViewOpenedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
    {
        if (await EnsureStartedAsync(cancellationToken).ConfigureAwait(false) is not { } rpc)
        {
            return;
        }

        Uri uri = textView.Document.Uri;
        int version = NextVersion(uri);
        await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = uri.AbsoluteUri, languageId = "hlsl", version, text = textView.Document.Text.CopyToString() },
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task TextViewChangedAsync(TextViewChangedArgs args, CancellationToken cancellationToken)
    {
        // カーソルの移動などでも呼ばれる。中身が変わっていなければ送らない。
        // 送ると、サーバーは構文だけを解析し直し、開いたときに出した意味解析の指摘が消える。
        if (_rpc is not { } rpc || args.Edits.Count == 0)
        {
            return;
        }

        ITextDocumentSnapshot document = args.AfterTextView.Document;
        int version = NextVersion(document.Uri);
        await rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new
        {
            textDocument = new { uri = document.Uri.AbsoluteUri, version },
            contentChanges = new[] { new { text = document.Text.CopyToString() } },
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task TextViewClosedAsync(ITextViewSnapshot textView, CancellationToken cancellationToken)
    {
        Uri uri = textView.Document.Uri;

        lock (_versions)
        {
            _versions.Remove(uri);
        }

        if (_rpc is { } rpc)
        {
            await rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new
            {
                textDocument = new { uri = uri.AbsoluteUri },
            }).ConfigureAwait(false);
        }

        if (_reporter is { } reporter)
        {
            await reporter.ClearDiagnosticsAsync(uri, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing)
        {
            _documentEvents?.Dispose();
            _rpc?.Dispose();

            if (_process is { HasExited: false } process)
            {
                process.Kill();
            }

            _process?.Dispose();
            _reporter?.Dispose();
            _startGate.Dispose();
        }

        base.Dispose(isDisposing);
    }

    private int NextVersion(Uri uri)
    {
        lock (_versions)
        {
            int version = _versions.TryGetValue(uri, out int current) ? current + 1 : 1;
            _versions[uri] = version;
            return version;
        }
    }

    /// <summary>サーバーを起動して初期化する。起動できなければ <see langword="null"/>。</summary>
    private async Task<JsonRpc?> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_rpc is { } started)
        {
            return started;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rpc is { } again)
            {
                return again;
            }

            if (ServerProcess.Start() is not { } process)
            {
                return null;
            }

            (Stream fromServer, Stream toServer) = ServerProcess.Streams(process, "hlsl-tools");

            _process = process;
            _reporter = Extensibility.Languages().GetDiagnosticsReporter("Shaderlyn.HlslTools");

            JsonRpc rpc = new(new HeaderDelimitedMessageHandler(toServer, fromServer, new SystemTextJsonFormatter()));
            rpc.AddLocalRpcMethod("textDocument/publishDiagnostics", new Func<string, JsonElement, Task>(OnPublishDiagnosticsAsync));
            rpc.StartListening();

            await rpc.InvokeWithParameterObjectAsync<JsonElement>(
                "initialize",
                new
                {
                    processId = Environment.ProcessId,
                    rootUri = (string?)null,
                    capabilities = new { },
                    initializationOptions = new { languageFeatures = false },
                },
                cancellationToken).ConfigureAwait(false);
            await rpc.NotifyAsync("initialized", new { }).ConfigureAwait(false);

            // 保存を受け取る。サーバーは保存したときに意味解析まで走らせる。
            _documentEvents = await Extensibility.Documents()
                .SubscribeAsync(new SaveListener(this), filterRegex: null, cancellationToken).ConfigureAwait(false);
            _rpc = rpc;
            return rpc;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task OnPublishDiagnosticsAsync(string uri, JsonElement diagnostics)
    {
        if (_reporter is not { } reporter)
        {
            return;
        }

        Uri document = new(uri);
        List<DocumentDiagnostic> reported = [];

        foreach (JsonElement diagnostic in diagnostics.EnumerateArray())
        {
            JsonElement range = diagnostic.GetProperty("range");
            JsonElement start = range.GetProperty("start");
            JsonElement end = range.GetProperty("end");

            reported.Add(new DocumentDiagnostic(
                document,
                new RpcRange(
                    start.GetProperty("line").GetInt32(),
                    start.GetProperty("character").GetInt32(),
                    end.GetProperty("line").GetInt32(),
                    end.GetProperty("character").GetInt32()),
                diagnostic.GetProperty("message").GetString() ?? string.Empty)
            {
                ErrorCode = diagnostic.TryGetProperty("code", out JsonElement code) ? code.GetString() : null,
                Severity = diagnostic.TryGetProperty("severity", out JsonElement severity) ? ToSeverity(severity.GetInt32()) : DiagnosticSeverity.Warning,
                ProviderName = "Shaderlyn",
                HelpLink = diagnostic.TryGetProperty("codeDescription", out JsonElement description)
                    && description.TryGetProperty("href", out JsonElement href) ? href.GetString() : null,
            });
        }

        await reporter.ClearDiagnosticsAsync(document, CancellationToken.None).ConfigureAwait(false);
        await reporter.ReportDiagnosticsAsync(reported, CancellationToken.None).ConfigureAwait(false);
    }

    private static DiagnosticSeverity ToSeverity(int lsp) => lsp switch
    {
        1 => DiagnosticSeverity.Error,
        2 => DiagnosticSeverity.Warning,
        3 => DiagnosticSeverity.Information,
        _ => DiagnosticSeverity.Hint,
    };

    /// <summary>開いているファイルが保存されたら、サーバーに知らせる。</summary>
    private async Task OnSavedAsync(Uri uri)
    {
        // 保存の通知の URI には、Visual Studio が文書のバージョン (?vs-version=4 など) を付けてくる。外して比較する。
        uri = new Uri(uri.GetLeftPart(UriPartial.Path));
        bool opened;

        lock (_versions)
        {
            opened = _versions.ContainsKey(uri);
        }

        if (opened && _rpc is { } rpc)
        {
            await rpc.NotifyWithParameterObjectAsync("textDocument/didSave", new
            {
                textDocument = new { uri = uri.AbsoluteUri },
            }).ConfigureAwait(false);
        }
    }

    /// <summary>ファイルの保存を受け取る。ほかの出来事は使わない。</summary>
    private sealed class SaveListener(HlslToolsDiagnostics owner) : IDocumentEventsListener
    {
        public Task SavedAsync(DocumentEventArgs e, CancellationToken token) => owner.OnSavedAsync(e.Moniker);

        public Task SavingAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public Task OpenedAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public Task ClosedAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public Task ShownAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public Task HiddenAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public Task RenamedAsync(RenamedDocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
