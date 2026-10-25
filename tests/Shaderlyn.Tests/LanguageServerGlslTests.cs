using System.Text.Json;

namespace Shaderlyn.Tests;

/// <summary>
/// VS Code 拡張の説明 (ホバー)・定義へ移動・補完・引数の案内を、GLSL のファイルで働かせたときの検証。
/// </summary>
/// <remarks>
/// GLSL の型と組み込み (glslang から作った表) で答える。HLSL の型や組み込み関数 (<c>float4</c>、<c>saturate</c>) は出さない。
/// </remarks>
public sealed class LanguageServerGlslTests : IDisposable
{
    private const string Shader = """
        #version 450
        layout(location = 0) in vec2 inUV;
        layout(location = 0) out vec4 outColor;
        layout(binding = 0) uniform sampler2D albedo;
        layout(binding = 1) uniform UBO { mat4 model; float exposure; } ubo;
        vec3 tonemap(vec3 color) { return color / (color + vec3(1.0)); }
        void main()
        {
            vec3 lit = texture(albedo, inUV).rgb * ubo.exposure;
            vec2 pixel = gl_FragCoord.xy;
            outColor = vec4(tonemap(lit), 1.0);
        }
        """;

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-lsp-glsl-{Guid.NewGuid():N}");

    public LanguageServerGlslTests() => Directory.CreateDirectory(_workDirectory);

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

    /// <summary>GLSL のファイルを開いた言語サーバーを用意する。</summary>
    private async Task<LanguageServerHarness> OpenAsync(string fileName = "Shader.frag", string text = Shader)
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
    public async Task 組み込み関数の形を説明する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        string hover = await harness.HoverAsync(Shader, "texture");

        Assert.Contains("```glsl", hover, StringComparison.Ordinal);
        Assert.Contains("vec4 texture(sampler2D, vec2", hover, StringComparison.Ordinal);
        Assert.Contains("組み込み関数", hover, StringComparison.Ordinal);
        Assert.DoesNotContain("```hlsl", hover, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 組み込み変数の型を説明する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        string hover = await harness.HoverAsync(Shader, "gl_FragCoord");

        Assert.Contains("vec4", hover, StringComparison.Ordinal);
        Assert.Contains("組み込み変数", hover, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ブロックのメンバーと行列の積の型を説明する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        Assert.Contains("float", await harness.HoverAsync(Shader, "xposure;"), StringComparison.Ordinal);
        Assert.Contains("vec3", await harness.HoverAsync(Shader, "(lit)"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 利用者の関数の定義へ移動する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        JsonElement definitions = await harness.DefinitionAsync(Shader, "tonemap(lit)");

        Assert.Equal(5, definitions[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task GLSLの型と組み込みを補完し_HLSLのものは出さない()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        string[] labels = Labels(await harness.RequestAtAsync("textDocument/completion", Shader, "pixel = "));

        Assert.Contains("vec3", labels);
        Assert.Contains("texture", labels);
        Assert.Contains("gl_FragCoord", labels);
        Assert.Contains("tonemap", labels);
        Assert.Contains("ubo", labels);
        Assert.DoesNotContain("float4", labels);
        Assert.DoesNotContain("saturate", labels);

        // 頂点シェーダーの組み込み変数は、フラグメントシェーダーでは使えない。
        Assert.DoesNotContain("gl_VertexIndex", labels);
    }

    [Fact]
    public async Task 成分の名前にstpqも出す()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        string[] labels = Labels(await harness.RequestAtAsync("textDocument/completion", Shader, "xy;"));

        Assert.Contains("x", labels);
        Assert.Contains("s", labels);
    }

    [Fact]
    public async Task 組み込み関数の引数を案内する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        using JsonDocument response = await harness.RequestAtAsync("textDocument/signatureHelp", Shader, "inUV).rgb");
        string[] labels = [.. response.RootElement.GetProperty("result").GetProperty("signatures").EnumerateArray().Select(s => s.GetProperty("label").GetString()!)];

        Assert.Contains(labels, l => l.StartsWith("vec4 texture(sampler2D, vec2", StringComparison.Ordinal));
        Assert.Equal(1, response.RootElement.GetProperty("result").GetProperty("activeParameter").GetInt32());
    }

    [Fact]
    public async Task 指令の補完にversionとextensionを出す()
    {
        // 「#ve」まで書いたところ (カーソルは「ve」の後ろ)。
        const string text = "#ve  \nvoid main() { }\n";
        await using LanguageServerHarness harness = await OpenAsync(text: text);

        string[] labels = Labels(await harness.RequestAtAsync("textDocument/completion", text, "e  "));

        Assert.Contains("version", labels);
        Assert.Contains("extension", labels);
    }
}
