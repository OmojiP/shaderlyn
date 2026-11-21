using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
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
    /// <inheritdoc/>
    public event AsyncEventHandler<EventArgs>? StartAsync;

#pragma warning disable CS0067 // サーバーを止めることはないので、StopAsync は呼ばない。

    /// <inheritdoc/>
    public event AsyncEventHandler<EventArgs>? StopAsync;

#pragma warning restore CS0067

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
    public Task<Connection?> ActivateAsync(CancellationToken token)
    {
        if (ServerProcess.Start() is not { } process)
        {
            return Task.FromResult<Connection?>(null);
        }

        (Stream fromServer, Stream toServer) = ServerProcess.Streams(process, LogName);
        return Task.FromResult<Connection?>(new Connection(fromServer, toServer));
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
