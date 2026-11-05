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
    // 演算子の型の組 (GD0301) と条件 (GD0311)
    [InlineData("GD0301", "shader_type spatial;\nvoid fragment() {\n  float a = 1.0 + 1;\n}\n")]
    [InlineData("GD0301", "shader_type spatial;\nvoid fragment() {\n  vec3 a = vec3(1.0) + vec4(1.0).xyz.xy;\n}\n")]
    [InlineData("GD0301", "shader_type spatial;\nvoid fragment() {\n  mat4 m = -mat4(1.0);\n}\n")]
    [InlineData("GD0301", "shader_type spatial;\nvoid fragment() {\n  bool b = vec2(1.0) < vec2(2.0);\n}\n")]
    [InlineData("GD0301", "shader_type spatial;\nvoid fragment() {\n  int a = 1 % ivec2(1).x;\n  ivec2 b = 1 % ivec2(1);\n}\n")]
    [InlineData("GD0301", "shader_type spatial;\nvoid fragment() {\n  bool a = true;\n  a++;\n}\n")]
    [InlineData("GD0301", "shader_type spatial;\nvoid fragment() {\n  float a = true ? 1.0 : 2;\n}\n")]
    // 組み込み関数と値の作成の形 (GD0302)
    [InlineData("GD0302", "shader_type spatial;\nvoid fragment() {\n  float a = pow(vec2(1.0), 2.0).x;\n}\n")]
    [InlineData("GD0302", "shader_type spatial;\nvoid fragment() {\n  int i = 1;\n  vec2 a = vec2(i, 1.0);\n}\n")]
    [InlineData("GD0302", "shader_type spatial;\nconst int K = 1;\nvoid fragment() {\n  float a = sin(K);\n}\n")]
    [InlineData("GD0311", "shader_type spatial;\nvoid fragment() {\n  if (1.0) {\n  }\n}\n")]
    // ステージ (GD0002)、ステージ関数の形 (GD0322)、ステージごとの組み込みの変数 (CF0310)、宣言より前の呼び出し (CF0310)
    [InlineData("GD0002", "shader_type spatial;\nvoid vertex() {\n  float a = dFdx(1.0);\n}\n")]
    [InlineData("GD0002", "shader_type spatial;\nfloat g() { return fwidth(1.0); }\nfloat h() { return g(); }\nvoid vertex() {\n  float a = h();\n}\n")]
    [InlineData("GD0002", "shader_type fog;\nvoid fog() {\n  float a = dFdx(1.0);\n}\n")]
    [InlineData("GD0002", "shader_type canvas_item;\nvoid vertex() {\n  float d = texture_sdf(vec2(0.0));\n}\n")]
    [InlineData("GD0322", "shader_type spatial;\nvoid fragment(float x) {\n}\n")]
    [InlineData("GD0322", "shader_type spatial;\nfloat light() {\n  return 1.0;\n}\n")]
    [InlineData("CF0310", "shader_type spatial;\nvoid vertex() {\n  ALBEDO = vec3(1.0);\n}\n")]
    [InlineData("CF0310", "shader_type spatial;\nvec3 f() { return FRAGCOORD.xyz; }\nvoid fragment() {\n  ALBEDO = f();\n}\n")]
    [InlineData("CF0310", "shader_type spatial;\nuniform float u = TIME;\n")]
    [InlineData("CF0310", "shader_type spatial;\nvoid fragment() {\n  ALBEDO = f();\n}\nvec3 f() { return vec3(1.0); }\n")]
    [InlineData("GD0311", "shader_type spatial;\nvoid fragment() {\n  for (int i = 0; i; i++) {\n  }\n}\n")]
    public async Task Godotが誤りにする書き方を報告する(string id, string code)
        => Assert.Contains($"error {id}", await AnalyzeAsync(code), StringComparison.Ordinal);

    [Theory]
    [InlineData("shader_type spatial;\nuniform vec4 tint : source_color;\nvoid vertex() {\n  VERTEX += NORMAL * TIME;\n}\nvoid fragment() {\n  ALBEDO = tint.rgb * PI;\n}\n")]
    [InlineData("shader_type canvas_item;\nvoid fragment() {\n  COLOR = texture(TEXTURE, UV) * vec4(SCREEN_UV, 0.0, 1.0);\n}\n")]
    [InlineData("shader_type particles;\nvoid process() {\n  TRANSFORM[3].xyz += VELOCITY * DELTA;\n}\n")]
    [InlineData("shader_type sky;\nvoid sky() {\n  COLOR = EYEDIR;\n}\n")]
    // 関数の呼び出しの実引数に書いた整数のリテラルは、仮引数の型へ変換して渡す
    [InlineData("shader_type spatial;\nfloat f(float x) { return x; }\nvoid fragment() {\n  float a = f(0) + f(-1);\n}\n")]
    // Godot が通す演算の型の組 (行列とベクトルは順を問わない、スカラーとベクトル・行列、整数のベクトルとスカラー)
    [InlineData("shader_type spatial;\nvoid fragment() {\n  mat3 m = mat3(1.0);\n  vec3 a = m * vec3(1.0) + vec3(1.0) * m;\n  mat3 b = 2.0 * m - m / 2.0;\n  vec3 c = 1.0 + a;\n  ivec2 d = ivec2(4) % 3 + (ivec2(1) << 1);\n  int e = 3 & 5 | 1;\n  a *= m;\n  a += 1.0;\n  bool f = a == c && !(1.0 < 2.0);\n}\n")]
    // 組み込み関数と値の作成の実引数に書いた整数のリテラルは変換される
    [InlineData("shader_type spatial;\nvoid fragment() {\n  float a = sin(1) + mix(0, 1, 0.5) + pow(2.0, 3);\n  vec2 b = vec2(1, 2);\n  mat2 c = mat2(1);\n}\n")]
    // ステージごとの組み込み: light の ALBEDO、fragment から呼ぶ関数の dFdx、sky の dFdx、fragment の texture_sdf、利用者の関数の TIME と PI
    [InlineData("shader_type spatial;\nfloat g() { return dFdx(1.0) + TIME * PI; }\nvoid fragment() {\n  ALBEDO = vec3(g());\n}\nvoid light() {\n  DIFFUSE_LIGHT = ALBEDO;\n}\n")]
    [InlineData("shader_type sky;\nvoid sky() {\n  COLOR = vec3(dFdx(1.0));\n}\n")]
    [InlineData("shader_type canvas_item;\nfloat f() { return texture_sdf(vec2(0.0)); }\nvoid fragment() {\n  COLOR = vec4(f() + texture_sdf(vec2(0.0)));\n}\n")]
    [InlineData("shader_type particles;\nvoid fragment() {\n}\nvoid process() {\n  bool a = emit_subparticle(TRANSFORM, VELOCITY, COLOR, CUSTOM, 0u);\n}\n")]
    // ステージ関数は書かなくてもよい (GLSL の main のように要るものは無い)
    [InlineData("shader_type spatial;\nconst float K = 1.0;\n")]
    public async Task Godotが通す書き方に誤りを出さない(string code)
        => Assert.Contains("1 ファイルを解析: エラー 0 件, 警告 0 件, 情報 0 件", await AnalyzeAsync(code), StringComparison.Ordinal);
}
