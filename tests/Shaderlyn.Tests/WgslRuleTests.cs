using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// WGSL に当てる C 系の共通のルールの検証 (WG2)。
/// </summary>
/// <remarks>
/// 通る例は naga 30 が誤りにしない書き方 (naga・wgpu の試験、webgpu-samples、Bevy にあるもの)。CLI に .wgsl を渡して、WGSL の環境で解析する。
/// </remarks>
public sealed class WgslRuleTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-wgsl-rules-{Guid.NewGuid():N}");

    public WgslRuleTests() => Directory.CreateDirectory(_workDirectory);

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

    private async Task<string> AnalyzeAsync(string code, string fileName = "a.wgsl")
    {
        File.WriteAllText(Path.Combine(_workDirectory, fileName), code);
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([Path.Combine(_workDirectory, fileName), "--no-config"], BuiltInAnalyzers.All, output, error);
        Assert.True(error.ToString().Length == 0, error.ToString());
        return output.ToString();
    }

    [Theory]
    [InlineData("CF0310", "fn f() -> f32 {\n    return nope;\n}\n")]
    [InlineData("CF0402", "fn g(x: f32) -> f32 { return x; }\nfn f() -> f32 {\n    return g(1.0, 2.0);\n}\n")]
    // 型を書かない let は、初期値の型になる (抽象的な数は具体的な型にする)
    [InlineData("CF0410", "fn f() {\n    let a = vec3f(1.0, 2.0, 3.0);\n    let b = a.xy;\n    var c: vec4f = b;\n}\n")]
    [InlineData("CF0312", "fn f() {\n    let a = vec3<f32>(1.0);\n    let d = a.w;\n}\n")]
    // i32 と f32 の間は暗黙に変換しない
    [InlineData("CF0410", "fn f() {\n    let a = 1i;\n    var b: f32 = a;\n}\n")]
    // 関数の外の初期値、構造体どうし、宣言されていない型、組み込み関数の引数の数、構造体のメンバーの重複
    [InlineData("CF0410", "const x: i32 = 1.0;\n")]
    [InlineData("CF0410", "var<private> a: vec2<f32> = vec2<i32>(1i, 2i);\n")]
    [InlineData("CF0410", "struct Foo { a: u32 }\nstruct Bar { a: u32 }\nfn f() {\n    let x: Bar = Foo(1);\n}\n")]
    [InlineData("CF0411", "struct Foo { a: u32 }\nstruct Bar { a: u32 }\nfn f() -> Bar {\n    return Foo(1);\n}\n")]
    [InlineData("CF0310", "const a: Vec = 10;\n")]
    [InlineData("CF0402", "fn f() -> f32 {\n    return sin(1.0, 2.0);\n}\n")]
    [InlineData("CF0314", "struct A {\n    a: f32,\n    a: f32,\n}\n")]
    // 書き込めない値と、アドレスを取れない値
    [InlineData("WG0309", "fn f() {\n    let a = 10;\n    a = 20;\n}\n")]
    [InlineData("WG0309", "fn f(x: f32) {\n    x += 1.0;\n}\n")]
    [InlineData("WG0309", "@group(0) @binding(0) var<storage> data: array<f32>;\nfn f() {\n    data[0] = 1.0;\n}\n")]
    [InlineData("WG0309", "struct U { a: f32 }\n@group(0) @binding(0) var<uniform> u: U;\nfn f() {\n    u.a = 1.0;\n}\n")]
    [InlineData("WG0309", "fn f() {\n    var v = vec2(0);\n    v.xy = vec2(1);\n}\n")]
    [InlineData("WG0330", "fn f() {\n    var v: vec2<f32>;\n    let p = &v.x;\n}\n")]
    [InlineData("WG0330", "fn f() {\n    var m: mat2x2<f32>;\n    let p = &m[0][0];\n}\n")]
    // 型引数を書かない値の作成の成分の数
    [InlineData("CF0404", "fn f() {\n    _ = vec4(1, 2, 3);\n}\n")]
    // enable していない拡張の機能
    [InlineData("WG0001", "fn f() {\n    let h = 1.0h;\n}\n")]
    [InlineData("WG0001", "var<private> v: vec3<f16>;\n")]
    [InlineData("WG0001", "@group(0) @binding(0) var t: binding_array<texture_2d<f32>, 4>;\n")]
    [InlineData("WG0001", "@vertex fn vs() -> @builtin(clip_distances) array<f32, 1> { return array<f32, 1>(0.0); }\n")]
    [InlineData("WG0001", "struct O { @location(0) @blend_src(0) a: vec4f, }\n")]
    public async Task nagaが誤りにする書き方を報告する(string id, string code)
        => Assert.Contains($"error {id}", await AnalyzeAsync(code), StringComparison.Ordinal);

    [Theory]
    // 抽象的な数は、具体的な型へ変換できる。const は抽象的なまま残る
    [InlineData("const N = 4;\nfn f() -> f32 {\n    var a: f32 = 1;\n    let b: u32 = N;\n    let c = N * 2.0;\n    var d: f32 = c;\n    return a + 2;\n}\n")]
    // 後ろで宣言した関数・定数・構造体を使える
    [InlineData("fn f() -> S {\n    return S(g(), K);\n}\nfn g() -> f32 { return 1.0; }\nconst K = 2u;\nstruct S { a: f32, b: u32, }\n")]
    // 型を作る名前 (vec3(…)、array<f32, 2>(…))、組み込み関数、値を捨てる _ =
    [InlineData("fn f(t: texture_2d<f32>, s: sampler) -> vec4f {\n    let v = vec3(1.0, 2.0, 3.0);\n    let a = array<f32, 2>(1.0, 2.0);\n    _ = a;\n    let d = textureDimensions(t);\n    let n: f32 = length(v) + dot(v, v);\n    return textureSample(t, s, vec2f(0.5)) * n + vec4f(v, f32(d.x));\n}\n")]
    // 書き込める値 (var、read_write のバッファー、1 成分のスウィズル、ポインターの先) と、アドレスを取れる値 (行列の列)
    [InlineData("@group(0) @binding(0) var<storage, read_write> data: array<f32>;\nvar<private> g: vec4f;\nfn f(p: ptr<function, f32>) {\n    data[0] = 1.0;\n    g.x = 2.0;\n    *p = 3.0;\n    var m: mat2x2<f32>;\n    let q = &m[0];\n    var i = 0;\n    i++;\n}\n")]
    // enable した拡張、自分で宣言した同じ名前の型
    [InlineData("enable f16;\nenable wgpu_ray_tracing_pipeline;\nfn f() -> f16 {\n    let h: vec3h = vec3<f16>(1.0h);\n    return h.x;\n}\n@group(0) @binding(0) var a: acceleration_structure;\n")]
    [InlineData("struct RayDesc { flags: u32, }\nfn f(r: RayDesc) -> u32 { return r.flags; }\n")]
    // ポインター、loop と continuing、switch
    [InlineData("fn g(p: ptr<function, f32>) { *p = 1.0; }\nfn f(n: u32) -> u32 {\n    var x = 0.0;\n    g(&x);\n    var i = 0u;\n    loop {\n        if i >= n { break; }\n        continuing { i++; }\n    }\n    switch n {\n        case 0u, 1u: { return 1u; }\n        default: { return i; }\n    }\n}\n")]
    public async Task nagaが通す書き方を報告しない(string code)
        => Assert.DoesNotContain(" error ", await AnalyzeAsync(code), StringComparison.Ordinal);
}
