using System.Buffers;
using System.Text;
using System.Text.Json;
using Shaderlyn.LanguageServer.Protocol;

namespace Shaderlyn.Tests;

/// <summary>
/// メッセージの枠の読み書きの検証。
/// </summary>
/// <remarks>
/// <b>ここが壊れると、症状は「たまに違う要求として届く」という形で出る。</b>
/// 解析やルールをいくら見直しても原因に辿り着けないので、
/// 枠の層だけを取り出して確かめる。
/// </remarks>
public sealed class LspConnectionTests
{
    [Fact]
    public async Task 受け取ったメッセージは借りた領域を参照しない()
    {
        // JsonDocument は渡されたメモリをコピーせず、値を読むたびに元の領域を見に行く。
        // プールへ返した領域を参照していると、
        // 次に借りた側が書き込んだ内容を「受け取ったメッセージ」として読むことになる。
        // 常駐中は解析が並行して走り、そこでも配列を借りるため、
        // 混み合ったときだけ要求の中身が入れ替わるという形で現れる。
        const string Body = """
            {"jsonrpc":"2.0","id":1,"method":"textDocument/completion","params":{"label":"uv"}}
            """;

        using MemoryStream input = new(Frame(Body));
        LspConnection connection = new(input, Stream.Null);

        using JsonDocument? message = await connection.ReadAsync(CancellationToken.None);

        Assert.NotNull(message);

        // 直前に返された配列を借り直して塗りつぶす。
        Clobber(Encoding.UTF8.GetByteCount(Body));

        Assert.Equal("textDocument/completion", message.RootElement.GetProperty("method").GetString());
        Assert.Equal(
            "uv",
            message.RootElement.GetProperty("params").GetProperty("label").GetString());
    }

    [Fact]
    public async Task 枠のとおりに書き出す()
    {
        using MemoryStream output = new();
        LspConnection connection = new(Stream.Null, output);

        await connection.WriteAsync(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("method", "window/logMessage");
                writer.WriteEndObject();
            },
            CancellationToken.None);

        string written = Encoding.UTF8.GetString(output.ToArray());
        string body = """{"method":"window/logMessage"}""";

        Assert.Equal($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}", written);
    }

    /// <summary>本体に <c>Content-Length</c> の枠を付ける。</summary>
    /// <param name="body">本体。</param>
    /// <returns>枠を付けたバイト列。</returns>
    private static byte[] Frame(string body)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);

        return
        [
            .. Encoding.ASCII.GetBytes($"Content-Length: {content.Length}\r\n\r\n"),
            .. content,
        ];
    }

    /// <summary>同じ大きさの領域を借りて塗りつぶす。</summary>
    /// <param name="length">受け取ったメッセージの長さ。</param>
    /// <remarks>
    /// プールは直前に返した配列を返すことが多い。
    /// 返ってこなかった場合、この検証は何も起きないだけで、誤って失敗はしない。
    /// </remarks>
    private static void Clobber(int length)
    {
        for (int i = 0; i < 4; i++)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);

            rented.AsSpan().Fill((byte)'x');
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
