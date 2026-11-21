using System.IO.Pipelines;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.LanguageServer;
using Microsoft.VisualStudio.RpcContracts.LanguageServerProvider;
using Nerdbank.Streams;

namespace Shaderlyn.VisualStudio;

#pragma warning disable VSEXTPREVIEW_LSP // LanguageServerProvider はまだ評価用 (将来変わることがある)。

/// <summary>
/// Shaderlyn の言語サーバーを起こし、Visual Studio とつなぐ。
/// </summary>
/// <remarks>
/// <para>
/// 対象のファイルの拡張子は、VS Code 拡張の documentSelector と揃える
/// (サーバーの ShaderSourceKinds・各環境の Extensions)。
/// </para>
/// <para>
/// HLSL Tools が引き受けるファイル (.hlsl など) には付かない。それらは <see cref="HlslToolsDiagnostics"/> が指摘を出す。
/// </para>
/// </remarks>
[VisualStudioContribution]
internal sealed class ShaderlynLanguageServerProvider : LanguageServerProvider
{
    /// <summary>Shaderlyn が解析するファイルの種類。</summary>
    [VisualStudioContribution]
    public static DocumentTypeConfiguration ShaderDocumentType => new("shaderlyn-shader")
    {
        FileExtensions =
        [
            ".shader", ".hlsl", ".hlsli", ".cginc", ".hlslinc", ".compute", ".usf", ".ush",
            ".vert", ".frag", ".comp", ".geom", ".tesc", ".tese", ".task", ".rgen", ".rint", ".rahit", ".rchit", ".rmiss", ".rcall", ".glsl",
            ".gdshader", ".gdshaderinc", ".slang", ".wgsl",
        ],
        BaseDocumentType = LanguageServerBaseDocumentType,
    };

    /// <inheritdoc/>
    public override LanguageServerProviderConfiguration LanguageServerProviderConfiguration => new(
        "%Shaderlyn.LanguageServerProvider.DisplayName%",
        [DocumentFilter.FromDocumentType(ShaderDocumentType)]);

    /// <inheritdoc/>
    public override Task<IDuplexPipe?> CreateServerConnectionAsync(CancellationToken cancellationToken)
    {
        if (ServerProcess.Start() is not { } process)
        {
            return Task.FromResult<IDuplexPipe?>(null);
        }

        (Stream fromServer, Stream toServer) = ServerProcess.Streams(process, "lsp");
        return Task.FromResult<IDuplexPipe?>(new DuplexPipe(PipeReader.Create(fromServer), PipeWriter.Create(toServer)));
    }

    /// <inheritdoc/>
    public override Task OnServerInitializationResultAsync(
        ServerInitializationResult serverInitializationResult,
        LanguageServerInitializationFailureInfo? initializationFailureInfo,
        CancellationToken cancellationToken)
    {
        if (serverInitializationResult == ServerInitializationResult.Failed)
        {
            // 起動に失敗したまま要求を送り続けないよう、このセッションでは止める。
            Enabled = false;
        }

        return base.OnServerInitializationResultAsync(serverInitializationResult, initializationFailureInfo, cancellationToken);
    }
}
