using System.Text.Json;

namespace Shaderlyn.Tests;

/// <summary>
/// VS Code 拡張の指摘・説明 (ホバー)・定義へ移動・補完・引数の案内を、Slang のファイルで働かせたときの検証。
/// </summary>
/// <remarks>
/// Slang の型と標準ライブラリで答える。import したモジュールの宣言も使う。
/// </remarks>
public sealed class LanguageServerSlangTests : IDisposable
{
    private const string Shader = """
        import helpers;
        struct Light { float3 color; float power; };
        func tonemap(c : float3) -> float3 { return c / (c + float3(1.0)); }
        [shader("compute")]
        [numthreads(8, 8, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            Light light = { float3(1.0), 2.0 };
            let lit = light.color * twice(light.power);
            float3 shaded = tonemap(lit);
            CoopVec<float, 4> v = CoopVec<float, 4>(1.0);
        }
        """;

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-lsp-slang-{Guid.NewGuid():N}");

    public LanguageServerSlangTests()
    {
        Directory.CreateDirectory(_workDirectory);
        File.WriteAllText(Path.Combine(_workDirectory, "helpers.slang"), "module helpers;\npublic float twice(float x) { return x * 2; }\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }
    }

    private async Task<LanguageServerHarness> OpenAsync(string fileName = "shader.slang", string text = Shader)
    {
        LanguageServerHarness harness = new(LanguageServer.DocumentUri.FromFilePath(Path.Combine(_workDirectory, fileName)));
        await harness.InitializeAsync(null, _workDirectory);
        await harness.OpenAsync(text);
        return harness;
    }

    private static string[] Labels(JsonDocument response)
    {
        JsonElement result = response.RootElement.GetProperty("result");
        JsonElement items = result.ValueKind == JsonValueKind.Object ? result.GetProperty("items") : result;
        return [.. items.EnumerateArray().Select(i => i.GetProperty("label").GetString()!)];
    }

    [Fact]
    public async Task 正しいシェーダーに指摘を出さない()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();

        Assert.Equal(0, diagnostics.GetArrayLength());
    }

    [Fact]
    public async Task 宣言されていない名前を指摘する()
    {
        const string text = "void f()\n{\n    float a = nope;\n}\n";
        await using LanguageServerHarness harness = await OpenAsync(text: text);

        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();

        Assert.Contains(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "CF0310");
    }

    [Fact]
    public async Task 書きかけの関数を保存してもほかの関数の宣言の検査を続ける()
    {
        // ある関数の本体が書きかけ (return a +) のまま保存しても、ほかの関数の宣言されていない名前は指摘し続ける。
        // 編集のたびの解析は構文だけなので、保存したときの解析で確かめる。
        const string text = "float g(float y)\n{\n    return nope * y;\n}\n";
        await using LanguageServerHarness harness = await OpenAsync(text: text);
        Assert.Contains((await harness.ReceiveDiagnosticsAsync()).EnumerateArray(), d => d.GetProperty("code").GetString() == "CF0310");

        await harness.ChangeAsync("float f(float a)\n{\n    return a +\n}\n\n" + text);
        await harness.SaveAsync();
        JsonElement diagnostics = await harness.ReceiveDiagnosticsForAsync(harness.Uri, d => d.EnumerateArray().Any(x => x.GetProperty("code").GetString() == "CF0310"));

        Assert.Contains(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "CF0001");
        Assert.Contains(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "CF0310"
                                                           && d.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 7);
    }


    [Fact]
    public async Task letの型とモジュールの関数を説明する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        Assert.Contains("float3", await harness.HoverAsync(Shader, "(lit)"), StringComparison.Ordinal);
        Assert.Contains("float twice(float x)", await harness.HoverAsync(Shader, "twice(l"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task func_の関数とモジュールの関数の定義へ移動する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        JsonElement local = await harness.DefinitionAsync(Shader, "tonemap(lit)");
        Assert.Equal(2, local[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        JsonElement imported = await harness.DefinitionAsync(Shader, "twice(l");
        Assert.EndsWith("helpers.slang", imported[0].GetProperty("uri").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Slangの型と標準ライブラリとモジュールの関数を補完する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        string[] labels = Labels(await harness.RequestAtAsync("textDocument/completion", Shader, "shaded = "));

        Assert.Contains("float3", labels);
        Assert.Contains("CoopVec", labels);
        Assert.Contains("lerp", labels);
        Assert.Contains("tonemap", labels);
        Assert.Contains("twice", labels);
    }
}
