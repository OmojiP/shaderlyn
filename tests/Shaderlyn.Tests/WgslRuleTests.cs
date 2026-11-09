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
    // 定数式: 数の範囲、シフトの量とあふれ、配列の大きさ、添字、const の初期値、const_assert、条件、呼び出しの番号、再帰
    [InlineData("WG0315", "const i32min = -0x80000000i;\n")]
    [InlineData("WG0315", "fn f() {\n    let x: u32 = -1;\n}\n")]
    [InlineData("WG0336", "const N: u32 = 1u >> 32;\n")]
    [InlineData("WG0336", "const N: i32 = 1i << 31;\n")]
    [InlineData("WG0336", "fn f() {\n    var x: u32;\n    var n = x << 32;\n}\n")]
    [InlineData("WG0308", "alias Bad = array<f32, 0>;\n")]
    [InlineData("WG0308", "const length: f32 = 2.718;\nalias Bad = array<f32, length>;\n")]
    [InlineData("WG0307", "fn f() -> f32 {\n    let a = array<f32, 3>(0.0, 1.0, 2.0);\n    return a[3];\n}\n")]
    [InlineData("WG0307", "fn f() -> f32 {\n    let v = vec2f(0.0);\n    return v[-1];\n}\n")]
    [InlineData("WG0307", "fn f() {\n    var x: array<i32, 1>;\n    var y = x[1.0];\n}\n")]
    [InlineData("WG0317", "fn f() {\n    let a = 5;\n    const c = a;\n}\n")]
    [InlineData("WG0317", "override o: i32;\nfn f() {\n    const c = o;\n}\n")]
    [InlineData("WG0317", "fn f() {\n    let a = 5;\n    const_assert a != 0;\n}\n")]
    [InlineData("WG0337", "const N = 4;\nconst_assert N > 8;\n")]
    [InlineData("WG0337", "fn f() {\n    const_assert 1 > 2;\n}\n")]
    [InlineData("WG0311", "const_assert(5);\n")]
    [InlineData("WG0311", "fn f() {\n    loop {\n        continuing {\n            break if 1;\n        }\n    }\n}\n")]
    [InlineData("WG0311", "fn f(n: u32) {\n    if n { }\n}\n")]
    [InlineData("WG0311", "fn f() {\n    _ = select(1, 2, 9001);\n}\n")]
    [InlineData("WG0303", "fn f(id: u32) {\n    _ = subgroupBroadcast(123, id);\n}\n")]
    [InlineData("WG0332", "fn f() {\n    g();\n}\nfn g() {\n    f();\n}\n")]
    [InlineData("WG0332", "fn f() {\n    f();\n}\n")]
    // 型の引数と、その宣言には使えない型
    [InlineData("WG0324", "var<private> m: mat3x3<i32>;\n")]
    [InlineData("WG0324", "const a = vec2<vec2f>();\n")]
    [InlineData("WG0324", "var t: texture_2d<bool>;\n")]
    [InlineData("WG0324", "var s: texture_storage_1d<rgba, write>;\n")]
    [InlineData("WG0324", "@group(0) @binding(0) var<bad> t: texture_2d<f32>;\n")]
    [InlineData("WG0324", "fn f() {\n    _ = mat2x2();\n}\n")]
    [InlineData("WG0403", "struct Bad { data: sampler }\n")]
    [InlineData("WG0403", "struct Bad { data: array<f32>, other: f32, }\n")]
    [InlineData("WG0403", "alias Bad = array<texture_2d<f32>, 4>;\n")]
    [InlineData("WG0403", "var input: u64;\n")]
    [InlineData("WG0403", "var<workgroup> initialized: u32 = 0u;\n")]
    [InlineData("WG0403", "fn f(arg: ptr<uniform, f32>) { }\n")]
    [InlineData("WG0403", "fn f(arg: array<f32>) { }\n")]
    [InlineData("WG0403", "fn f() {\n    var x: atomic<u32>;\n}\n")]
    [InlineData("WG0403", "fn f() {\n    let x = array<u32>(0, 1, 2);\n}\n")]
    [InlineData("WG0403", "fn f() {\n    _ = atomic<i32>(0);\n}\n")]
    [InlineData("WG0403", "struct A { a: atomic<u32> }\n@group(0) @binding(0) var<storage> atom: A;\nfn f() -> A {\n    return atom;\n}\n")]
    // エントリポイントの入出力と、ステージで使えない操作
    [InlineData("WG0322", "@fragment\nfn fragment(_input: vec4<f32>) -> @location(0) vec4<f32> {\n    return _input;\n}\n")]
    [InlineData("WG0322", "struct FragmentIn {\n    @location(0) pos: vec4<f32>,\n    uv: vec2<f32>,\n}\n@fragment\nfn fragment(_input: FragmentIn) -> @location(0) vec4<f32> {\n    return _input.pos;\n}\n")]
    [InlineData("WG0322", "struct VertexOut {\n    @location(0) a: vec4<f32>,\n}\n@vertex\nfn vertex() -> VertexOut {\n    return VertexOut(vec4<f32>());\n}\n")]
    [InlineData("WG0322", "enable dual_source_blending;\nstruct FragmentOutput {\n    @location(0) @blend_src(0) output0: vec4<f32>,\n}\n")]
    [InlineData("WG0322", "enable dual_source_blending;\nstruct FragmentOutput {\n    @location(0) @blend_src(0) output0: vec4<f32>,\n    @location(0) @blend_src(1) output1: f32,\n}\n")]
    [InlineData("WG0002", "@vertex\nfn main() -> @builtin(position) vec4<f32> {\n    if true {\n        discard;\n    }\n    return vec4<f32>();\n}\n")]
    [InlineData("WG0002", "fn helper() {\n    workgroupBarrier();\n}\n@fragment\nfn main() -> @location(0) vec4f {\n    helper();\n    return vec4f();\n}\n")]
    // メモリの配置、大きさ、属性
    [InlineData("WG0103", "struct Bar {\n    @align(7) data: array<f32>,\n}\n")]
    [InlineData("WG0103", "struct Bar {\n    @align(8) data: vec3<f32>,\n}\n")]
    [InlineData("WG0103", "struct Bar {\n    @size(0) data: array<f32, 1>,\n}\n")]
    [InlineData("WG0103", "@group(0) @binding(0) var<uniform> input: array<vec2<u32>, 2>;\n")]
    [InlineData("WG0339", "alias LargeArray = array<u32, 1 << 29>;\n")]
    [InlineData("WG0339", "struct TwoArrays {\n    arr1: array<u32, 1 << 28>,\n    arr2: array<u32, 1 << 28>,\n}\n")]
    [InlineData("WG0101", "struct S {\n    @must_use a: i32,\n}\n")]
    [InlineData("WG0101", "@must_use\nfn use_me(a: i32) {\n    let x = a;\n}\n")]
    [InlineData("WG0101", "@must_use\n@must_use\nfn use_me(a: i32) -> i32 {\n    return 10;\n}\n")]
    [InlineData("WG0338", "@must_use\nfn use_me(a: i32) -> i32 {\n    return 10;\n}\nfn useless() -> i32 {\n    use_me(1);\n    return 0;\n}\n")]
    // 無い拡張、演算子、組み込み関数と値の作成の実引数
    [InlineData("WG0003", "requires buffer_view;\n")]
    [InlineData("WG0003", "enable f61;\n")]
    [InlineData("WG0301", "fn foo(a: vec2<bool>, b: vec2<bool>) {\n    let y = a && b;\n}\n")]
    [InlineData("WG0302", "fn x() -> f32 {\n    return cross(vec4(0., 1., 2., 3.), vec4(0., 1., 2., 3.)).x;\n}\n")]
    [InlineData("WG0302", "fn foo() -> f32 {\n    return dot(vec4<f32>(), vec3<f32>());\n}\n")]
    [InlineData("WG0302", "fn foo() -> f32 {\n    return distance(vec2<f32>(), 0i);\n}\n")]
    [InlineData("WG0302", "fn x() -> i32 {\n    return i32(vec2<f32>(0.0));\n}\n")]
    [InlineData("WG0302", "fn x(a: u32) -> vec2f {\n    return vec2f(a);\n}\n")]
    [InlineData("WG0302", "fn f() {\n    var a: array<i32, 1> = array<i32, 1>(1.0);\n}\n")]
    [InlineData("WG0302", "fn x() {\n    _ = mat2x2<f32>(array(0, 1), vec2(2, 3));\n}\n")]
    [InlineData("WG0302", "fn f() {\n    _ = select(true, 1, false);\n}\n")]
    [InlineData("WG0302", "struct S { member: i32 }\nfn f(which: bool) -> S {\n    var x: S = S(1);\n    var y: S = S(2);\n    return select(x, y, which);\n}\n")]
    [InlineData("WG0302", "@group(0) @binding(0) var s: sampler;\nfn f() -> vec4f {\n    let a = 3;\n    return textureSample(a, s, vec2<f32>(0.0));\n}\n")]
    [InlineData("WG0302", "enable wgpu_int16;\nfn f() {\n    var v = i16(1);\n    v = subgroupAnd(v);\n}\n")]
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
    // 範囲の中の定数式、成り立つ const_assert、定数の添字、関数の中の 64 ビットの演算 (naga はあふれを見ない)
    [InlineData("const N = 4u;\nconst M: i32 = 1i << 30;\nconst_assert N < 8u;\nalias A = array<f32, N * 2u>;\nfn f(i: u32) -> f32 {\n    const_assert N == 4u;\n    var a: A;\n    let v = vec4f(1.0);\n    let m = mat2x2f(v.xy, v.zw);\n    if i > 0u && N > 1u { }\n    return a[7] + v[3] + m[1][1] + a[i] + select(0.0, 1.0, i > 2u);\n}\n")]
    // 使える型: 資源の変数、最後のメンバーの大きさを決めない配列、ストレージのテクスチャ、ポインターの仮引数、型引数の無い vec2()
    [InlineData("struct Particles { count: u32, data: array<vec4f>, }\n@group(0) @binding(0) var<storage, read_write> particles: Particles;\n@group(0) @binding(1) var tex: texture_2d<f32>;\n@group(0) @binding(2) var out: texture_storage_2d<rgba8unorm, write>;\n@group(0) @binding(3) var samp: sampler;\nvar<workgroup> shared_data: array<atomic<u32>, 64>;\nfn g(p: ptr<function, f32>) -> f32 { return *p; }\nfn f() -> vec2<i32> {\n    var x = 1.0;\n    let m = mat2x2<f32>();\n    _ = g(&x);\n    return vec2();\n}\n")]
    // 正しいエントリポイント: 構造体の入出力、@blend_src の 2 つの出力、計算シェーダーの同期、フラグメントシェーダーの discard と微分
    [InlineData("enable dual_source_blending;\nstruct VsOut { @builtin(position) pos: vec4f, @location(0) uv: vec2f, }\nstruct FsOut { @location(0) @blend_src(0) a: vec4f, @location(0) @blend_src(1) b: vec4f, }\n@group(0) @binding(0) var t: texture_2d<f32>;\n@group(0) @binding(1) var s: sampler;\n@vertex\nfn vs(@builtin(vertex_index) i: u32) -> VsOut { return VsOut(vec4f(), vec2f()); }\n@fragment\nfn fs(input: VsOut) -> FsOut {\n    if input.uv.x < 0.0 { discard; }\n    let c = textureSample(t, s, input.uv) * dpdx(input.uv.x);\n    return FsOut(c, c);\n}\n@compute @workgroup_size(64)\nfn cs(@builtin(global_invocation_id) id: vec3u) {\n    workgroupBarrier();\n}\n")]
    // 正しい配置 (uniform の vec4 の配列、@align(16) の構造体、@size)、@must_use の戻り値を使う呼び出し
    [InlineData("struct Light { @align(16) pos: vec3f, @size(16) power: f32, }\nstruct U { lights: array<Light, 4>, weights: array<vec4f, 2>, }\n@group(0) @binding(0) var<uniform> u: U;\n@must_use\nfn twice(x: f32) -> f32 { return x * 2.0; }\nfn f() -> f32 {\n    _ = twice(1.0);\n    let y = twice(2.0);\n    return y + u.lights[0].power;\n}\n")]
    // 正しい実引数: 変換の作成、抽象的な数、成分ごとの論理演算、textureGather の成分の番号、知っている拡張
    [InlineData("enable f16;\nrequires readonly_and_readwrite_storage_textures;\n@group(0) @binding(0) var t: texture_2d<f32>;\n@group(0) @binding(1) var s: sampler;\nfn f(a: u32, b: vec2<bool>, c: vec2<bool>) -> vec4f {\n    let v = vec2f(f32(a), 1);\n    let w = vec2f(vec2u(a));\n    let x = cross(vec3f(1.0), vec3(0.0, 1.0, 0.0)) * dot(v, w) * distance(v, vec2(0.0));\n    let y = select(vec2(0.0), v, b & c);\n    let z = sin(1) + clamp(0.5, 0.0, 1.0);\n    return textureGather(0, t, s, v) + vec4f(x, z) * f32(all(y > vec2(0.0)) || any(b));\n}\n")]
    // enable した拡張、自分で宣言した同じ名前の型
    [InlineData("enable f16;\nenable wgpu_ray_tracing_pipeline;\nfn f() -> f16 {\n    let h: vec3h = vec3<f16>(1.0h);\n    return h.x;\n}\n@group(0) @binding(0) var a: acceleration_structure;\n")]
    [InlineData("struct RayDesc { flags: u32, }\nfn f(r: RayDesc) -> u32 { return r.flags; }\n")]
    // ポインター、loop と continuing、switch
    [InlineData("fn g(p: ptr<function, f32>) { *p = 1.0; }\nfn f(n: u32) -> u32 {\n    var x = 0.0;\n    g(&x);\n    var i = 0u;\n    loop {\n        if i >= n { break; }\n        continuing { i++; }\n    }\n    switch n {\n        case 0u, 1u: { return 1u; }\n        default: { return i; }\n    }\n}\n")]
    public async Task nagaが通す書き方を報告しない(string code)
        => Assert.DoesNotContain(" error ", await AnalyzeAsync(code), StringComparison.Ordinal);
}
