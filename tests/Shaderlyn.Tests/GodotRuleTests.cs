using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// Godot シェーダー言語に当てる C 系の共通のルールの検証 (GD2)。
/// </summary>
/// <remarks>
/// 誤りの例は、Godot 4.7.2 が同じ行で誤りにすることを確かめたもの (tools/oracles/godot-errors)。
/// 通る例は、Godot が通すもの。CLI に .gdshader を渡して、Godot の環境で解析する。
/// </remarks>
public sealed class GodotRuleTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-godot-rules-{Guid.NewGuid():N}");

    public GodotRuleTests() => Directory.CreateDirectory(_workDirectory);

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

    private async Task<string> AnalyzeAsync(string code)
    {
        File.WriteAllText(Path.Combine(_workDirectory, "a.gdshader"), code);
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([_workDirectory, "--no-config"], BuiltInAnalyzers.All, output, error);
        Assert.True(error.ToString().Length == 0, error.ToString());
        return output.ToString();
    }

    [Theory]
    [InlineData("CF0310", "shader_type spatial;\nvoid fragment() {\n  float a = nope;\n}\n")]
    // GLSL の組み込みの変数は Godot に無い
    [InlineData("CF0310", "shader_type spatial;\nvoid fragment() {\n  vec4 a = gl_FragCoord;\n}\n")]
    // ほかのシェーダーの種類の組み込み (TRANSFORM は particles の process にだけある)
    [InlineData("CF0310", "shader_type canvas_item;\nvoid fragment() {\n  mat4 t = TRANSFORM;\n}\n")]
    [InlineData("CF0312", "shader_type spatial;\nvoid fragment() {\n  vec3 a = vec3(1.0);\n  float b = a.w;\n}\n")]
    [InlineData("CF0314", "shader_type spatial;\nvoid fragment() {\n  float a = 1.0;\n  float a = 2.0;\n}\n")]
    [InlineData("CF0402", "shader_type spatial;\nfloat f(float x) { return x; }\nvoid fragment() {\n  float a = f(1.0, 2.0);\n}\n")]
    // Godot は暗黙の変換をしない
    [InlineData("CF0410", "shader_type spatial;\nvoid fragment() {\n  float a = 1;\n}\n")]
    [InlineData("CF0401", "shader_type spatial;\nfloat f(float x) { return x; }\nvoid fragment() {\n  int i = 0;\n  float a = f(i);\n}\n")]
    [InlineData("CF0401", "shader_type spatial;\nconst int K = 1;\nfloat f(float x) { return x; }\nvoid fragment() {\n  float a = f(K);\n}\n")]
    public async Task Godotが誤りにする書き方を報告する(string id, string code)
        => Assert.Contains($"error {id}", await AnalyzeAsync(code), StringComparison.Ordinal);

    [Theory]
    [InlineData("shader_type spatial;\nuniform vec4 tint : source_color;\nvoid vertex() {\n  VERTEX += NORMAL * TIME;\n}\nvoid fragment() {\n  ALBEDO = tint.rgb * PI;\n}\n")]
    [InlineData("shader_type canvas_item;\nvoid fragment() {\n  COLOR = texture(TEXTURE, UV) * vec4(SCREEN_UV, 0.0, 1.0);\n}\n")]
    [InlineData("shader_type particles;\nvoid process() {\n  TRANSFORM[3].xyz += VELOCITY * DELTA;\n}\n")]
    [InlineData("shader_type sky;\nvoid sky() {\n  COLOR = EYEDIR;\n}\n")]
    // 関数の呼び出しの実引数に書いた整数のリテラルは、仮引数の型へ変換して渡す
    [InlineData("shader_type spatial;\nfloat f(float x) { return x; }\nvoid fragment() {\n  float a = f(0) + f(-1);\n}\n")]
    // ステージ関数は書かなくてもよい (GLSL の main のように要るものは無い)
    [InlineData("shader_type spatial;\nconst float K = 1.0;\n")]
    public async Task Godotが通す書き方に誤りを出さない(string code)
        => Assert.Contains("1 ファイルを解析: エラー 0 件, 警告 0 件, 情報 0 件", await AnalyzeAsync(code), StringComparison.Ordinal);
}
