using System.Text.Json;

namespace Shaderlyn.Tests;

/// <summary>
/// VS Code 拡張の指摘・説明 (ホバー)・定義へ移動・補完・引数の案内を、Godot のシェーダーで働かせたときの検証。
/// </summary>
/// <remarks>
/// Godot の型と組み込み (Godot 4.7.2 のソースから作った表) で答える。HLSL と GLSL の言葉 (<c>float4</c>、<c>gl_FragCoord</c>、<c>```hlsl</c>) は出さない。
/// </remarks>
public sealed class LanguageServerGodotTests : IDisposable
{
    private const string Shader = """
        shader_type spatial;
        render_mode unshaded;
        uniform sampler2D albedo : source_color, filter_linear;
        uniform float exposure : hint_range(0.0, 4.0) = 1.0;
        varying vec3 world;
        struct Light { vec3 color; float power; };
        vec3 tonemap(vec3 color) { return color / (color + vec3(1.0)); }
        void vertex() {
            world = VERTEX;
        }
        void fragment() {
            Light light = Light(vec3(1.0), 2.0);
            vec3 lit = texture(albedo, UV).rgb * exposure * light.power;
            vec2 pixel = SCREEN_UV;
            ALBEDO = tonemap(lit);
        }
        """;

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-lsp-godot-{Guid.NewGuid():N}");

    public LanguageServerGodotTests() => Directory.CreateDirectory(_workDirectory);

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

    /// <summary>Godot のファイルを開いた言語サーバーを用意する。</summary>
    private async Task<LanguageServerHarness> OpenAsync(string fileName = "shader.gdshader", string text = Shader)
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
    public async Task Godotの規則の指摘を出す()
    {
        const string text = "shader_type spatial;\nvoid fragment() {\n  float a = 1.0 + 1;\n}\n";
        await using LanguageServerHarness harness = await OpenAsync(text: text);

        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();

        Assert.Contains(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "GD0301");
    }

    [Fact]
    public async Task 組み込み関数の形を説明する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        string hover = await harness.HoverAsync(Shader, "texture");

        Assert.Contains("```glsl", hover, StringComparison.Ordinal);
        Assert.Contains("vec4 texture(sampler2D, vec2", hover, StringComparison.Ordinal);
        Assert.Contains("組み込み関数", hover, StringComparison.Ordinal);
        Assert.DoesNotContain("hlsl", hover, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("glslang", hover, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 組み込み変数の型を説明する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        string hover = await harness.HoverAsync(Shader, "SCREEN_UV");

        Assert.Contains("vec2", hover, StringComparison.Ordinal);
        Assert.Contains("組み込み変数", hover, StringComparison.Ordinal);
    }

    [Fact]
    public async Task uniformと構造体のメンバーの型を説明する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        Assert.Contains("float", await harness.HoverAsync(Shader, "exposure * l"), StringComparison.Ordinal);
        Assert.Contains("float", await harness.HoverAsync(Shader, "ower;"), StringComparison.Ordinal);
        Assert.Contains("vec3", await harness.HoverAsync(Shader, "(lit)"), StringComparison.Ordinal);

        // uniform の宣言には、定数バッファではなくヒントを添える。
        string declaration = await harness.HoverAsync(Shader, "albedo :");
        Assert.Contains("uniform (ヒント: source_color, filter_linear)", declaration, StringComparison.Ordinal);
        Assert.DoesNotContain("定数バッファ", declaration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 利用者の関数の定義へ移動する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        JsonElement definitions = await harness.DefinitionAsync(Shader, "tonemap(lit)");

        Assert.Equal(6, definitions[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Godotの型と組み込みを補完し_HLSLとGLSLのものは出さない()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        string[] labels = Labels(await harness.RequestAtAsync("textDocument/completion", Shader, "pixel = "));

        Assert.Contains("vec3", labels);
        Assert.Contains("texture", labels);
        Assert.Contains("SCREEN_UV", labels);
        Assert.Contains("ALBEDO", labels);
        Assert.Contains("TIME", labels);
        Assert.Contains("tonemap", labels);
        Assert.Contains("exposure", labels);
        Assert.DoesNotContain("float4", labels);
        Assert.DoesNotContain("saturate", labels);
        Assert.DoesNotContain("gl_FragCoord", labels);

        // vertex の組み込み (VERTEX は fragment にもあるが、INSTANCE_ID は vertex だけ) と、ほかのシェーダーの種類の組み込みは出さない。
        Assert.DoesNotContain("INSTANCE_ID", labels);
        Assert.DoesNotContain("EYEDIR", labels);
    }

    [Fact]
    public async Task 組み込み関数の引数を案内する()
    {
        await using LanguageServerHarness harness = await OpenAsync();

        using JsonDocument response = await harness.RequestAtAsync("textDocument/signatureHelp", Shader, "UV).rgb");
        string[] labels = [.. response.RootElement.GetProperty("result").GetProperty("signatures").EnumerateArray().Select(s => s.GetProperty("label").GetString()!)];

        Assert.Contains(labels, l => l.StartsWith("vec4 texture(sampler2D", StringComparison.Ordinal));
        Assert.Equal(1, response.RootElement.GetProperty("result").GetProperty("activeParameter").GetInt32());
    }

    [Fact]
    public async Task 描画のモードを補完する()
    {
        // カーソルは「,」の後ろ。
        const string text = "shader_type spatial;\nrender_mode unshaded, ;\n";
        await using LanguageServerHarness harness = await OpenAsync(text: text);

        string[] labels = Labels(await harness.RequestAtAsync("textDocument/completion", text, ", ;"));

        Assert.Contains("cull_disabled", labels);
        Assert.Contains("blend_mix", labels);
        Assert.DoesNotContain("blend", labels);
        Assert.DoesNotContain("vec3", labels);

        // canvas_item にしか無いモード
        Assert.DoesNotContain("light_only", labels);
    }

    [Fact]
    public async Task uniformのヒントとシェーダーの種類を補完する()
    {
        // カーソルは「:」の後ろ。
        const string text = "shader_type spatial;\nuniform sampler2D t : ;\n";
        await using LanguageServerHarness harness = await OpenAsync(text: text);

        string[] hints = Labels(await harness.RequestAtAsync("textDocument/completion", text, ": ;"));

        Assert.Contains("source_color", hints);
        Assert.Contains("filter_linear_mipmap", hints);
        Assert.DoesNotContain("vec3", hints);

        // カーソルは shader_type の後ろの空白。
        const string type = "shader_type  ;\n";
        await using LanguageServerHarness typeHarness = await OpenAsync("type.gdshader", type);

        string[] types = Labels(await typeHarness.RequestAtAsync("textDocument/completion", type, "  ;"));

        Assert.Contains("spatial", types);
        Assert.Contains("canvas_item", types);
    }

    [Fact]
    public async Task 指令の補完にincludeを出し_versionは出さない()
    {
        // 「#in」まで書いたところ (カーソルは「in」の後ろ)。
        const string text = "shader_type spatial;\n#in  \nvoid fragment() { }\n";
        await using LanguageServerHarness harness = await OpenAsync(text: text);

        string[] labels = Labels(await harness.RequestAtAsync("textDocument/completion", text, "n  "));

        Assert.Contains("include", labels);
        Assert.DoesNotContain("version", labels);
        Assert.DoesNotContain("extension", labels);
    }
}
