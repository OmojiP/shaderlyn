using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Shaderlyn.VisualStudio;

/// <summary>
/// Shaderlyn の言語サーバーを起動する。
/// </summary>
/// <remarks>
/// <para>
/// 設定のページ (ツール &gt; オプション &gt; Shaderlyn) に実行ファイルがあればそれを、なければ拡張機能に同梱したもの
/// (server/shaderlyn-lsp.exe。arm64 の Visual Studio では server/arm64/shaderlyn-lsp.exe) を起動する。実行ファイルの場所はユーザーごとの設定にだけ書ける。
/// リポジトリのファイル (.shaderlyn.yaml) には書かせない。
/// </para>
/// <para>
/// 設定の実行ファイルが見つからない場合は、そのことを伝えて同梱したものを起動する。
/// 起動を失敗させると、Visual Studio は「クライアントが有効な接続を返しませんでした」(InvalidOperationException) という通知を出し、
/// 前のサーバーの指摘も画面に残り続ける。
/// </para>
/// </remarks>
internal static class ServerProcess
{
    /// <summary>拡張機能に同梱した言語サーバー。</summary>
    /// <remarks>
    /// arm64 の Visual Studio では arm64 のものを使う。入っていない場合 (手元で x64 のものだけを入れて組んだ場合) は、
    /// x64 のものを起動する (Windows 11 の arm64 はエミュレーションで x64 を実行できる)。
    /// x64 の Visual Studio をエミュレーションで動かしている場合は、プロセスが x64 なので x64 のものを使う。
    /// </remarks>
    private static string BundledServer
    {
        get
        {
            string server = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "server");
            string arm64 = Path.Combine(server, "arm64", "shaderlyn-lsp.exe");

            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 && File.Exists(arm64)
                ? arm64
                : Path.Combine(server, "shaderlyn-lsp.exe");
        }
    }

    /// <summary>サーバーを起動する。起動できなければ <see langword="null"/>。</summary>
    /// <param name="configuredPath">設定のページの実行ファイル。未設定なら <see langword="null"/>。</param>
    /// <returns>起動したプロセス。</returns>
    public static async Task<Process?> StartAsync(string? configuredPath)
    {
        string server = BundledServer;

        if (configuredPath is not null)
        {
            if (File.Exists(configuredPath))
            {
                server = configuredPath;
            }
            else
            {
                await ReportMissingAsync(configuredPath).ConfigureAwait(false);
            }
        }

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

    /// <summary>最後に「見つからない」と伝えたパス。言語クライアントが 2 つあるので、同じパスでは 1 度だけ伝える。</summary>
    private static string? s_reportedMissing;

    /// <summary>設定の実行ファイルが見つからないことを、メッセージで伝える。</summary>
    /// <param name="server">見つからなかった実行ファイル。</param>
    /// <returns>完了を表すタスク。</returns>
    private static async Task ReportMissingAsync(string server)
    {
        if (Interlocked.Exchange(ref s_reportedMissing, server) == server)
        {
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        VsShellUtilities.ShowMessageBox(
            ServiceProvider.GlobalProvider,
            $"設定の言語サーバーが見つからないため、拡張機能に同梱したものを使います。\n\n{server}\n\n"
            + "ツール > オプション > Shaderlyn の「実行ファイル」を確認してください。",
            "Shaderlyn",
            OLEMSGICON.OLEMSGICON_WARNING,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    /// <summary>見つからないと伝えたパスを忘れる。設定を変えたあと、また見つからなければもう一度伝える。</summary>
    public static void ForgetReportedMissing() => Interlocked.Exchange(ref s_reportedMissing, null);

    /// <summary>サーバーとやり取りするストリームを返す。</summary>
    /// <param name="process">起動したサーバー。</param>
    /// <param name="logName">記録のファイル名に付ける名前 (Debug のビルドだけ)。</param>
    /// <returns>サーバーから読むストリームと、サーバーへ書くストリーム。</returns>
    /// <remarks>
    /// Debug のビルドでは、環境変数 <c>SHADERLYN_LSP_LOG</c> にフォルダーを指定すると、やり取りをそこへ書き出す (開発用)。
    /// </remarks>
    public static (Stream FromServer, Stream ToServer) Streams(Process process, string logName)
    {
        Stream fromServer = process.StandardOutput.BaseStream;
        Stream toServer = process.StandardInput.BaseStream;

#if DEBUG
        if (Environment.GetEnvironmentVariable("SHADERLYN_LSP_LOG") is { Length: > 0 } logDirectory)
        {
            Directory.CreateDirectory(logDirectory);
            fromServer = new LoggingStream(fromServer, Path.Combine(logDirectory, $"{logName}-from-server.log"));
            toServer = new LoggingStream(toServer, Path.Combine(logDirectory, $"{logName}-to-server.log"));
        }
#else
        _ = logName;
#endif

        return (fromServer, toServer);
    }

#if DEBUG
    /// <summary>
    /// 読み書きしたバイト列を、ファイルにも書き出すストリーム (開発用)。
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
#endif
}
