using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace Shaderlyn.VisualStudio;

/// <summary>
/// Shaderlyn の言語サーバーを起こし、Visual Studio とつなぐ (Shaderlyn のファイルの種類)。
/// </summary>
/// <remarks>
/// ホバーのコードは行内のコードで受け取る (<c>hoverCodeBlocks: false</c>)。
/// Visual Studio はコードブロックに「コードのコピー」の見出しを付け、1 行の宣言でも大きな枠で表示する。
/// </remarks>
[Export(typeof(ILanguageClient))]
[ContentType(ShaderContentType.Name)]
[RunOnContext(RunningContext.RunOnHost)]
internal sealed class ShaderlynLanguageClient : ShaderlynLanguageClientBase
{
    /// <inheritdoc/>
    public override string Name => "Shaderlyn";

    /// <inheritdoc/>
    public override object InitializationOptions => new JObject { ["hoverCodeBlocks"] = false };

    /// <inheritdoc/>
    protected override string LogName => "lsp";
}

/// <summary>
/// HLSL Tools のファイルの種類 (HLSL) に、Shaderlyn の指摘とコードアクションだけを出す。
/// </summary>
/// <remarks>
/// 補完・ホバーなどは HLSL Tools のものを使う。
/// 同じ位置にホバーや補完が 2 つずつ出ないよう、サーバーには <c>languageFeatures: false</c> を渡す。
/// </remarks>
[Export(typeof(ILanguageClient))]
[ContentType(ShaderContentType.HlslToolsName)]
[RunOnContext(RunningContext.RunOnHost)]
internal sealed class HlslToolsLanguageClient : ShaderlynLanguageClientBase
{
    /// <inheritdoc/>
    public override string Name => "Shaderlyn (HLSL Tools)";

    /// <inheritdoc/>
    public override object InitializationOptions => new JObject { ["languageFeatures"] = false };

    /// <inheritdoc/>
    protected override string LogName => "hlsl-tools";
}

/// <summary>
/// 言語サーバーを起こす処理の共通部分。
/// </summary>
internal abstract class ShaderlynLanguageClientBase : ILanguageClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShaderlynLanguageClientBase"/> class.
    /// </summary>
    protected ShaderlynLanguageClientBase()
    {
        // 設定のページで実行ファイルを変えたら、言語サーバーを起動し直す。
        ShaderlynOptions.Changed += (_, _) =>
        {
            ServerProcess.ForgetReportedMissing();
            Task.Run(RestartAsync).FileAndForget("Shaderlyn/Restart");
        };
    }

    /// <inheritdoc/>
    public event AsyncEventHandler<EventArgs>? StartAsync;

    /// <inheritdoc/>
    public event AsyncEventHandler<EventArgs>? StopAsync;

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public IEnumerable<string>? ConfigurationSections => null;

    /// <inheritdoc/>
    public abstract object InitializationOptions { get; }

    /// <inheritdoc/>
    public IEnumerable<string>? FilesToWatch => null;

    /// <inheritdoc/>
    public bool ShowNotificationOnInitializeFailed => true;

    /// <summary>やり取りの記録のファイル名に付ける名前。</summary>
    protected abstract string LogName { get; }

    /// <inheritdoc/>
    public async Task<Connection?> ActivateAsync(CancellationToken token)
    {
        string? configuredPath = await ShaderlynOptions.GetServerPathAsync().ConfigureAwait(false);

        if (await ServerProcess.StartAsync(configuredPath).ConfigureAwait(false) is not { } process)
        {
            return null;
        }

        (Stream fromServer, Stream toServer) = ServerProcess.Streams(process, LogName);
        return new Connection(fromServer, toServer);
    }

    /// <summary>言語サーバーを止めて、起動し直す。</summary>
    /// <returns>完了を表すタスク。</returns>
    private async Task RestartAsync()
    {
        if (StopAsync is { } stop)
        {
            await stop.InvokeAsync(this, EventArgs.Empty).ConfigureAwait(false);
        }

        if (StartAsync is { } start)
        {
            await start.InvokeAsync(this, EventArgs.Empty).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task OnLoadedAsync()
    {
        if (StartAsync is { } start)
        {
            await start.InvokeAsync(this, EventArgs.Empty).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task OnServerInitializedAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        => Task.FromResult<InitializationFailureContext?>(new InitializationFailureContext
        {
            FailureMessage = initializationState.StatusMessage,
        });
}
