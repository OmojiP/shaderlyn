using System.Text.Json;

namespace Shaderlyn.Tests;

/// <summary>
/// VS Code 拡張の指摘・説明 (ホバー)・定義へ移動・補完・引数の案内を、WGSL のファイルで働かせたときの検証 (WG4)。
/// </summary>
/// <remarks>
/// WGSL の型と組み込み関数で答える。型を書かない <c>let</c> は、初期値の型を具体的にしたもので説明する。
/// </remarks>
public sealed class LanguageServerWgslTests : IDisposable
{
    private const string Shader = """
        struct Light { color: vec3f, power: f32, }
        @group(0) @binding(0) var<uniform> light: Light;
        fn tonemap(c: vec3f) -> vec3f { return c / (c + vec3f(1.0)); }
        @fragment
        fn main(@location(0) uv: vec2f) -> @location(0) vec4f {
            let lit = light.color * light.power;
            let shaded = tonemap(lit);
            return vec4f(shaded, 1.0);
        }
        """;

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-lsp-wgsl-{Guid.NewGuid():N}");

    public LanguageServerWgslTests() => Directory.CreateDirectory(_workDirectory);

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

    private async Task<LanguageServerHarness> OpenAsync(string fileName = "shader.wgsl", string text = Shader)
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
        const string text = "fn f() -> f32 {\n    return nope;\n}\n";
        await using LanguageServerHarness harness = await OpenAsync(text: text);

        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();

        Assert.Contains(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "CF0310");
    }

    [Fact]
    public async Task letの型と関数を説明する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        Assert.Contains("vec3f", await harness.HoverAsync(Shader, "(lit)"), StringComparison.Ordinal);
        Assert.Contains("tonemap", await harness.HoverAsync(Shader, "tonemap(lit)"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 関数と構造体の定義へ移動する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        JsonElement function = await harness.DefinitionAsync(Shader, "tonemap(lit)");
        Assert.Equal(2, function[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());

        JsonElement global = await harness.DefinitionAsync(Shader, "light.color");
        Assert.Equal(1, global[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task WGSLの型と組み込み関数を補完する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        string[] labels = Labels(await harness.RequestAtAsync("textDocument/completion", Shader, "shaded = "));

        Assert.Contains("vec3f", labels);
        Assert.Contains("textureSample", labels);
        Assert.Contains("tonemap", labels);
        Assert.Contains("light", labels);
        Assert.DoesNotContain("float3", labels);
    }
}
