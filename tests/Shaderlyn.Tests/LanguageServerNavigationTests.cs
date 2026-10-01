using System.Text.Json;

namespace Shaderlyn.Tests;

/// <summary>
/// 名前をたどる操作 (補完・呼び出しの案内・参照の一覧・名前の変更) の検証。
/// </summary>
/// <remarks>
/// <b>どれも「その構成に何があるか」を答える。</b>
/// <c>#ifdef</c> で守られたメンバーを条件を伝えずに並べると、
/// 別の構成で壊れるコードを書かせることになる。
/// </remarks>
public sealed class LanguageServerNavigationTests
{
    /// <summary>条件付きのメンバーと、名前を重ねた関数を持つシェーダー。</summary>
    private const string NavigationShader = """
        Shader "Company/Navigation"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma multi_compile _ SHADER_DEBUG
                    struct Varyings
                    {
                        float2 uv : TEXCOORD0;
                    #ifdef SHADER_DEBUG
                        float4 debugColor : COLOR;
                    #endif
                    };
                    float _Amount;
                    half4 shade(float3 direction, float scale) { return direction.x * scale; }
                    half4 frag(Varyings input) : SV_Target
                    {
                        float local = _Amount;
                        return shade(float3(input.uv, 0), local);
                    }
                    ENDHLSL
                }
            }
        }
        """;

    [Fact]
    public async Task 構造体のメンバーを補完する()
    {
        JsonElement items = await RequestAsync("textDocument/completion", "input.uv");

        Assert.Contains("uv", Labels(items));
        Assert.Contains("debugColor", Labels(items));
    }

    [Fact]
    public async Task 条件付きのメンバーにはその条件を添える()
    {
        // 条件を伝えずに並べると、別の構成で壊れるコードを書かせることになる。
        JsonElement items = await RequestAsync("textDocument/completion", "input.uv");

        JsonElement debug = items.EnumerateArray()
            .Single(i => i.GetProperty("label").GetString() == "debugColor");

        Assert.Contains(
            "SHADER_DEBUG",
            debug.GetProperty("detail").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task 位置で書ける名前を補完する()
    {
        JsonElement items = await RequestAsync("textDocument/completion", "local = ");
        List<string> labels = [.. Labels(items)];

        Assert.Contains("local", labels);      // 局所変数
        Assert.Contains("input", labels);      // 仮引数
        Assert.Contains("_Amount", labels);    // uniform
        Assert.Contains("shade", labels);      // 利用者が書いた関数
        Assert.Contains("Varyings", labels);   // 構造体
        Assert.Contains("saturate", labels);   // 組み込み関数
        Assert.Contains("float3", labels);     // 型
    }

    [Fact]
    public async Task 呼び出しの途中で関数の形を見せる()
    {
        JsonDocument response = await SendAsync("textDocument/signatureHelp", ", local);");
        JsonElement result = response.RootElement.GetProperty("result");

        string label = result.GetProperty("signatures")[0].GetProperty("label").GetString()!;

        Assert.Contains("shade", label, StringComparison.Ordinal);
        Assert.Contains("float3 direction", label, StringComparison.Ordinal);
        Assert.Contains("float scale", label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 今書いている引数の位置を返す()
    {
        // shade(..., local) の 2 つ目を書いている。
        JsonDocument response = await SendAsync("textDocument/signatureHelp", "local);");
        JsonElement result = response.RootElement.GetProperty("result");

        Assert.Equal(1, result.GetProperty("activeParameter").GetInt32());
    }

    [Fact]
    public async Task 名前が現れる場所を一覧する()
    {
        JsonElement result = await RequestAsync("textDocument/references", "_Amount;");

        // 宣言と、frag の中の 1 か所。
        Assert.Equal(2, result.GetArrayLength());
    }

    [Fact]
    public async Task 局所的な名前はその関数の中だけを一覧する()
    {
        JsonElement result = await RequestAsync("textDocument/references", "local)");

        // 宣言と、return の中の 1 か所。
        Assert.Equal(2, result.GetArrayLength());
    }

    [Fact]
    public async Task 名前の変更で現れる場所をすべて書き換える()
    {
        JsonDocument response = await SendAsync(
            "textDocument/rename", "_Amount;", new { newName = "_Strength" });

        JsonElement changes = response.RootElement.GetProperty("result").GetProperty("changes");
        JsonElement edits = changes.EnumerateObject().Single().Value;

        Assert.Equal(2, edits.GetArrayLength());
        Assert.All(
            edits.EnumerateArray(),
            e => Assert.Equal("_Strength", e.GetProperty("newText").GetString()));
    }

    /// <summary>位置を指す要求を送り、result を返す。</summary>
    /// <param name="method">LSP の要求の名前。</param>
    /// <param name="target">カーソルを置く文字列。</param>
    /// <returns>応答の result。</returns>
    private static async Task<JsonElement> RequestAsync(string method, string target)
    {
        JsonDocument response = await SendAsync(method, target);
        JsonElement result = response.RootElement.GetProperty("result");

        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out JsonElement items)
            ? items
            : result;
    }

    /// <summary>位置を指す要求を送る。</summary>
    /// <param name="method">LSP の要求の名前。</param>
    /// <param name="target">カーソルを置く文字列。</param>
    /// <param name="extra">要求に足す項目。</param>
    /// <returns>応答。</returns>
    private static async Task<JsonDocument> SendAsync(string method, string target, object? extra = null)
    {
        await using LanguageServerHarness harness = new();

        await harness.InitializeAsync();
        await harness.OpenAsync(NavigationShader);

        await harness.ReceiveDiagnosticsAsync();

        return await harness.RequestAtAsync(method, NavigationShader, target, extra);
    }

    /// <summary>候補の名前を並べる。</summary>
    /// <param name="items">候補の配列。</param>
    /// <returns>名前。</returns>
    private static IEnumerable<string> Labels(JsonElement items)
        => items.EnumerateArray().Select(i => i.GetProperty("label").GetString()!);
}
