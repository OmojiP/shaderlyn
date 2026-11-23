using System.Text.Json;

namespace Shaderlyn.Tests;

/// <summary>
/// 言語サーバーが、セマンティックモデルを受信のループの外で、文書ごとに 1 つずつ組み立てること (DocumentModel) の検証。
/// </summary>
public sealed class LanguageServerModelTests
{
    private const string Source = """
        Shader "Test"
        {
            SubShader { Pass { HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
            half4 frag() : SV_Target { float value = 1; return value; }
            ENDHLSL } }
        }
        """;

    [Fact]
    public async Task 編集の合間のホバーにすべて応答する()
    {
        // 組み立てを始める前に新しいバージョンが来たモデルは組み立てない。その要求にも応答を返さなければ、エディタは待ち続ける。
        await using LanguageServerHarness harness = new();
        await harness.InitializeAsync();
        await harness.OpenAsync(Source);

        (int line, int character) = Locate(Source, "value;");
        HashSet<int> pending = [];

        for (int i = 0; i < 5; i++)
        {
            await harness.ChangeAsync(Source + new string(' ', i + 1));

            int id = 300 + i;
            pending.Add(id);
            await harness.SendAsync(new
            {
                jsonrpc = "2.0",
                id,
                method = "textDocument/hover",
                @params = new
                {
                    textDocument = new { uri = harness.Uri },
                    position = new { line, character },
                },
            });
        }

        string? last = null;

        while (pending.Count > 0)
        {
            using JsonDocument message = await harness.ReceiveAsync();

            if (message.RootElement.TryGetProperty("id", out JsonElement id) && pending.Remove(id.GetInt32()))
            {
                Assert.True(message.RootElement.TryGetProperty("result", out JsonElement result));

                if (id.GetInt32() == 304)
                {
                    last = result.ValueKind == JsonValueKind.Object ? result.GetProperty("contents").GetProperty("value").GetString() : null;
                }
            }
        }

        // 最後のバージョンは必ず組み立てるので、説明が返る。
        Assert.NotNull(last);
        Assert.Contains("float", last, StringComparison.Ordinal);
    }

    private static (int Line, int Character) Locate(string source, string target)
    {
        int offset = source.IndexOf(target, StringComparison.Ordinal);
        string before = source[..offset];
        int line = before.Count(c => c == '\n');
        return (line, offset - (before.LastIndexOf('\n') + 1));
    }
}
