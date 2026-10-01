using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Shaderlyn.LanguageServer.Protocol;

/// <summary>
/// Language Server Protocol のメッセージを標準入出力でやり取りする。
/// </summary>
/// <remarks>
/// <para>
/// <b>プロトコルのライブラリを使わず自前で書いている。</b>
/// 単一バイナリで配布するにあたり依存を増やしたくないためで、
/// 設定ファイルの YAML を自前で読んでいるのと同じ判断である。
/// 実際に必要なのは「<c>Content-Length</c> 行 + 空行 + JSON 本体」という枠と、
/// 数えるほどのメソッドだけである。
/// </para>
/// <para>
/// <b>本体は <see cref="JsonDocument"/> で読み、<see cref="Utf8JsonWriter"/> で書く。</b>
/// 型に対応付けて往復させると、AOT では反射が使えず生成器の設定が要る。
/// 診断の出力層が既に同じ書き方をしている。
/// </para>
/// </remarks>
public sealed class LspConnection
{
    private const string ContentLengthHeader = "Content-Length:";

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// 接続を生成する。
    /// </summary>
    /// <param name="input">受信するストリーム。</param>
    /// <param name="output">送信するストリーム。</param>
    public LspConnection(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _input = input;
        _output = output;
    }

    /// <summary>
    /// メッセージを 1 件受け取る。
    /// </summary>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>受け取ったメッセージ。ストリームが閉じた場合は <see langword="null"/>。</returns>
    public async Task<JsonDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        int length = await ReadContentLengthAsync(cancellationToken).ConfigureAwait(false);

        if (length <= 0)
        {
            return null;
        }

        byte[] body = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            await _input.ReadExactlyAsync(body.AsMemory(0, length), cancellationToken).ConfigureAwait(false);

            // ここでコピーする。JsonDocument は渡されたメモリをコピーせず、
            // 値を読むたびに元の領域を見に行く。
            // 借りたままの領域を渡してプールへ返すと、
            // 次に借りた誰かが書き込んだ内容を「受け取ったメッセージ」として読むことになる。
            // 解析は並行して走り、そこでも配列を借りるため、
            // 混み合ったときだけ要求の中身が入れ替わるという形で現れる。
            return JsonDocument.Parse(body.AsSpan(0, length).ToArray());
        }
        catch (JsonException)
        {
            // 壊れたメッセージ 1 件で常駐を落とさない。次のメッセージから読み直す。
            return JsonDocument.Parse("{}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(body);
        }
    }

    /// <summary>
    /// ヘッダを読み、本体の長さを求める。
    /// </summary>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>本体の長さ。ストリームが閉じた場合は 0。</returns>
    /// <remarks>
    /// ヘッダは ASCII の行の並びで、空行で終わる。
    /// <c>Content-Type</c> は現れても無視してよい。
    /// </remarks>
    private async Task<int> ReadContentLengthAsync(CancellationToken cancellationToken)
    {
        int length = 0;

        while (true)
        {
            string? line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                return 0;
            }

            if (line.Length == 0)
            {
                return length;
            }

            if (line.StartsWith(ContentLengthHeader, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[ContentLengthHeader.Length..].Trim(), out int parsed))
            {
                length = parsed;
            }
        }
    }

    /// <summary>ヘッダの 1 行を読む。</summary>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>改行を除いた行。ストリームが閉じた場合は <see langword="null"/>。</returns>
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        StringBuilder line = new();
        byte[] one = new byte[1];

        while (true)
        {
            int read = await _input.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return line.Length > 0 ? line.ToString() : null;
            }

            if (one[0] == (byte)'\n')
            {
                return line.ToString().TrimEnd('\r');
            }

            line.Append((char)one[0]);
        }
    }

    /// <summary>
    /// メッセージを 1 件送る。
    /// </summary>
    /// <param name="write">本体を書く処理。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>送信の完了を表すタスク。</returns>
    /// <remarks>
    /// 解析は並行して走るため、書き込みは 1 件ずつ直列化する。
    /// 途中で混ざると枠が壊れ、以降のすべてのメッセージが読めなくなる。
    /// </remarks>
    public async Task WriteAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        using MemoryStream body = new();
        using (Utf8JsonWriter writer = new(body))
        {
            write(writer);
        }

        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(body.ToArray(), cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
