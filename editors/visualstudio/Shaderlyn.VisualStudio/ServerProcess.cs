using System.Diagnostics;
using System.Reflection;

namespace Shaderlyn.VisualStudio;

/// <summary>
/// 拡張機能に同梱した Shaderlyn のサーバー (server/shaderlyn-lsp.exe) を起動する。
/// </summary>
/// <remarks>
/// <para>
/// 環境変数 <c>SHADERLYN_SERVER_PATH</c> に実行ファイルを指定すると、同梱したものの代わりにそれを起動する (開発中のサーバーを試すときなど)。
/// 設定の画面には置かない。設定は .shaderlyn.yaml だけにする方針で、実行ファイルの場所はリポジトリのファイルに書かせない。
/// </para>
/// <para>
/// 環境変数 <c>SHADERLYN_LSP_LOG</c> にフォルダーを指定すると、やり取りをそこへ書き出す (確認用)。
/// </para>
/// </remarks>
internal static class ServerProcess
{
    /// <summary>同梱したものの代わりに使うサーバーを指定する環境変数。</summary>
    private const string ServerPathVariable = "SHADERLYN_SERVER_PATH";

    /// <summary>サーバーを起動する。実行ファイルがないか起動できなければ <see langword="null"/>。</summary>
    /// <returns>起動したプロセス。</returns>
    public static Process? Start()
    {
        string server = Environment.GetEnvironmentVariable(ServerPathVariable) is { } specified && !string.IsNullOrWhiteSpace(specified)
            ? specified
            : Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "server", "shaderlyn-lsp.exe");

        if (!File.Exists(server))
        {
            return null;
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
            process.Dispose();
            return null;
        }

        return process;
    }

    /// <summary>サーバーとやり取りするストリームを返す。記録が有効なら、やり取りをファイルにも書き出す。</summary>
    /// <param name="process">起動したサーバー。</param>
    /// <param name="logName">記録のファイル名に付ける名前。</param>
    /// <returns>サーバーから読むストリームと、サーバーへ書くストリーム。</returns>
    public static (Stream FromServer, Stream ToServer) Streams(Process process, string logName)
    {
        Stream fromServer = process.StandardOutput.BaseStream;
        Stream toServer = process.StandardInput.BaseStream;

        if (Environment.GetEnvironmentVariable("SHADERLYN_LSP_LOG") is { Length: > 0 } logDirectory)
        {
            Directory.CreateDirectory(logDirectory);
            fromServer = new LoggingStream(fromServer, Path.Combine(logDirectory, $"{logName}-from-server.log"));
            toServer = new LoggingStream(toServer, Path.Combine(logDirectory, $"{logName}-to-server.log"));
        }

        return (fromServer, toServer);
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

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            await _log.WriteAsync(buffer, offset, read, cancellationToken).ConfigureAwait(false);
            await _log.FlushAsync(cancellationToken).ConfigureAwait(false);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            _log.Write(buffer, offset, count);
            _log.Flush();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            await _log.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
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
