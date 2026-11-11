using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Slang;

namespace Shaderlyn.Tests;

/// <summary>
/// Slang の構文解析の検証 (SG1)。
/// </summary>
/// <remarks>
/// 通る例は、slangc 2026.19 が誤りにしない書き方 (slang の例とテスト、SaschaWillems の Vulkan の例、Falcor にあるもの)。
/// </remarks>
public sealed class SlangSyntaxTests
{
    private static ShaderSyntaxTree Parse(string code)
        => SlangSyntaxTree.Parse(SourceText.From(code, "a.slang"));

    private static string Describe(ShaderSyntaxTree tree)
        => string.Join("\n", tree.Diagnostics.Select(d => $"{d.Location.LineSpan.Start} {d.Id}: {d.GetMessage()}"));

    [Theory]
    // 行番号の無い #line、__has_feature、型の位置の組み込み、functype の仮引数、引数の無い添字、$for
    [InlineData("#if __has_feature(hlsl_vk_buffer_pointer)\nint known() { return 1; }\n#endif\n#line\nint f() { return 0; }\n")]
    [InlineData("interface IHasElement { associatedtype Element; }\nstruct Holder<each T> where nonempty(T) where expand each T : IHasElement\n{\n    __first(T).Element value;\n}\nint applyIt(functype(int) -> int fn, int x) { return fn(x); }\n")]
    [InlineData("struct A { __subscript() -> int { get { return 1; } } }\nint g(A a)\n{\n    int r = 0;\n    $for(i in Range(0, 5))\n    {\n        r += a[];\n    }\n    return r;\n}\n")]
    // モジュール
    [InlineData("module adam;\nimport common;\n__exported import Utils.Math.MathHelpers;\nimplementing adam;\n__include \"a.slang\";\n")]
    // ; を省いた構造体と列挙型、可視性
    [InlineData("struct VSInput\n{\n    float3 Pos;\n}\nstruct VSOutput\n{\n    float4 Pos : SV_POSITION;\n}\npublic enum Mode\n{\n    A = 0,\n    B,\n}\n")]
    [InlineData("public struct AdamState\n{\n    internal float mean;\n    __init() {}\n    __init(float m) { mean = m; }\n}\n")]
    // func・let・var・ジェネリック・インターフェイス・where
    [InlineData("func add(a : int, b : int) -> int { return a + b; }\nfunc f<T : IArithmetic>(x : T) -> T where T : IDifferentiable { return x; }\nfunc g(Buf* buf, float4 v);\n")]
    [InlineData("interface IFoo { float get(); }\nstruct Foo<let N : int = 2, T> : IFoo, IBar\n{\n    float get() { return N; }\n}\nT twice<T : IArithmetic>(T x) { return x + x; }\n")]
    [InlineData("void f()\n{\n    let a = 1;\n    var b : float = 2.0;\n    var c = twice<float>(1.0) + MLVec<4>.fromArray({ 1, 2, 3, 4 }).x;\n    for (var i = 0; i < 4; i++) { }\n    if (let v = opt) { }\n}\n")]
    // extension・typealias・associatedtype・property・__subscript
    [InlineData("extension bool : IPrintable { void print() { } }\ntypealias Real = float;\ninterface IRandom { associatedtype State : IState; }\nstruct S\n{\n    property float x { get { return 1; } set { } }\n    __subscript(int i) -> float { get { return 0; } }\n}\n")]
    // 属性 (vk::、, で並べたもの、__generic の後ろ)、ポインター、型の中の型
    [InlineData("[[vk::binding(0, 0)]] RaytracingAccelerationStructure accelStruct;\n[vk::push_constant] int* dest;\n[shader(\"compute\"), numthreads(1, 1, 1)]\nvoid main() { }\n__generic<T : __BuiltinFloatingPointType, let N : int>\n[Differentiable]\nvector<T, N> f(vector<T, N> v) { return v; }\n")]
    [InlineData("uniform RWStructuredBuffer<float>.Handle params;\nParameterBlock<DescriptorHandle<Buffer<uint>>> param;\nvoid f(SceneGradientFlag.Differential flag, inout DifferentialPair<MLVec<4>> input)\n{\n    A.Differential d;\n    float4* p = (float4*)ptr;\n    float4 v = *p + p->x;\n    out.position = 1;\n}\n")]
    // 式: is / as / no_diff / ラムダ / try / new / 複数の添字 / 数のスウィズル / 16 進数の浮動小数 / 生の文字列
    [InlineData("void f()\n{\n    bool b = x is Foo;\n    let y = x as Foo;\n    T = no_diff perp(N);\n    let l = (int a) => a + 1;\n    let m = try g(1);\n    let o = new Impl();\n    float t = tensor[0, 1];\n    float2 z = 0.xx;\n    float h = 0x1p-24;\n    printf(R\"(a \"%d\")\", 5);\n}\n")]
    // 文: do { } catch / throw / defer / ラベル / __target_switch / spirv_asm
    [InlineData("void f()\n{\n    do { g(); } catch (err : MyError) { }\n    defer { h(); }\n    outer: for (int i = 0; i < 2; i++) { break outer; }\n    __target_switch { case hlsl: break; }\n}\nint g() throws MyError { throw MyError.Fail; }\n")]
    // GLSL の書き方 (Slang は GLSL も読む)
    [InlineData("#version 460\nlayout(std430, binding = 0) buffer testBuffer\n{\n    uint data;\n}\nlayout(local_size_x = 1) in;\nprecision highp float;\n")]
    // #language と typedef の ; の省略
    [InlineData("#language slang 2026\ntypedef uint Type\nstruct S { Type t; }\n")]
    // 数の接尾辞 (slangc が通すもの)、無限大のリテラル、数のスウィズル
    [InlineData("void f()\n{\n    var a = 1.0hf + 1.0LF + 1.0fl + 1.0h;\n    var b = 1zU + 1uLL + 1Lu + 1llu;\n    float c = 2.4#INFf;\n    float2 d = 0.xx;\n}\n")]
    public void slangcが通す書き方を構文エラーにしない(string code)
    {
        ShaderSyntaxTree tree = Parse(code);

        Assert.True(tree.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error), Describe(tree));
    }

    [Theory]
    [InlineData("void f()\n{\n    float a = (1.0 + 2.0;\n}\n", 3)]
    [InlineData("struct S\n{\n    float a\n}\n", 4)]
    [InlineData("func f(a : int) -> int\n{\n    return a +;\n}\n", 3)]
    [InlineData("struct S { int a; }\ntypedef int;\n", 2)]
    // 使えない数の接尾辞 (数のすぐ後ろに続けた名前も)
    [InlineData("void f()\n{\n    float x = 1.0ff;\n}\n", 3)]
    [InlineData("void f()\n{\n    float x = 1.0d;\n}\n", 3)]
    [InlineData("void f()\n{\n    int x = 0LUL;\n}\n", 3)]
    [InlineData("void f()\n{\n    int x = 0ZZ;\n}\n", 3)]
    [InlineData("void f()\n{\n    float x = 1.0z;\n}\n", 3)]
    public void 構文の誤りを報告する(string code, int line)
    {
        ShaderSyntaxTree tree = Parse(code);

        Assert.Contains(tree.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Location.LineSpan.Start.Line + 1 == line);
    }
}
