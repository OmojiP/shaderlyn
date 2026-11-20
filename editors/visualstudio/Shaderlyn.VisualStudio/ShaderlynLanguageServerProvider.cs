using System.Diagnostics;
using System.IO.Pipelines;
using System.Reflection;
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
/// 環境変数 <c>SHADERLYN_LSP_LOG</c> にフォルダーを指定すると、やり取りをそこへ書き出す (試作の確認用)。
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
        string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string server = Path.Combine(directory, "server", "shaderlyn-lsp.exe");

        if (!File.Exists(server))
        {
            return Task.FromResult<IDuplexPipe?>(null);
        }

        ProcessStartInfo info = new(server)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process process = new() { StartInfo = info };

        if (!process.Start())
        {
            return Task.FromResult<IDuplexPipe?>(null);
        }

        Stream fromServer = process.StandardOutput.BaseStream;
        Stream toServer = process.StandardInput.BaseStream;

        if (Environment.GetEnvironmentVariable("SHADERLYN_LSP_LOG") is { Length: > 0 } logDirectory)
        {
            Directory.CreateDirectory(logDirectory);
            fromServer = new LoggingStream(fromServer, Path.Combine(logDirectory, "from-server.log"));
            toServer = new LoggingStream(toServer, Path.Combine(logDirectory, "to-server.log"));
        }

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

    /// <summary>
    /// 読み書きしたバイト列を、ファイルにも書き出すストリーム (試作の確認用)。
    /// </summary>
    private sealed class LoggingStream(Stream inner, string logPath) : Stream
    {
        private readonly FileStream _log = new(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            inner.Flush();
            _log.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            _log.Write(buffer, offset, read);
            _log.Flush();
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _log.WriteAsync(buffer[..read], cancellationToken).ConfigureAwait(false);
            await _log.FlushAsync(cancellationToken).ConfigureAwait(false);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            _log.Write(buffer, offset, count);
            _log.Flush();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _log.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _log.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return inner.FlushAsync(cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _log.Dispose();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
