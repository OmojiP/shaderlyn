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
    // 関数の外の宣言: 修飾子 (GD0101)、描画のモード (GD0102)、uniform のヒントと既定値 (GD0103)、型 (GD0403)
    [InlineData("GD0101", "shader_type spatial;\ninstance uniform sampler2D s;\n")]
    [InlineData("GD0101", "shader_type spatial;\nglobal uniform float g[2];\n")]
    [InlineData("GD0101", "shader_type sky;\ninstance uniform float u;\n")]
    [InlineData("GD0101", "shader_type particles;\nvarying float v;\n")]
    [InlineData("GD0102", "shader_type spatial;\nrender_mode nope;\n")]
    [InlineData("GD0102", "shader_type canvas_item;\nrender_mode cull_back;\n")]
    [InlineData("GD0102", "shader_type spatial;\nrender_mode unshaded;\nrender_mode unshaded;\n")]
    [InlineData("GD0102", "shader_type spatial;\nrender_mode blend_mix, blend_add;\n")]
    [InlineData("GD0102", "shader_type spatial;\nstencil_mode read, compare_equal, 256;\n")]
    [InlineData("GD0102", "shader_type spatial;\nstencil_mode read, compare_equal, -1;\n")]
    [InlineData("GD0103", "shader_type spatial;\nuniform float u : source_color;\n")]
    [InlineData("GD0103", "shader_type spatial;\nuniform vec4 u : color_conversion_disabled;\n")]
    [InlineData("GD0103", "shader_type spatial;\nuniform float u : filter_linear;\n")]
    [InlineData("GD0103", "shader_type spatial;\nuniform sampler2D s : hint_normal, hint_default_white;\n")]
    [InlineData("GD0103", "shader_type spatial;\nuniform sampler2D s : repeat_enable, repeat_enable;\n")]
    [InlineData("GD0103", "shader_type spatial;\nuniform float u[2] : hint_range(0.0, 1.0);\n")]
    [InlineData("GD0103", "shader_type spatial;\nuniform float u : hint_range(0.0, 1.0, a);\n")]
    [InlineData("GD0103", "shader_type spatial;\nuniform int u : hint_enum(1, 2);\n")]
    [InlineData("GD0103", "shader_type spatial;\ninstance uniform float u : instance_index(16);\n")]
    [InlineData("GD0103", "shader_type sky;\nuniform sampler2D s : hint_screen_texture;\n")]
    [InlineData("GD0103", "shader_type spatial;\nuniform float u = vec2(1.0);\n")]
    [InlineData("GD0103", "shader_type spatial;\nuniform uint u = -1;\n")]
    [InlineData("GD0103", "shader_type spatial;\nuniform float u[2] = {1.0, 2.0};\n")]
    [InlineData("GD0403", "shader_type spatial;\nstruct S { float a; };\nuniform S u;\n")]
    [InlineData("GD0403", "shader_type spatial;\nvarying sampler2D v;\n")]
    [InlineData("GD0403", "shader_type spatial;\nstruct S { sampler2D a; };\n")]
    [InlineData("GD0403", "shader_type spatial;\nstruct S { nope a; };\n")]
    [InlineData("CF0001", "shader_type spatial;\nstruct S { };\n")]
    // switch と break / continue (GD0321)、discard (GD0002)、for の最初 (CF0001)
    [InlineData("GD0321", "shader_type spatial;\nvoid fragment() {\n  switch (1.0) {\n    case 1:\n      break;\n  }\n}\n")]
    [InlineData("GD0321", "shader_type spatial;\nvoid fragment() {\n  uint a = 1u;\n  switch (a) {\n    case 1:\n      break;\n  }\n}\n")]
    [InlineData("GD0321", "shader_type spatial;\nvoid fragment() {\n  int a = 1;\n  int k = 2;\n  switch (a) {\n    case k:\n      break;\n  }\n}\n")]
    [InlineData("GD0321", "shader_type spatial;\nconst int K = 1;\nvoid fragment() {\n  int a = 1;\n  switch (a) {\n    case 1:\n      break;\n    case K:\n      break;\n  }\n}\n")]
    [InlineData("GD0321", "shader_type spatial;\nvoid fragment() {\n  int a = 1;\n  switch (a) {\n    default:\n      break;\n    default:\n      break;\n  }\n}\n")]
    [InlineData("GD0321", "shader_type spatial;\nvoid fragment() {\n  int a = 1;\n  switch (a) {\n    case 1: {\n      case 2:\n        break;\n    }\n  }\n}\n")]
    [InlineData("GD0321", "shader_type spatial;\nvoid fragment() {\n  if (true) {\n    break;\n  }\n}\n")]
    [InlineData("GD0321", "shader_type spatial;\nvoid fragment() {\n  for (int i = 0; i < 2; i++) {\n    switch (i) {\n      case 0:\n        continue;\n    }\n  }\n}\n")]
    [InlineData("GD0002", "shader_type spatial;\nvoid vertex() {\n  discard;\n}\n")]
    [InlineData("GD0002", "shader_type spatial;\nvoid f() {\n  discard;\n}\nvoid vertex() {\n  f();\n}\n")]
    [InlineData("GD0002", "shader_type particles;\nvoid process() {\n  discard;\n}\n")]
    [InlineData("CF0001", "shader_type spatial;\nvoid fragment() {\n  int i;\n  for (i = 0; i < 2; i++) {\n  }\n}\n")]
    // 書き込めない値 (GD0309) と out の実引数 (GD0306)
    [InlineData("GD0309", "shader_type spatial;\nuniform vec3 u;\nvoid fragment() {\n  u.x = 1.0;\n}\n")]
    [InlineData("GD0309", "shader_type spatial;\nconst int K = 1;\nvoid fragment() {\n  int a = K++;\n}\n")]
    [InlineData("GD0309", "shader_type spatial;\nvoid f(const float x) {\n  x = 1.0;\n}\n")]
    [InlineData("GD0309", "shader_type spatial;\nvoid fragment() {\n  const float a[2] = { 1.0, 2.0 };\n  a[0] = 3.0;\n}\n")]
    [InlineData("GD0309", "shader_type spatial;\nvoid fragment() {\n  TIME += 1.0;\n}\n")]
    [InlineData("GD0309", "shader_type spatial;\nvoid fragment() {\n  VERTEX = vec3(1.0);\n}\n")]
    [InlineData("GD0309", "shader_type spatial;\nvoid fragment() {\n  vec3 a = vec3(1.0);\n  a.xx = vec2(1.0);\n}\n")]
    [InlineData("GD0309", "shader_type spatial;\nfloat f() { return 1.0; }\nvoid fragment() {\n  f() = 1.0;\n}\n")]
    [InlineData("GD0306", "shader_type spatial;\nuniform float u;\nvoid f(out float x) { x = 1.0; }\nvoid fragment() {\n  f(u);\n}\n")]
    [InlineData("GD0306", "shader_type spatial;\nconst float K = 1.0;\nvoid f(inout float x) { x = 1.0; }\nvoid fragment() {\n  f(K);\n}\n")]
    [InlineData("GD0306", "shader_type spatial;\nvoid f(out float x) { x = 1.0; }\nvoid fragment() {\n  vec3 v = vec3(1.0);\n  f(v[0]);\n}\n")]
    [InlineData("GD0306", "shader_type spatial;\nconst vec3 K = vec3(1.0);\nvoid f(out float x) { x = 1.0; }\nvoid fragment() {\n  f(K.x);\n}\n")]
    [InlineData("GD0306", "shader_type spatial;\nvoid fragment() {\n  float r = modf(1.5, 2.0);\n}\n")]
    // 添字 (GD0307)
    [InlineData("GD0307", "shader_type spatial;\nvoid fragment() {\n  float a[2] = { 1.0, 2.0 };\n  float b = a[2];\n}\n")]
    [InlineData("GD0307", "shader_type spatial;\nvoid fragment() {\n  vec2 v = vec2(1.0);\n  float b = v[-1];\n}\n")]
    [InlineData("GD0307", "shader_type spatial;\nstruct S { float a[3]; };\nvoid fragment() {\n  S s;\n  float b = s.a[3];\n}\n")]
    [InlineData("GD0307", "shader_type spatial;\nvoid fragment() {\n  vec4 v = vec4(1.0);\n  float b = v.xy[2];\n}\n")]
    [InlineData("GD0307", "shader_type spatial;\nvoid fragment() {\n  vec2 v = vec2(1.0);\n  float b = v[1.0];\n}\n")]
    [InlineData("GD0307", "shader_type spatial;\nvoid fragment() {\n  float b = TIME[0];\n}\n")]
    // 組み込みの名前の宣言し直し (GD0405) と、関数の外の名前を隠す局所変数 (CF0314)
    [InlineData("GD0405", "shader_type spatial;\nvoid fragment() {\n  float TIME = 1.0;\n}\n")]
    [InlineData("GD0405", "shader_type canvas_item;\nconst float PI = 3.0;\n")]
    [InlineData("GD0405", "shader_type spatial;\nuniform float ALBEDO;\n")]
    [InlineData("GD0405", "shader_type spatial;\nfloat sin(float x) { return x; }\n")]
    [InlineData("GD0405", "shader_type spatial;\nfloat f(float ALBEDO) { return ALBEDO; }\n")]
    [InlineData("CF0314", "shader_type spatial;\nuniform float u;\nvoid fragment() {\n  float u = 1.0;\n}\n")]
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
    // uniform のヒント (配列に書けるもの、color_conversion_disabled は source_color の後、hint_range の定数)、既定値の変換、描画のモード
    [InlineData("shader_type spatial;\nconst float K = 2.0;\nuniform sampler2D t : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;\nuniform vec4 c : source_color, color_conversion_disabled;\nuniform float r : hint_range(0, K, 0.1) = 0.5;\nuniform int e : hint_enum(\"A\", \"B\") = 0;\ninstance uniform vec4 i : instance_index(15);\nuniform sampler2D a[2] : source_color, filter_nearest;\n")]
    [InlineData("shader_type spatial;\nuniform float a = 1;\nuniform float b = 1u;\nuniform uint c = 1;\nuniform int d = 1u;\n")]
    [InlineData("shader_type spatial;\nrender_mode blend_add, depth_draw_always, cull_disabled, unshaded, alpha_to_coverage_and_one;\nrender_mode diffuse_toon;\nstencil_mode read, compare_equal, 255;\nstruct A { float x; };\nstruct B { A a; };\n")]
    // switch (符号付きの値、16 進、同じ型の const、uint)、switch の中のループの continue、fragment から呼ぶ関数と light の discard、空の for の最初
    [InlineData("shader_type spatial;\nconst int K = 3;\nvoid fragment() {\n  const int L = 4;\n  int a = 1;\n  switch (a) {\n    case -1:\n      break;\n    case 0x10:\n    case K:\n    case L: {\n      break;\n    }\n    default:\n      for (int i = 0; i < 2; i++) {\n        continue;\n      }\n      break;\n  }\n  uint b = 1u;\n  switch (b) {\n    case 1u:\n      break;\n  }\n}\n")]
    [InlineData("shader_type spatial;\nvoid f() {\n  discard;\n}\nvoid fragment() {\n  f();\n  int i = 0;\n  for (; i < 2; i++) {\n  }\n}\nvoid light() {\n  discard;\n}\n")]
    // 書き込める値: vertex の VERTEX、fragment の ALBEDO、配列の要素とメンバーを out へ、uniform のメンバーを in と const へ
    [InlineData("shader_type spatial;\nuniform vec3 u;\nvoid f(inout float x, out vec2 y) {\n  x += 1.0;\n  y = vec2(x);\n}\nvoid g(const float x) {\n}\nvoid vertex() {\n  VERTEX.y += 1.0;\n  float a[2] = { 1.0, 2.0 };\n  vec3 v = vec3(1.0);\n  vec2 w;\n  f(a[0], w);\n  f(v.x, v.yz);\n  a[1]++;\n  v.zx = vec2(0.0);\n  g(u.x);\n  g(1.0);\n}\nvoid fragment() {\n  ALBEDO.r = 1.0;\n  float i;\n  float r = modf(1.5, i);\n}\n")]
    // 添字: 配列・ベクトル・行列・構造体のメンバーの配列・スウィズル、uint の添字、const の名前の式 (範囲は調べない)
    [InlineData("shader_type spatial;\nstruct S { float a[3]; vec3 v; };\nconst int K = 7;\nvoid fragment() {\n  float a[2] = { 1.0, 2.0 };\n  vec4 v = vec4(1.0);\n  mat3 m = mat3(1.0);\n  S s;\n  uint u = 1u;\n  float b = a[1] + v[3] + m[2][2] + s.a[2] + s.v[2] + v[u] + v.xyz[2] + a[K - 6];\n}\n")]
    // 名前: vertex の局所変数 ALBEDO、利用者の関数の局所変数 ALBEDO、const の sin、関数と同じ名前の局所変数、外側の局所変数と同じ名前、組み込みと同じ名前の構造体
    [InlineData("shader_type spatial;\nconst float sin = 1.0;\nstruct TIME { float a; };\nfloat g() {\n  float ALBEDO = 1.0;\n  return ALBEDO;\n}\nvoid vertex() {\n  vec3 ALBEDO = vec3(1.0);\n  float g = 2.0;\n  float a = 1.0;\n  {\n    float a = 2.0;\n  }\n  for (int i = 0; i < 1; i++) {\n  }\n  for (int i = 0; i < 1; i++) {\n  }\n}\n")]
    public async Task Godotが通す書き方に誤りを出さない(string code)
        => Assert.Contains("1 ファイルを解析: エラー 0 件, 警告 0 件, 情報 0 件", await AnalyzeAsync(code), StringComparison.Ordinal);
}
