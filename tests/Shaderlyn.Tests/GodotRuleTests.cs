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
    // varying のステージ (GD0002)、整数の varying の flat (GD0101)、組み込み関数の out (GD0306)
    [InlineData("GD0002", "shader_type spatial;\nvarying float v;\nvoid vertex() {\n  v = 1.0;\n}\nvoid fragment() {\n  v = 2.0;\n}\n")]
    [InlineData("GD0002", "shader_type spatial;\nvarying float v;\nvoid fragment() {\n  v = 2.0;\n}\nvoid vertex() {\n  v = 1.0;\n}\n")]
    [InlineData("GD0002", "shader_type spatial;\nvarying vec3 v;\nvoid light() {\n  v = vec3(1.0);\n}\n")]
    [InlineData("GD0002", "shader_type spatial;\nvarying bool b;\nvoid vertex() {\n  b = true;\n}\n")]
    [InlineData("GD0002", "shader_type spatial;\nvarying bvec2 b;\nvoid fragment() {\n  b.x = true;\n}\n")]
    [InlineData("GD0002", "shader_type spatial;\nvarying float v;\nvoid fragment() {\n  v = 1.0;\n}\nfloat f() {\n  return v;\n}\nvoid vertex() {\n  float a = f();\n}\n")]
    [InlineData("GD0002", "shader_type spatial;\nvarying float v;\nvoid g(out float x) { x = 1.0; }\nvoid vertex() {\n  v = 1.0;\n}\nvoid fragment() {\n  g(v);\n}\n")]
    [InlineData("GD0101", "shader_type spatial;\nvarying ivec2 k;\n")]
    [InlineData("GD0306", "shader_type spatial;\nvarying float v;\nvoid fragment() {\n  float r = modf(1.5, v);\n}\n")]
    // 配列 (GD0308・GD0319・CF0410・CF0001)、定数の式 (GD0317)、関数の名前 (GD0312)、再帰 (GD0332)、0 での割り算 (GD0333)
    [InlineData("GD0308", "shader_type spatial;\nuniform float u[];\n")]
    [InlineData("GD0308", "shader_type spatial;\nvoid f(float x[]) {}\n")]
    [InlineData("GD0308", "shader_type spatial;\nvoid fragment() {\n  float a[0];\n}\n")]
    [InlineData("GD0308", "shader_type spatial;\nvoid fragment() {\n  float a[2.0];\n}\n")]
    [InlineData("GD0308", "shader_type spatial;\nvoid fragment() {\n  float a[-1];\n}\n")]
    [InlineData("GD0308", "shader_type spatial;\nvoid fragment() {\n  int n = 2;\n  float a[n];\n}\n")]
    [InlineData("GD0319", "shader_type spatial;\nconst float G[3] = { 1.0, 2.0 };\n")]
    [InlineData("GD0319", "shader_type spatial;\nvoid fragment() {\n  float a[2] = float[](1.0, 2.0, 3.0);\n}\n")]
    [InlineData("GD0319", "shader_type spatial;\nvoid fragment() {\n  float a[2] = float[2](1.0, 2.0, 3.0);\n}\n")]
    [InlineData("GD0319", "shader_type spatial;\nvoid fragment() {\n  float a[2];\n  float b[3];\n  a = b;\n}\n")]
    [InlineData("GD0319", "shader_type spatial;\nvoid f(float x[2]) {}\nvoid fragment() {\n  float a[3];\n  f(a);\n}\n")]
    [InlineData("CF0410", "shader_type spatial;\nvoid fragment() {\n  float a[2] = { 1, 2 };\n}\n")]
    [InlineData("CF0410", "shader_type spatial;\nvoid fragment() {\n  float a[2] = int[2](1, 2);\n}\n")]
    [InlineData("CF0001", "shader_type spatial;\nvoid fragment() {\n  float a[];\n}\n")]
    [InlineData("CF0001", "shader_type spatial;\nvoid fragment() {\n  float b[] = float[](1.0, 2.0);\n}\n")]
    [InlineData("CF0001", "shader_type spatial;\nfloat g(float x);\nfloat f(float x) { return x; }\n")]
    [InlineData("GD0317", "shader_type spatial;\nuniform float u;\nconst float A = u;\n")]
    [InlineData("GD0317", "shader_type spatial;\nvoid fragment() {\n  float a = 1.0;\n  const float b = a;\n}\n")]
    [InlineData("GD0317", "shader_type spatial;\nfloat f() { return 1.0; }\nvoid fragment() {\n  const float b = f();\n}\n")]
    [InlineData("GD0312", "shader_type spatial;\nfloat f() { return 1.0; }\nvoid fragment() {\n  float a = f;\n}\n")]
    [InlineData("GD0312", "shader_type spatial;\nvoid fragment() {\n  float a = sin;\n}\n")]
    [InlineData("GD0332", "shader_type spatial;\nfloat f(float x) { return f(x); }\n")]
    [InlineData("GD0333", "shader_type spatial;\nvoid fragment() {\n  ivec2 a = ivec2(4) / ivec2(1, 0);\n}\n")]
    [InlineData("GD0333", "shader_type spatial;\nvoid fragment() {\n  ivec2 a = ivec2(4) / 0;\n}\n")]
    [InlineData("GD0333", "shader_type spatial;\nvoid fragment() {\n  uvec2 a = uvec2(4u) % uvec2(0u);\n}\n")]
    // サンプラーの仮引数 (GD0334)
    [InlineData("GD0334", "shader_type canvas_item;\nuniform sampler2D a : repeat_enable;\nuniform sampler2D b;\nvec4 f(sampler2D s) { return texture(s, vec2(0.0)); }\nvoid fragment() {\n  COLOR = f(a) + f(b);\n}\n")]
    [InlineData("GD0334", "shader_type canvas_item;\nuniform sampler2D a;\nvec4 f(sampler2D s) { return texture(s, vec2(0.0)); }\nvoid fragment() {\n  COLOR = f(TEXTURE) + f(a);\n}\n")]
    [InlineData("GD0334", "shader_type canvas_item;\nuniform sampler2D a : filter_nearest;\nuniform sampler2D b : filter_linear;\nvec4 g(sampler2D s) { return texture(s, vec2(0.0)); }\nvec4 f(sampler2D s) { return g(s); }\nvoid fragment() {\n  COLOR = g(b) + f(a);\n}\n")]
    [InlineData("GD0334", "shader_type sky;\nvec4 f(samplerCube s) { return texture(s, vec3(0.0)); }\nvoid sky() {\n  COLOR = f(RADIANCE).rgb;\n}\n")]
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
    // varying: vertex → fragment / light、fragment → light (整数は flat が要らない)、vertex で書く前の読み込み (後ろに呼び出しが無い)、
    // vertex で書いた varying を最初に使う関数の dFdx (Godot はそこで調べるのをやめる)、vertex での out への受け渡し
    [InlineData("shader_type spatial;\nvarying vec3 v;\nvarying flat int k;\nvoid vertex() {\n  v = VERTEX;\n  v.x += 1.0;\n  k = 1;\n}\nvoid fragment() {\n  ALBEDO = v * float(k);\n}\nvoid light() {\n  DIFFUSE_LIGHT = v;\n}\n")]
    [InlineData("shader_type spatial;\nvarying vec3 v;\nvarying int k;\nvarying bool b;\nvoid fragment() {\n  v = vec3(1.0);\n  k = 2;\n  b = true;\n}\nvoid light() {\n  DIFFUSE_LIGHT = v * float(k);\n  if (b) {\n    DIFFUSE_LIGHT = vec3(0.0);\n  }\n}\n")]
    [InlineData("shader_type spatial;\nvarying float v;\nvoid vertex() {\n  float a = v;\n  v = sin(1.0);\n}\n")]
    [InlineData("shader_type spatial;\nvarying float v;\nfloat f() {\n  float a = v;\n  return dFdx(a);\n}\nvoid g(out float x) { x = 1.0; }\nvoid vertex() {\n  v = 1.0;\n  float b = f();\n  g(v);\n}\n")]
    // 配列: { } の大きさの省略、const の大きさの式、大きさの同じ代入と受け渡し、配列を返す関数
    [InlineData("shader_type spatial;\nconst int N = 2;\nuniform float u[N];\nvoid f(float x[2]) {}\nfloat[2] g() { return float[2](1.0, 2.0); }\nvoid fragment() {\n  float a[] = { 1.0, 2.0 };\n  float c[N] = float[2](1.0, 2.0);\n  float[2] d = { 1.0, 2.0 };\n  float e[N + 1];\n  float h[2u];\n  float b[2] = g();\n  f(b);\n  c = b;\n}\n")]
    // 定数の式 (組み込み関数、組み込みの変数、メンバー、添字、?:)、スカラーと float と uvec の 0 での割り算 (Godot は何も言わない)
    [InlineData("shader_type spatial;\nconst float A = 1.0;\nconst vec2 B = vec2(A, 2.0) * 2.0;\nconst float C[2] = { 1.0, 2.0 };\nconst float S = sin(1.0);\nvoid fragment() {\n  const float e = A * 2.0 + B.x + C[0] + TIME;\n  const float f = true ? 1.0 : 2.0;\n  int a = 4 / 0;\n  float b = 1.0 / 0.0;\n  uvec2 c = uvec2(4u) / uvec2(0u);\n  int z = 0;\n  ivec2 d = ivec2(4) / ivec2(z);\n}\n")]
    // サンプラーの仮引数: 同じフィルターの uniform (ほかのヒントは比べない)、同じ組み込み、仮引数ごとに違う設定
    [InlineData("shader_type canvas_item;\nuniform sampler2D a : filter_linear, source_color;\nuniform sampler2D b : filter_linear, hint_default_white;\nvec4 f(sampler2D s) { return texture(s, vec2(0.0)); }\nvec4 g(sampler2D s, sampler2D t) { return texture(s, vec2(0.0)) + texture(t, vec2(0.0)); }\nvoid fragment() {\n  COLOR = f(a) + f(b) + g(TEXTURE, a) + g(TEXTURE, b);\n}\n")]
    public async Task Godotが通す書き方に誤りを出さない(string code)
        => Assert.Contains("1 ファイルを解析: エラー 0 件, 警告 0 件, 情報 0 件", await AnalyzeAsync(code), StringComparison.Ordinal);

    /// <summary>project.godot にレンダラーを書いて解析する。<paramref name="renderer"/> が空なら書かない (Forward+)。</summary>
    private Task<string> AnalyzeWithRendererAsync(string renderer, string code)
    {
        File.WriteAllText(Path.Combine(_workDirectory, "project.godot"),
            renderer.Length == 0 ? "config_version=5\n" : $"config_version=5\n\n[rendering]\n\nrenderer/rendering_method=\"{renderer}\"\n");
        return AnalyzeAsync(code);
    }

    private static string Varyings(string shaderType, int count)
    {
        IEnumerable<string> names = Enumerable.Range(0, count).Select(i => $"v{i}");
        return $"shader_type {shaderType};\n{string.Concat(names.Select(n => $"varying vec4 {n};\n"))}void vertex() {{\n{string.Concat(names.Select(n => $"  {n} = vec4(1.0);\n"))}}}\n";
    }

    [Theory]
    // Compatibility では high-end の組み込み関数を使えない。hint_normal_roughness_texture は Forward+ だけ (種類より先に調べる)
    [InlineData("gl_compatibility", "GD0001", "shader_type spatial;\nuniform sampler2D t;\nvoid fragment() {\n  ALBEDO = textureGather(t, vec2(0.0)).rgb;\n}\n")]
    [InlineData("gl_compatibility", "GD0001", "shader_type spatial;\nvoid fragment() {\n  float a = fma(1.0, 2.0, 3.0) + dFdxFine(UV.x);\n}\n")]
    [InlineData("mobile", "GD0001", "shader_type spatial;\nuniform sampler2D t : hint_normal_roughness_texture;\n")]
    [InlineData("mobile", "GD0001", "shader_type canvas_item;\nuniform sampler2D t : hint_normal_roughness_texture;\n")]
    [InlineData("gl_compatibility", "GD0001", "shader_type spatial;\nuniform sampler2D t : hint_normal_roughness_texture;\n")]
    // CURRENT_RENDERER はレンダラーで決まる
    [InlineData("", "CF0001", "shader_type spatial;\n#if CURRENT_RENDERER == RENDERER_FORWARD_PLUS\nint a = 1;\n#endif\n")]
    [InlineData("mobile", "CF0001", "shader_type spatial;\n#if CURRENT_RENDERER == RENDERER_MOBILE\nint a = 1;\n#endif\n")]
    [InlineData("gl_compatibility", "CF0001", "shader_type spatial;\n#if CURRENT_RENDERER == RENDERER_COMPATIBILITY\nint a = 1;\n#endif\n")]
    public async Task レンダラーで変わる誤りを報告する(string renderer, string id, string code)
        => Assert.Contains($"error {id}", await AnalyzeWithRendererAsync(renderer, code), StringComparison.Ordinal);

    [Theory]
    [InlineData("", "shader_type spatial;\nuniform sampler2D t : hint_normal_roughness_texture;\nvoid fragment() {\n  ALBEDO = textureGather(t, vec2(0.0)).rgb;\n}\n")]
    [InlineData("mobile", "shader_type spatial;\nuniform sampler2D t;\nvoid fragment() {\n  ALBEDO = textureGather(t, vec2(0.0)).rgb + vec3(dFdx(UV.x));\n}\n")]
    [InlineData("gl_compatibility", "shader_type spatial;\nvoid fragment() {\n  ALBEDO = vec3(dFdx(UV.x));\n}\n")]
    [InlineData("", "shader_type spatial;\n#if CURRENT_RENDERER == RENDERER_MOBILE\nint a = 1;\n#endif\n")]
    [InlineData("mobile", "shader_type spatial;\n#if CURRENT_RENDERER != RENDERER_MOBILE\nint a = 1;\n#endif\n")]
    public async Task レンダラーで通るものに誤りを出さない(string renderer, string code)
        => Assert.Contains("1 ファイルを解析: エラー 0 件, 警告 0 件, 情報 0 件", await AnalyzeWithRendererAsync(renderer, code), StringComparison.Ordinal);

    [Theory]
    // Forward+ と Mobile は組み込みの varying が spatial で 15、canvas_item で 9 を使う。Compatibility は 0。上限は 32
    [InlineData("", "spatial", 17, false)]
    [InlineData("", "spatial", 18, true)]
    [InlineData("mobile", "spatial", 18, true)]
    [InlineData("", "canvas_item", 23, false)]
    [InlineData("", "canvas_item", 24, true)]
    [InlineData("gl_compatibility", "spatial", 32, false)]
    [InlineData("gl_compatibility", "spatial", 33, true)]
    public async Task varyingの数の上限はレンダラーで変わる(string renderer, string shaderType, int count, bool reported)
    {
        string output = await AnalyzeWithRendererAsync(renderer, Varyings(shaderType, count));

        Assert.Equal(reported, output.Contains("error GD0104", StringComparison.Ordinal));
        if (reported)
        {
            // 最初に上限を超えた varying (最後のもの) で報告する
            Assert.Contains($"a.gdshader({count + 1}:", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task 行列と配列のvaryingは列と要素の数で数える()
    {
        Assert.Contains("a.gdshader(6:", await AnalyzeAsync(
            "shader_type spatial;\nvarying mat4 a;\nvarying mat4 b;\nvarying mat4 c;\nvarying mat4 d;\nvarying mat4 e;\nvoid vertex() {\n  e = mat4(1.0);\n}\n"), StringComparison.Ordinal);
        Assert.Contains("a.gdshader(3:", await AnalyzeAsync(
            "shader_type spatial;\nvarying vec4 a[10];\nvarying vec4 b[8];\nvoid vertex() {\n  a[0] = vec4(1.0);\n  b[0] = vec4(1.0);\n}\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 環境の表示にプロジェクトとレンダラーを書く()
    {
        File.WriteAllText(Path.Combine(_workDirectory, "project.godot"), "config_version=5\n\n[rendering]\n\nrenderer/rendering_method=\"mobile\"\n");
        File.WriteAllText(Path.Combine(_workDirectory, "a.gdshader"), "shader_type spatial;\n");
        StringWriter output = new();
        StringWriter error = new();

        await Program.RunAsync([_workDirectory, "--no-config", "--env"], BuiltInAnalyzers.All, output, error);

        Assert.Contains($"プロジェクト (res://): {_workDirectory} (ファイルから上へ辿って見つけた project.godot)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("レンダラー: Mobile (project.godot の rendering/renderer/rendering_method)", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("HLSL", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 設定のレンダラーはproject_godotより優先する()
    {
        File.WriteAllText(Path.Combine(_workDirectory, "project.godot"), "config_version=5\n");
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "godot:\n  renderer: gl_compatibility\n");
        File.WriteAllText(Path.Combine(_workDirectory, "a.gdshader"), "shader_type spatial;\nvoid fragment() {\n  float a = fma(1.0, 2.0, 3.0);\n}\n");
        StringWriter output = new();
        StringWriter error = new();

        await Program.RunAsync([_workDirectory], BuiltInAnalyzers.All, output, error);

        Assert.Contains("error GD0001", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task res_の取り込みをプロジェクトのフォルダーから解決する()
    {
        Directory.CreateDirectory(Path.Combine(_workDirectory, "shaders", "include"));
        Directory.CreateDirectory(Path.Combine(_workDirectory, "materials"));
        File.WriteAllText(Path.Combine(_workDirectory, "project.godot"), "config_version=5\n");
        File.WriteAllText(Path.Combine(_workDirectory, "shaders", "include", "a.gdshaderinc"), "float helper_a() { return 1.0; }\n");
        File.WriteAllText(Path.Combine(_workDirectory, "shaders", "include", "b.gdshaderinc"), "float helper_b() { return 2.0; }\n");
        string shader = Path.Combine(_workDirectory, "materials", "m.gdshader");
        File.WriteAllText(shader,
            "shader_type spatial;\n#include \"res://shaders/include/a.gdshaderinc\"\n#include \"../shaders/include/b.gdshaderinc\"\nvoid fragment() {\n  ALBEDO = vec3(helper_a() + helper_b());\n}\n");
        StringWriter output = new();
        StringWriter error = new();

        // ファイルを直接渡す (フォルダーの外の ../ と res:// も、プロジェクトの中なら読む)。環境は拡張子で決まる。
        await Program.RunAsync([shader, "--no-config"], BuiltInAnalyzers.All, output, error);

        Assert.Contains("1 ファイルを解析: エラー 0 件, 警告 0 件, 情報 0 件", output.ToString(), StringComparison.Ordinal);
    }
}
