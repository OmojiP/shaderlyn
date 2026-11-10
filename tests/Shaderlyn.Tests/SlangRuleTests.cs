using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// Slang に当てる C 系の共通のルールの検証 (SG2)。
/// </summary>
/// <remarks>
/// 通る例は slangc 2026.19 が誤りにしない書き方 (slang の例とテスト、Falcor にあるもの)。CLI に .slang を渡して、Slang の環境で解析する。
/// </remarks>
public sealed class SlangRuleTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-slang-rules-{Guid.NewGuid():N}");

    public SlangRuleTests() => Directory.CreateDirectory(_workDirectory);

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

    private async Task<string> AnalyzeAsync(string code, string fileName = "a.slang")
    {
        File.WriteAllText(Path.Combine(_workDirectory, fileName), code);
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([Path.Combine(_workDirectory, fileName), "--no-config"], BuiltInAnalyzers.All, output, error);
        Assert.True(error.ToString().Length == 0, error.ToString());
        return output.ToString();
    }

    [Theory]
    [InlineData("CF0310", "float f()\n{\n    return nope;\n}\n")]
    [InlineData("CF0402", "float g(float x) { return x; }\nfloat f()\n{\n    return g(1.0, 2.0);\n}\n")]
    [InlineData("CF0021", "import missing.module;\nfloat f() { return 1.0; }\n")]
    // 型を書かない let / var は、初期値の式の型になる
    [InlineData("CF0410", "void f()\n{\n    let a = float3(1, 2, 3);\n    var b = a.xy;\n    float4 c = b;\n}\n")]
    [InlineData("CF0312", "void f()\n{\n    let a = float3(1, 2, 3);\n    let d = a.w;\n}\n")]
    // 構造体のメンバーとメソッド (extension や property から足されない型)
    [InlineData("CF0312", "struct S { float a; float get() { return a; } }\nfloat f(S s) { return s.b; }\n")]
    [InlineData("CF0312", "struct S { float a; float get() { return a; } }\nfloat f(S s) { return s.put(); }\n")]
    // 構造体の { } の要素が多すぎる (足りないのは既定の値で埋める)
    [InlineData("CF0412", "struct S { float a; float b; }\nvoid f()\n{\n    S s = { 1.0, 2.0, 3.0 };\n}\n")]
    // 宣言の置き場所
    [InlineData("SG0101", "void f(struct { int a; } x) { }\n")]
    [InlineData("SG0101", "typealias A = struct S { };\n")]
    [InlineData("SG0101", "struct Container\n{\n    namespace ns { }\n}\n")]
    [InlineData("SG0101", "interface IFoo\n{\n    enum E { A, B }\n}\n")]
    [InlineData("SG0101", "float4 main() : SV_Target\n{\n    float helper(int x) { return x; }\n    return 0;\n}\n")]
    // [NoDiscard] の戻り値を捨てる
    [InlineData("SG0338", "struct T\n{\n    [NoDiscard]\n    float load() { return 1.0; }\n}\nvoid f(T t)\n{\n    t.load();\n}\n")]
    [InlineData("SG0338", "[NoDiscard]\nint g() { return 1; }\nvoid f(bool c)\n{\n    c && g() > 0;\n    for (int i = 0; i < 1; g()) { }\n}\n")]
    // 構造体の外からの private のメンバー
    [InlineData("SG0401", "struct T\n{\n    private int member;\n    private int func() { return member; }\n}\nvoid f()\n{\n    T t;\n    t.member = 2;\n}\n")]
    [InlineData("SG0401", "struct T\n{\n    private int member;\n    private int func() { return member; }\n}\nint f()\n{\n    T t;\n    return t.func();\n}\n")]
    // enum と整数の間、数と構造体の間は暗黙に変換しない
    [InlineData("HL0347", "enum Color { Red, Green }\nvoid f(int val)\n{\n    Color c = val;\n}\n")]
    [InlineData("HL0347", "enum Color { Red, Green }\nvoid f(Color c)\n{\n    uint y = c;\n}\n")]
    [InlineData("CF0410", "struct S { int a; }\nvoid f()\n{\n    S s;\n    s = 0;\n}\n")]
    // switch の外の case と、自分自身に依る static const
    [InlineData("SG0321", "void f()\n{\n    case 0:\n    return;\n}\n")]
    [InlineData("SG0321", "void f(uint i)\n{\n    switch (i)\n    {\n    case 0:\n        {\n        case 1:\n            break;\n        }\n    }\n}\n")]
    [InlineData("SG0317", "struct S\n{\n    static const int kA = kB;\n    static const int kB = kA;\n}\n")]
    [InlineData("SG0317", "static const int kVal = kVal + 1;\n")]
    public async Task slangcが誤りにする書き方を報告する(string id, string code)
        => Assert.Contains($"error {id}", await AnalyzeAsync(code), StringComparison.Ordinal);

    [Theory]
    // 標準ライブラリの関数と型、ジェネリック、let / var、func
    [InlineData("func twice<T : IArithmetic>(x : T) -> T { return x + x; }\nvoid f()\n{\n    let a = twice<float>(1.0);\n    var b : float = lerp(a, 2.0, 0.5);\n    CoopVec<float, 4> v = CoopVec<float, 4>(1.0);\n    Optional<int> o = none;\n    uint8_t2 c = uint8_t2(1, 2);\n    printf(\"%f\", a);\n}\n")]
    // 構造体の空の { } と足りない { } (既定の値で埋める)、void の関数から void の式を返す
    [InlineData("struct S { float a = 1; float b = 2; }\nvoid g() { }\nvoid f()\n{\n    S s = {};\n    S t = { 1.0 };\n    return g();\n}\n")]
    // 局所変数で仮引数を隠す、ジェネリックの制約だけが違う多重定義、可変長の仮引数
    [InlineData("float f(float v)\n{\n    float v = 2.0;\n    return v;\n}\n__generic<T : __BuiltinFloatingPointType>\nbool h() { return true; }\n__generic<T : __BuiltinIntegerType>\nbool h() { return false; }\nint count<each T>(T values) { return 0; }\nvoid k() { int n = count(1, 2, 3) + count(); }\n")]
    // extension で足したメソッド、インターフェイスの extension で足して基底から来るメソッド、property
    [InlineData("struct V { float x; }\nextension V { float twice() { return x * 2; } }\nfloat f(V v) { return v.twice(); }\n")]
    [InlineData("interface ICounter { int add(int x); }\nextension ICounter { int increment() { return add(1); } }\nstruct C : ICounter { int s; int add(int x) { return s + x; } }\nint f(C c) { return c.increment(); }\n")]
    [InlineData("struct P { float _v; property float v { get { return _v; } } }\nfloat f(P p) { return p.v; }\n")]
    // 64 ビットに広がる整数のリテラル
    [InlineData("void f()\n{\n    uint64_t a = 0x9E3779B97F4A7C15ull;\n    int64_t b = 0x1122334455667788;\n}\n")]
    // [NoDiscard] の戻り値を使う
    [InlineData("[NoDiscard]\nint g() { return 1; }\nvoid consume(int x) { }\nvoid f()\n{\n    let a = g();\n    consume(g());\n    g() + a;\n    if (g() > 0) { }\n}\n")]
    // private のメンバーは、構造体と中の構造体の中から使える
    [InlineData("struct T\n{\n    private int member;\n    int get(T other) { return other.member + member; }\n    struct Inner { int read(T t) { return t.member; } }\n    int pub;\n}\nint f(T t) { return t.pub + t.get(t); }\n")]
    // 変換できるもの: enum から基になる型、インターフェイスへの値、__init を持つ構造体、組み込み関数と同じ名前の関数
    [InlineData("enum Mode : uint { A, B }\nenum Color { Red }\ninterface IFoo { int get(); }\nstruct B : IFoo { int get() { return 1; } IFoo self() { return this; } }\nstruct Converted { float3 v; __init(float3 x) { v = x; } }\nvoid take(Converted c) { }\nstruct M { float2x2 m; }\nfloat2 mul(M a, float2 v) { return mul(a.m, v); }\nvoid f(Mode m, Color c)\n{\n    uint u = m;\n    int i = c;\n    take(float3(0.0));\n}\n")]
    // 書ける置き場所: 修飾子と __generic の後ろの構造体、関数の中の局所の構造体のメソッド、インターフェイスの中の __constraint
    [InlineData("__generic<T>\nstruct Box { T v; }\n__target_intrinsic(cpp, \"Foo\")\nstruct Foo { int a; }\npublic struct Pub { int b; }\ninterface IFoo { }\ninterface IReq { __constraint int : IFoo; }\nint f()\n{\n    struct Op { int g(int v) { return v; } };\n    Op o;\n    return o.g(1);\n}\n")]
    // ポインター
    [InlineData("void f(float3x4* p)\n{\n    float3x4 m = p[0];\n    float4* q = (float4*)p;\n}\n")]
    public async Task slangcが通す書き方に誤りを出さない(string code)
        => Assert.Contains("1 ファイルを解析: エラー 0 件, 警告 0 件, 情報 0 件", await AnalyzeAsync(code), StringComparison.Ordinal);

    [Fact]
    public async Task importしたモジュールの宣言を使え_モジュールには指摘を出さない()
    {
        Directory.CreateDirectory(Path.Combine(_workDirectory, "util"));

        // モジュールの関数の本体は読まないので、戻り値の無い経路を取り込む側で報告しない。
        File.WriteAllText(Path.Combine(_workDirectory, "util", "helpers.slang"), "module helpers;\npublic float twice(float x) { return x * 2; }\n");
        File.WriteAllText(Path.Combine(_workDirectory, "util", "more-things.slang"), "public float thrice(float x) { return x * 3; }\n");

        string output = await AnalyzeAsync(
            "import util.helpers;\nimport util.more_things;\nimport slang.neural;\nimport \"glsl\";\nfloat f() { return twice(1.0) + thrice(1.0) + undefinedThing; }\n", "main.slang");

        Assert.Contains("error CF0310: 'undefinedThing'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("'twice'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("'thrice'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("CF0021", output, StringComparison.Ordinal);
        Assert.DoesNotContain("helpers.slang", output, StringComparison.Ordinal);
    }

    [Theory]
    // ベクトルを含んで成分が足りない形は、slangc が非推奨の警告 (E31200) にとどめるので警告。スカラーだけの形は呼び出しが曖昧になるので誤り
    [InlineData("float4(v, 1)", "warning CF0404")]
    [InlineData("float4(1.0, v)", "warning CF0404")]
    [InlineData("float4(1, 2, 3)", "error CF0404")]
    [InlineData("float4(v, 1, 2, 3)", "error CF0404")]
    public async Task 成分の足りない値の作成はslangcに合わせて警告か誤りにする(string construction, string expected)
        => Assert.Contains(expected, await AnalyzeAsync($"void f()\n{{\n    float2 v = float2(1, 2);\n    float4 c = {construction};\n}}\n"), StringComparison.Ordinal);

    [Fact]
    public async Task 値を返さない経路は警告にする()
        => Assert.Contains("warning CF0501", await AnalyzeAsync("int f(int a)\n{\n    if (a > 0)\n    {\n        return 1;\n    }\n}\n"), StringComparison.Ordinal);
}
