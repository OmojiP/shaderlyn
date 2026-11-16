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
    // 初期値の中では宣言している名前がまだ見えず、for の変数はループの外で見えない
    [InlineData("CF0310", "void f()\n{\n    int e = e;\n}\n")]
    [InlineData("CF0310", "int f()\n{\n    for (int i = 0; i < 2; i++) { }\n    return i;\n}\n")]
    // 形の違う値どうしの演算子
    [InlineData("SG0301", "float3 f(float3 a, float4 b)\n{\n    return a + b;\n}\n")]
    [InlineData("SG0301", "float2 f(float2 v, float2x2 m)\n{\n    return v * m;\n}\n")]
    // extension とインターフェイスの中のフィールド、extension の既定値、参照の型のフィールド
    [InlineData("SG0101", "struct T { int a; }\nextension T\n{\n    int b;\n}\n")]
    [InlineData("SG0101", "interface I\n{\n    int v;\n}\n")]
    [InlineData("SG0101", "struct Foo<T> { T v; }\nextension<T = int> Foo<T>\n{\n    T get() { return v; }\n}\n")]
    [InlineData("SG0406", "struct A { Ref<float> f; }\n")]
    // 可視性の書き方
    [InlineData("SG0402", "private struct Hidden { }\n")]
    [InlineData("SG0402", "struct Inner { public int x; }\n")]
    [InlineData("SG0402", "struct IS { int a; }\npublic IS make() { return {}; }\n")]
    [InlineData("SG0402", "struct IS { int a; }\npublic struct P { public IS s; }\n")]
    // 構造体の値への組み込みの演算子、ConstantBuffer の値の縮める変換
    [InlineData("SG0301", "struct S { int a; }\nbool f(S s)\n{\n    return s == s;\n}\n")]
    [InlineData("SG0301", "struct S { int a; }\nvoid f(S s)\n{\n    let t = s * 2;\n}\n")]
    [InlineData("CF0410", "ConstantBuffer<float4x4> m;\nfloat4 f()\n{\n    float4 v = m;\n    return v;\n}\n")]
    // ?: の枝の型が合わない
    [InlineData("SG0301", "enum E { A, B }\nuint f(bool c, uint u)\n{\n    return c ? u : E.B;\n}\n")]
    [InlineData("SG0301", "float4 f(bool c)\n{\n    return c ? float3(1) : float4(1);\n}\n")]
    // ビット演算とシフトの浮動小数
    [InlineData("SG0301", "float f(float a, float b)\n{\n    return a | b;\n}\n")]
    [InlineData("SG0301", "int f(int i, float a)\n{\n    return i << a;\n}\n")]
    [InlineData("SG0301", "float f(float a)\n{\n    return ~a;\n}\n")]
    // [mutating] でないメソッドの中の、フィールドへの書き込み
    [InlineData("SG0309", "struct S\n{\n    float r;\n    void set(float x) { r = x; }\n}\n")]
    [InlineData("SG0309", "struct S\n{\n    float3 c;\n    void grow() { this.c.x += 1; }\n}\n")]
    // 読むだけの資源の要素への書き込み
    [InlineData("SG0309", "StructuredBuffer<uint> sb;\nvoid f()\n{\n    sb[0] = 1;\n}\n")]
    [InlineData("SG0309", "StructuredBuffer<uint> sb;\nvoid f()\n{\n    InterlockedAdd(sb[0], 1);\n}\n")]
    [InlineData("SG0309", "Texture2D<float4> tex;\nvoid f()\n{\n    tex[uint2(0, 0)] = float4(0);\n}\n")]
    // スカラーでない条件、点で書いた列挙子の変換
    [InlineData("SG0311", "struct S { int a; }\nvoid f(S s)\n{\n    if (s) { }\n}\n")]
    [InlineData("SG0311", "void f(float3 v)\n{\n    while (v) { break; }\n}\n")]
    [InlineData("HL0347", "enum Color { Red, Green }\nfloat f()\n{\n    float x = Color.Green;\n    return x;\n}\n")]
    // テクスチャの要素にできない型
    [InlineData("SG0405", "Texture2D<float4x4> t;\n")]
    [InlineData("SG0405", "struct S { float a; }\nRWBuffer<S> b;\n")]
    // 成分を減らす暗黙の変換は誤り (HLSL と違い、警告で通さない)
    [InlineData("CF0410", "float f(float4 v)\n{\n    float3 t = v;\n    return t.x;\n}\n")]
    [InlineData("CF0401", "float g(float x) { return x; }\nfloat f(float4 v)\n{\n    return g(v);\n}\n")]
    [InlineData("CF0411", "float3x3 f(float4x4 m)\n{\n    return m;\n}\n")]
    // out / inout に暗黙の変換の要る値を渡す (int と uint の組だけは通る)
    [InlineData("CF0401", "void a(out float x) { x = 0; }\nvoid b(double y)\n{\n    a(y);\n}\n")]
    [InlineData("CF0401", "void a(inout half x) { }\nvoid b(float y)\n{\n    a(y);\n}\n")]
    // 宣言されていない型の名前 (変数・仮引数・戻り値・フィールドの型、構造体の基底)
    [InlineData("CF0310", "NotAType f(float x)\n{\n    return x;\n}\n")]
    [InlineData("CF0310", "struct S : INotExist { }\n")]
    [InlineData("CF0310", "struct S { Missing m; }\n")]
    [InlineData("CF0021", "import missing.module;\nfloat f() { return 1.0; }\n")]
    // 型を書かない let / var は、初期値の式の型になる
    [InlineData("CF0410", "void f()\n{\n    let a = float3(1, 2, 3);\n    var b = a.xy;\n    float4 c = b;\n}\n")]
    [InlineData("CF0312", "void f()\n{\n    let a = float3(1, 2, 3);\n    let d = a.w;\n}\n")]
    // 構造体のメンバーとメソッド (extension や property から足されない型)
    [InlineData("CF0312", "struct S { float a; float get() { return a; } }\nfloat f(S s) { return s.b; }\n")]
    [InlineData("CF0312", "struct S { float a; float get() { return a; } }\nfloat f(S s) { return s.put(); }\n")]
    // 構造体の { } の要素が多すぎる (足りないのは既定の値で埋める)
    [InlineData("CF0412", "struct S { float a; float b; }\nvoid f()\n{\n    S s = { 1.0, 2.0, 3.0 };\n}\n")]
    // 構造体のフィールドと仮引数の重複
    [InlineData("CF0314", "struct S\n{\n    int f;\n    float f;\n}\n")]
    [InlineData("CF0314", "int g(int size, float size)\n{\n    return 0;\n}\n")]
    // リテラルの書き方
    [InlineData("SG0004", "uint f()\n{\n    return '\\q';\n}\n")]
    [InlineData("SG0004", "uint f()\n{\n    return 'ab';\n}\n")]
    [InlineData("SG0004", "uint64_t f()\n{\n    return 18446744073709551616;\n}\n")]
    // 宣言の置き場所
    [InlineData("SG0101", "void f(struct { int a; } x) { }\n")]
    [InlineData("SG0101", "typealias A = struct S { };\n")]
    [InlineData("SG0101", "struct Container\n{\n    namespace ns { }\n}\n")]
    [InlineData("SG0101", "interface IFoo\n{\n    enum E { A, B }\n}\n")]
    [InlineData("SG0101", "float4 main() : SV_Target\n{\n    float helper(int x) { return x; }\n    return 0;\n}\n")]
    // インターフェイスの適合の循環
    [InlineData("SG0342", "interface IValue { float get(); }\nstruct BadImpl : IValue\n{\n    IValue child;\n    float get() { return 0; }\n}\n")]
    [InlineData("SG0342", "interface IFoo { float a(); }\ninterface IBar { float b(); }\nstruct BarImpl : IBar { IFoo foo; float b() { return 0; } }\nstruct FooImpl : IFoo { IBar bar; float a() { return 0; } }\n")]
    // 資源を包めない型、属性と可視性の繰り返し
    [InlineData("SG0404", "StructuredBuffer<Texture2D> textures;\n")]
    [InlineData("SG0404", "float f(Optional<SamplerState> s) { return 0; }\n")]
    [InlineData("SG0102", "[numthreads(1, 1, 1)]\n[numthreads(2, 1, 1)]\nvoid main() { }\n")]
    [InlineData("SG0102", "public internal int v() { return 0; }\n")]
    // 修飾子、レジスタ、static const の初期値、配列にできない型
    [InlineData("SG0102", "const float k = 1.0;\n")]
    [InlineData("SG0102", "uniform float u = 2.0;\n")]
    [InlineData("SG0102", "void f()\n{\n    groupshared int a;\n}\n")]
    [InlineData("SG0103", "Texture2D tex : register(u5);\n")]
    [InlineData("SG0103", "RWStructuredBuffer<float> buf : register(t0);\n")]
    [InlineData("SG0316", "uniform float foo;\nstatic const float a = foo * 2.0;\n")]
    [InlineData("SG0316", "int getX() { return 1; }\nstatic const int c = getX();\n")]
    [InlineData("SG0403", "struct P { float x; }\nuniform ParameterBlock<P> qs[2];\n")]
    [InlineData("SG0403", "struct WithUnbounded { int data[]; }\nWithUnbounded many[3];\n")]
    // 取り除かれた宣言
    [InlineData("SG0341", "[RemovedSince(-1, \"This struct is removed\")]\nstruct RemovedStruct { int x; }\nvoid f()\n{\n    RemovedStruct s;\n}\n")]
    [InlineData("SG0341", "[RemovedSince(2025, \"gone\")]\nint old() { return 1; }\nint f() { return old(); }\n")]
    // [NoDiscard] の戻り値を捨てる
    [InlineData("SG0338", "struct T\n{\n    [NoDiscard]\n    float load() { return 1.0; }\n}\nvoid f(T t)\n{\n    t.load();\n}\n")]
    [InlineData("SG0338", "[NoDiscard]\nint g() { return 1; }\nvoid f(bool c)\n{\n    c && g() > 0;\n    for (int i = 0; i < 1; g()) { }\n}\n")]
    // void を返す関数の [NoDiscard] (宣言の誤り)
    [InlineData("SG0338", "[NoDiscard]\nvoid touch() { }\n")]
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
    // ループの外の break と continue、2 つ目の default、値の重なる case (列挙子は値で比べる)、整数でない switch の値
    [InlineData("SG0321", "void f() { break; }\n")]
    [InlineData("SG0321", "void f() { if (true) { continue; } }\n")]
    [InlineData("SG0321", "void f(int a)\n{\n    switch (a) { default: break; default: break; }\n}\n")]
    [InlineData("SG0321", "enum F { X = 1, Y = 1 }\nvoid f(int a)\n{\n    switch (a) { case F.X: break; case F.Y: break; }\n}\n")]
    [InlineData("SG0321", "void f(float v)\n{\n    switch (v) { case 1: break; }\n}\n")]
    // 括弧で包まない条件の代入
    [InlineData("SG0343", "void f(int a)\n{\n    if (a = 0) { }\n}\n")]
    [InlineData("SG0343", "void f(int a)\n{\n    do { } while (a = 3);\n}\n")]
    // 大きさの決まらない型: 最後でないフィールド、それをフィールドに持つ構造体、局所変数、StructuredBuffer の要素、定数バッファーのメンバー
    [InlineData("SG0308", "struct V\n{\n    int c[];\n    int a;\n}\n")]
    [InlineData("SG0308", "struct V { int n; int a[]; }\nstruct Q { int id; V v; }\n")]
    [InlineData("SG0308", "struct V { int n; int a[]; }\nvoid f()\n{\n    V v;\n}\n")]
    [InlineData("SG0308", "void f()\n{\n    float a[];\n}\n")]
    [InlineData("SG0308", "struct V { int n; int a[]; }\nStructuredBuffer<V> items;\n")]
    [InlineData("SG0308", "cbuffer ubo\n{\n    float4 data[];\n}\n")]
    // 宣言に使えない型: void の変数、ビットフィールド、列挙型の基の型、特殊化定数
    [InlineData("SG0407", "void f()\n{\n    void a;\n}\n")]
    [InlineData("SG0407", "struct S\n{\n    int foo : 33;\n}\n")]
    [InlineData("SG0407", "struct S\n{\n    float foo : 10;\n}\n")]
    [InlineData("SG0407", "enum Bad : float { A, B }\n")]
    [InlineData("SG0407", "[vk::constant_id(1)]\nconst int2 v;\n")]
    // 属性の値
    [InlineData("SG0304", "[numthreads(0, 1, 1)]\nvoid main() { }\n")]
    [InlineData("SG0304", "[numthreads(1, 1, 1)]\n[WaveSize(5)]\nvoid main() { }\n")]
    [InlineData("SG0304", "[COM(\"0000000-0000-0000-0000-000000000001\")]\ninterface IA { void f(); }\n")]
    [InlineData("SG0304", "[RemovedSince(10000, \"x\")]\nvoid old() { }\n")]
    // 初期値の無い static const
    [InlineData("SG0318", "static const int globalVar;\n")]
    // 修飾子: 初期値の groupshared、ポインターの coherent、typedef の volatile、static の特殊化定数
    [InlineData("SG0102", "groupshared uint shared = 1;\n")]
    [InlineData("SG0102", "globallycoherent int* p;\n")]
    [InlineData("SG0102", "typedef volatile float VolatileFloat;\n")]
    [InlineData("SG0102", "[vk::constant_id(1)]\nstatic const int x = 2;\n")]
    // 名前の無い enum class
    [InlineData("SG0101", "enum class { A, B }\n")]
    // 同じ形の関数の 2 つ目の本体と、列挙子の重複
    [InlineData("CF0314", "int foo() { return 1; }\nint foo() { return 2; }\n")]
    [InlineData("CF0314", "enum E { A, A }\n")]
    // throw と try: throws の無い関数、defer の中、throws と違う列挙型
    [InlineData("SG0345", "enum MyError { Fail }\nint g() throws MyError { return 1; }\nvoid f()\n{\n    let n = try g();\n}\n")]
    [InlineData("SG0345", "enum MyError { Fail }\nvoid f()\n{\n    throw MyError.Fail;\n}\n")]
    [InlineData("SG0345", "enum MyError { Fail }\nvoid f() throws MyError\n{\n    defer {\n        throw MyError.Fail;\n    }\n}\n")]
    [InlineData("SG0345", "enum E1 { Fail }\nenum E2 { Fail }\nint g() throws E1\n{\n    throw E2.Fail;\n}\n")]
    // static のメソッドの this と static でないメンバー、型の名前からの static でないフィールド
    [InlineData("SG0344", "struct Foo\n{\n    int a;\n    static int func() { return this.a; }\n}\n")]
    [InlineData("SG0344", "struct Test\n{\n    void instanceMethod() { }\n    static void staticMethod() { instanceMethod(); }\n}\n")]
    [InlineData("SG0344", "struct Color { int Red; }\nvoid test()\n{\n    int x = Color.Red;\n}\n")]
    // ポインターでない値の ->、スカラーの添字、関数の sizeof
    [InlineData("SG0301", "struct T { int member; }\nvoid f()\n{\n    T val;\n    val->member = 2;\n}\n")]
    [InlineData("SG0301", "int foo()\n{\n    int x;\n    return x[0];\n}\n")]
    [InlineData("SG0301", "int func()\n{\n    return sizeof(func);\n}\n")]
    // 負の数の添字
    [InlineData("CF0510", "int f()\n{\n    int a[3];\n    return a[-1];\n}\n")]
    // ファイルの直下の __init と、extension の中の仮引数の無い __init
    [InlineData("SG0101", "__init() {}\n")]
    [InlineData("SG0101", "struct S { int x; }\nextension S\n{\n    __init() { x = 0; }\n}\n")]
    // [UnscopedEnum] と enum class、[[vk::offset]] の置き場所、dyn の置き場所、構造体のフィールドの groupshared
    [InlineData("SG0102", "[UnscopedEnum]\nenum class E { V }\n")]
    [InlineData("SG0102", "[[vk::offset(16)]] struct S1 { float a; }\n")]
    [InlineData("SG0102", "dyn struct Bad { int x; }\n")]
    [InlineData("SG0102", "struct P { groupshared int x; }\n")]
    // new を書かない class の作成
    [InlineData("SG0346", "class MyClass\n{\n    int x;\n    __init() { x = 0; }\n}\nvoid test()\n{\n    MyClass obj = MyClass();\n}\n")]
    // 2 つの構造体の継承、override の書き忘れと当たるメソッドの無い override
    [InlineData("SG0347", "struct Base1 { int x; }\nstruct Base2 { int y; }\nstruct Derived : Base1, Base2 { int z; }\n")]
    [InlineData("SG0347", "interface IFoo { int getVal() { return 0; } }\nstruct Impl : IFoo { int getVal() { return 1; } }\n")]
    [InlineData("SG0347", "interface IFoo { int getVal() { return 0; } }\nstruct Impl : IFoo { override int getVal(int x) { return x; } }\n")]
    // 戻り値の型の無い __subscript、property の accessor の仮引数
    [InlineData("SG0348", "struct M\n{\n    int val[4];\n    __subscript(int x)\n    {\n        get { return val[x]; }\n    }\n}\n")]
    [InlineData("SG0348", "struct T\n{\n    property p : int\n    {\n        get(a) { return 0; }\n    }\n}\n")]
    [InlineData("SG0348", "struct T\n{\n    property q : float\n    {\n        set(c : int) { }\n    }\n}\n")]
    // 型の中と extension の中の演算子、標準のモジュールにだけ書ける修飾子
    [InlineData("SG0101", "struct S\n{\n    int x;\n    S operator+(S o) { return o; }\n}\n")]
    [InlineData("SG0101", "struct P { int v; }\nextension P\n{\n    P operator-(P o) { return o; }\n}\n")]
    [InlineData("SG0101", "struct Q\n{\n    int v;\n    [mutating]\n    __postfix Q operator++() { v += 1; return this; }\n}\n")]
    [InlineData("SG0102", "__magic_type(FloatType)\nstruct MyFloat\n{\n}\n")]
    // vector<T, N> を TN として見る (形の違う値どうしの演算子)
    [InlineData("SG0301", "void f()\n{\n    vector<int, 4> c;\n    vector<float, 3> d;\n    d = c + d;\n}\n")]
    // 多重定義のどれも構造体の値を受け取らない呼び出し (構造体の作成 Test() も構造体の値)
    [InlineData("CF0401", "struct Test {}\nfloat overloaded(float2 x, float y) { return 0; }\nfloat overloaded(float3 x, float y) { return 0; }\nfloat test()\n{\n    return overloaded(Test(), 0);\n}\n")]
    // 型引数の違うジェネリックの型どうしと、次元の違うテクスチャどうし
    [InlineData("CF0410", "struct G<T> { T v; }\nvoid f(G<int> gi, G<float> gf)\n{\n    gi = gf;\n}\n")]
    [InlineData("CF0410", "Texture1D<float> tex;\nvoid f()\n{\n    Texture2D<float> t2 = tex;\n}\n")]
    [InlineData("CF0410", "Texture1D<float> tex;\nvoid f()\n{\n    Texture1D<int> t1 = tex;\n}\n")]
    // if (let a = x) の a は then の側でだけ見える
    [InlineData("CF0310", "interface IFoo { int foo(); }\nstruct MyImpl : IFoo { int foo() { return 1; } }\nint test(IFoo foo)\n{\n    if (let a = foo as MyImpl)\n    {\n        return a.foo();\n    }\n    else if (a == none)\n    {\n        return -1;\n    }\n    return 0;\n}\n")]
    // throws の型とジェネリックの制約の型
    [InlineData("CF0310", "void computeMain() throws NotAType\n{\n}\n")]
    [InlineData("CF0310", "T addValues<T : IAdditive>(T left, T right) { return left; }\n")]
    [InlineData("CF0310", "T h<T>(T x) where T : IMissing { return x; }\n")]
    // extension の private のメソッドと、private の __subscript の外からの使用
    [InlineData("SG0401", "struct S { int v; }\nextension S\n{\n    private int extensionPrivate() { return 2; }\n}\nvoid test(S s)\n{\n    s.extensionPrivate();\n}\n")]
    [InlineData("SG0401", "struct T\n{\n    int member;\n    private __subscript(int i) -> int\n    {\n        get { return member; }\n    }\n}\nint test(T t)\n{\n    return t[0];\n}\n")]
    // ジェネリックの制約を満たさない型引数 (書いた型引数、推論した型引数、型の型引数、組み込みの型、基底の構造体の適合は受け継がない)
    [InlineData("SG0349", "interface IFoo { int get(); }\nstruct Bar {}\nvoid f<T : IFoo>(T x) {}\nvoid main()\n{\n    Bar b;\n    f(b);\n}\n")]
    [InlineData("SG0349", "interface IT { float t(float x); }\nstruct PlainData { float value; }\nfloat apply<T : IT>(T obj, float x) { return obj.t(x); }\nfloat test(PlainData p)\n{\n    return apply<PlainData>(p, 1.0);\n}\n")]
    [InlineData("SG0349", "interface IFoo { }\nstruct G<T : IFoo> { T v; }\nstruct None { }\nvoid f()\n{\n    G<None> g;\n}\n")]
    [InlineData("SG0349", "interface IFoo { }\nT g<T>(T x) where T : IFoo { return x; }\nvoid f()\n{\n    g<int>(1);\n}\n")]
    [InlineData("SG0349", "interface IFoo {}\nstruct Parent : IFoo {}\nstruct Child : Parent {}\nT getObject<T>() where T : IFoo { return T(); }\nvoid main()\n{\n    getObject<Child>();\n}\n")]
    // 微分する関数の、繰り返しの回数を決められないループ
    [InlineData("SG0350", "[Differentiable]\nfloat f(float x)\n{\n    float s = 0;\n    for (int i = 0; i < (int)x; i++) { s += x; }\n    return s;\n}\n")]
    [InlineData("SG0350", "[Differentiable]\nfloat f(float x)\n{\n    float s = 0;\n    int i = 0;\n    while (i < 5) { s += x; i++; }\n    return s;\n}\n")]
    [InlineData("SG0350", "[ForwardDifferentiable]\nfloat f(float x)\n{\n    float s = 0;\n    for (int i = 0; i < 5; i++) { i = (int)x; }\n    return s;\n}\n")]
    // 微分の属性の関数の名前、属性の置き場所、微分のグループのスレッドの数
    [InlineData("CF0310", "[BackwardDerivative(nonexistent_bwd)]\nfloat f(float x) { return x * x; }\n")]
    [InlineData("SG0102", "[MaybeDifferentiable]\nfloat bar(float x) { return x * x; }\n")]
    [InlineData("SG0102", "interface IFoo\n{\n    [TreatAsDifferentiable]\n    float compute(float x);\n}\n")]
    [InlineData("SG0304", "[shader(\"compute\")]\n[DerivativeGroupLinear]\n[numthreads(3, 1, 1)]\nvoid main() { }\n")]
    // 初期化子の並び: __init の仮引数の数、__init を持つ構造体を継承した構造体、資源の型のフィールドと変数
    [InlineData("SG0351", "struct One\n{\n    int x;\n    __init(int v) { x = v; }\n}\nvoid f()\n{\n    One o = {};\n}\n")]
    [InlineData("SG0351", "struct BaseCtor { int a; __init() { a = 2; } }\nstruct DerCtor : BaseCtor { int b = 1; }\nvoid f()\n{\n    DerCtor c = {};\n}\n")]
    [InlineData("SG0351", "struct WithTex { int v; Texture1D<float> t; }\nvoid f()\n{\n    WithTex w = {1};\n}\n")]
    [InlineData("SG0351", "void f()\n{\n    Texture2D<float4> t = {};\n}\n")]
    // テクスチャの offset の実引数 (定数バッファーのメンバー、代入し直す変数、ループの変数)
    [InlineData("SG0352", "Texture2D t;\nSamplerState s;\ncbuffer U\n{\n    float2 uv;\n    int2 offset;\n};\nfloat4 f()\n{\n    return t.Sample(s, uv, offset);\n}\n")]
    [InlineData("SG0352", "Texture2D t;\nSamplerState s;\nfloat4 f(float2 uv)\n{\n    int ii = 0;\n    if (uv.x > 0.0f) { ii = 1; }\n    return t.Sample(s, uv, int2(ii, ii));\n}\n")]
    [InlineData("SG0352", "Texture2D t;\nSamplerState s;\nfloat4 f(float2 uv)\n{\n    float4 r = 0;\n    for (int j = 0; j < 4; j++) { r += t.Sample(s, uv, int2(j, 0)); }\n    return r;\n}\n")]
    // 関わりの無い具体的な型どうしの is / as
    [InlineData("SG0301", "struct Dog { int bark; }\nstruct Cat { float meow; }\nvoid f()\n{\n    Dog d;\n    bool b = d is Cat;\n}\n")]
    // __floatAsInt の実引数 (浮動小数でない値と、定数でない値)
    [InlineData("SG0352", "void f()\n{\n    int a = __floatAsInt(42);\n}\n")]
    [InlineData("SG0352", "void f(float p)\n{\n    int a = __floatAsInt(p);\n}\n")]
    // 2026 の言語の、ベクトルを含んで成分の足りない作成と並び
    [InlineData("SG0341", "#language slang 2026\nvoid f()\n{\n    float2 v = float2(1, 2);\n    float4 c = float4(v, 1);\n}\n")]
    [InlineData("SG0341", "#language slang 2026\nvoid f()\n{\n    int4 v = { int2(1, 2), 3 };\n}\n")]
    // 重複した仮引数と列挙子の使用 (どちらを指すか決まらない)
    [InlineData("CF0314", "int f(int size, float size)\n{\n    return size;\n}\n")]
    [InlineData("CF0314", "enum E { A, A }\nE g() { return E::A; }\n")]
    // 空でありうる並び: nonempty の無い __first、nonempty を求める関数へ渡す並び、空のタプル
    [InlineData("SG0354", "int badValueUse<let each D : int>()\n{\n    return __first(D);\n}\n")]
    [InlineData("SG0354", "void consumeNonEmpty<let each D : int>() where nonempty(D) {}\nvoid badForward<let each D : int>()\n{\n    consumeNonEmpty<D>();\n}\n")]
    [InlineData("SG0354", "int f()\n{\n    return __first(makeTuple());\n}\n")]
    // extension で宣言した入れ子の型と、修飾した入れ子の型への数の代入
    [InlineData("CF0410", "struct N { float value; }\nextension N\n{\n    struct Config { float threshold; }\n}\nvoid f()\n{\n    N.Config c;\n    c = 0;\n}\n")]
    // 読むだけのポインターへの書き込みと、型・アクセス・アドレス空間の違うポインターの暗黙の変換
    [InlineData("SG0309", "typealias ReadPtr = Ptr<int, Access::Read, AddressSpace::Device>;\nvoid f(ReadPtr ptr)\n{\n    ptr[0] = 1;\n}\n")]
    [InlineData("CF0410", "int* processMemory;\nvoid f()\n{\n    Ptr<int, Access::ReadWrite> p6 = Ptr<int, Access::Read>(processMemory);\n}\n")]
    // countof(…) の制約の書き方と、型引数を書いた呼び出しの並びの数
    [InlineData("SG0354", "void badNotEqual<let N : int, each T>() where countof(T) != N { }\n")]
    [InlineData("SG0354", "void badRight<let N : int, each T>() where N == countof(T) { }\n")]
    [InlineData("SG0354", "void load<let N : int, each TIndex>(TIndex indices) where countof(TIndex) == N { }\nvoid f()\n{\n    load<2>(1, 2, 3);\n}\n")]
    // 要素の型の違う T* どうしと、インターフェイスの型の値のアドレス
    [InlineData("CF0410", "interface IBase { int getVal(); }\nstruct Foo : IBase { int val; int getVal() { return val; } }\nvoid f()\n{\n    Foo x;\n    Foo* pf = &x;\n    IBase* ip = pf;\n}\n")]
    [InlineData("CF0410", "interface IFoo { int get(); }\nvoid f(IFoo local)\n{\n    IFoo* p = &local;\n}\n")]
    // SamplerDescriptorHeap と ResourceDescriptorHeap の取り違え
    [InlineData("CF0410", "void f(uint i)\n{\n    Texture2D.Handle bad1 = SamplerDescriptorHeap[i];\n    SamplerState bad4 = ResourceDescriptorHeap[i];\n}\n")]
    // ジェネリックの型の値から具体的な数の型への初期化
    [InlineData("CF0410", "int implicitPlain<T : IArithmetic>(T t)\n{\n    int x = t;\n    return x;\n}\n")]
    // ラムダ式: 取り込んだ値への書き込み、戻り値の型の食い違い、コピーできない型の取り込み
    [InlineData("SG0309", "void test()\n{\n    int c = 0;\n    let lam = (int a) => {\n        c = a + 1;\n    };\n}\n")]
    [InlineData("SG0355", "void test()\n{\n    let lam = (int a) => {\n        if (a > 0)\n            return 1;\n        else\n            return 3.0;\n    };\n}\n")]
    // public の構造体の public でないフィールドは、作られる __init の仮引数にならない
    [InlineData("SG0351", "public struct HasPrivateMember\n{\n    private int data;\n}\nvoid f()\n{\n    HasPrivateMember a = HasPrivateMember({ 1 });\n}\n")]
    [InlineData("SG0351", "public struct Visibility\n{\n    internal int x;\n    public int y = 0;\n}\nvoid f()\n{\n    Visibility t1 = {1, 2};\n}\n")]
    // 読むだけの資源の要素のアドレス
    [InlineData("SG0356", "StructuredBuffer<float> constants;\nvoid f()\n{\n    float* p = __getAddress(constants[0]);\n}\n")]
    // [NoDiscard] の演算子と型引数つきの関数、配列の大きさの 0 での割り算と負の数、大きすぎる Z の整数
    [InlineData("SG0338", "struct W { int v; }\n[NoDiscard]\nW operator+(W a, W b) { return a; }\nvoid f(W w1, W w2)\n{\n    w1 + w2;\n}\n")]
    [InlineData("SG0338", "[NoDiscard]\nT identity<T>(T x) { return x; }\nvoid f()\n{\n    identity<int>(3);\n}\n")]
    [InlineData("SG0357", "void f()\n{\n    int a[1 / 0];\n}\n")]
    [InlineData("SG0357", "func bar() -> int[-1];\n")]
    [InlineData("SG0004", "void f()\n{\n    uint64_t x = 18446744073709551616Z;\n}\n")]
    // [mutating] のメソッドの、書き換えられない自分・定数バッファーのメンバー・ParameterBlock の値への呼び出し
    [InlineData("SG0309", "struct Counter\n{\n    int count;\n    [mutating] void increment() { count++; }\n    void bad() { increment(); }\n}\n")]
    [InlineData("SG0309", "struct Counter { int count; [mutating] void increment() { count++; } }\ncbuffer C\n{\n    Counter gCounter;\n}\nvoid main()\n{\n    gCounter.increment();\n}\n")]
    // 大きさを決めない配列の countof、関数の外の static の資源の初期値、each の無い型の仮引数の each、each の無い expand
    [InlineData("SG0308", "int gArr[];\nint f()\n{\n    return countof(gArr);\n}\n")]
    [InlineData("SG0102", "RWStructuredBuffer<uint> inputBuffer;\nstatic RWStructuredBuffer<uint> gBuffer = inputBuffer;\n")]
    [InlineData("SG0354", "void useEach<T>(expand each T values) { }\n")]
    [InlineData("SG0354", "int f()\n{\n    int x = 0;\n    expand (x + 1);\n    return x;\n}\n")]
    // ジェネリックの仮引数の書き方 (値の仮引数の型、制約に書けない型、前を指す制約、extension の型の仮引数、型引数の無いジェネリックの型)
    [InlineData("SG0358", "void useValue<let N>(int x) { }\n")]
    [InlineData("SG0358", "void withFloat<let F : float>(int x) { }\n")]
    [InlineData("SG0358", "struct Foo1<D : int> { }\n")]
    [InlineData("SG0358", "interface IFloat {}\ninterface IFoo<T : IFloat> {}\nvoid test1<Foo : IFoo<T>, T : IFloat>() { }\n")]
    [InlineData("SG0358", "extension<T> int\n{\n    void foo() { }\n}\n")]
    [InlineData("SG0358", "Buffer b0;\n")]
    // インターフェイスの要件の初期値
    [InlineData("SG0101", "interface Bug\n{\n    static const int badVar = 0;\n}\n")]
    // インターフェイスの要件の書き忘れ、[mutating] と static の食い違い
    [InlineData("SG0359", "interface IFoo\n{\n    void bar();\n}\nstruct T : IFoo\n{\n}\n")]
    [InlineData("SG0359", "interface IThing { int processValue(int inValue); }\nstruct Counter : IThing\n{\n    int state;\n    [mutating] int processValue(int inValue) { state += inValue; return state; }\n}\n")]
    [InlineData("SG0359", "interface Base { static int getValue(); }\nstruct Impl : Base\n{\n    int getValue() { return 5; }\n}\n")]
    // 言語のバージョンを書いたファイルの、存在型での特殊化
    [InlineData("SG0349", "#lang slang 2025\ninterface IFoo { float calc(float x); }\nfloat genericFunc<T : IFoo>(T obj, float x) { return obj.calc(x); }\nfloat f(IFoo obj)\n{\n    return genericFunc(obj, 3.0);\n}\n")]
    // 継承の誤り (class の継承・構造体の継承・#lang 2026 の構造体の継承)、インターフェイスの extension
    [InlineData("SG0347", "class C { int x; }\nstruct S : C { int y; }\n")]
    [InlineData("SG0347", "struct C { int x; }\nclass D : C { int y; }\n")]
    [InlineData("SG0347", "#lang 2026\nstruct B { int x; }\nstruct D : B { int y; }\n")]
    [InlineData("SG0358", "interface IFoo { int f(); }\nextension IFoo { int g() { return f(); } }\n")]
    // ジェネリックの仮引数の並び (可変長の並びの後ろ・後ろを指す既定の値)、型引数の数、where の形
    [InlineData("SG0358", "struct Bad1<each T, U> {}\n")]
    [InlineData("SG0358", "void badDefaultOrder<int A = B, int B = 10>() {}\n")]
    [InlineData("SG0358", "struct Foo<T, U> { T t; }\nvoid f() { Foo<int> b; }\n")]
    [InlineData("SG0358", "struct Foo<T, U = int> { T t; }\nvoid f() { Foo<int, float, int> b; }\n")]
    [InlineData("SG0358", "void f() { Buffer<> b; }\n")]
    [InlineData("SG0358", "interface IFoo {}\nint f<T>() where float : IFoo { return 0; }\n")]
    [InlineData("SG0358", "interface IAlpha {}\ninterface IBeta {}\nint f<T>() where T == IAlpha & IBeta { return 0; }\n")]
    [InlineData("SG0358", "struct BadConstraint<each T> where nonempty(int) {}\n")]
    [InlineData("SG0358", "struct Outer<let each D : int>\n{\n    struct Inner<let each E : int> where nonempty(D) {}\n}\n")]
    // インターフェイスの中の書き方 (__init と property の本体・override・片方だけの accessor の既定の実装)、[COM] のインターフェイス
    [InlineData("SG0101", "interface IBase\n{\n    __init() {}\n}\n")]
    [InlineData("SG0101", "interface I\n{\n    property int value { get { return 1; } }\n}\n")]
    [InlineData("SG0347", "interface IBase { int f(); }\ninterface IDerived : IBase\n{\n    override int f() {}\n}\n")]
    [InlineData("SG0348", "interface I\n{\n    property int value { get; set { } }\n}\n")]
    [InlineData("SG0101", "[COM(\"00000000-0000-0000-0000-000000000001\")]\ninterface ICOMWithAssocType\n{\n    associatedtype Element;\n}\n")]
    [InlineData("SG0347", "[COM(\"00000000-0000-0000-0000-000000000001\")]\ninterface ICOMBase { float getValue(); }\ninterface INonCOM : ICOMBase { float compute(); }\n")]
    [InlineData("SG0347", "[COM(\"00000000-0000-0000-0000-000000000001\")]\ninterface ICOMService { float query(); }\nstruct BadImpl : ICOMService { float v; float query() { return v; } }\n")]
    // as の右のインターフェイスと、制約の無いジェネリックの型の is
    [InlineData("SG0301", "interface I { int g(); }\nstruct C : I { int g() { return 1; } }\nI k(C c) { return c as I; }\n")]
    [InlineData("SG0301", "interface I { int g(); }\nbool f<T>(T t) { return t is I; }\n")]
    // 構文の誤り: ; の無い throw、名前の無いメンバー、accessor でない語、名前でないジェネリックの仮引数、制約の struct
    [InlineData("CF0001", "enum E { Fail }\nuint f(int n) throws E\n{\n    if (n < 0)\n        throw E.Fail\n    return (uint)n;\n}\n")]
    [InlineData("CF0001", "void main()\n{\n    float3 arr[3];\n    arr.;\n}\n")]
    [InlineData("CF0001", "struct MyType\n{\n    __subscript(int index) -> int\n    {\n        get { return index; }\n        se\n    }\n}\n")]
    [InlineData("CF0001", "int doThing<1>() { return 2; }\n")]
    [InlineData("CF0001", "__generic<T : struct> extension T\n{\n}\n")]
    // 名前空間に無いメンバー、型引数の違うジェネリックの型のフィールドどうしの代入
    [InlineData("CF0310", "namespace ns { int fb() { return 1; } }\nint f() { return ns.fa(); }\n")]
    [InlineData("CF0410", "struct G<T> { T v; }\nstruct Outer<T> { G<T> g; }\nstruct H { G<int> i; G<float> f; }\nvoid f(Outer<int> a, Outer<float> b, H c)\n{\n    a.g = b.g;\n    c.i = c.f;\n}\n")]
    // 微分の関数の仮引数の型と数、static のメソッドの微分、no_diff の書き方
    [InlineData("SG0360", "[ForwardDerivative(fwd_f)]\nfloat f(float x) { return x * x; }\nDifferentialPair<float> fwd_f(int x) { return diffPair(0.0, 0.0); }\n")]
    [InlineData("SG0360", "[BackwardDerivative(bwd_g)]\nfloat g(float x, float y) { return x + y; }\nvoid bwd_g(float dout) { }\n")]
    [InlineData("SG0360", "[BackwardDerivative(d_f)]\nfloat f(float x) { return x * x; }\nvoid d_f(float x, float.Differential dout) { }\n")]
    [InlineData("SG0360", "struct S\n{\n    [ForwardDerivative(fwd_m)]\n    static float m(float x) { return x * x; }\n    DifferentialPair<float> fwd_m(DifferentialPair<float> x) { return diffPair(0.0, 0.0); }\n}\n")]
    [InlineData("SG0360", "[ForwardDifferentiable]\nfloat m(float x)\n{\n    float y = no_diff x;\n    return y;\n}\n")]
    [InlineData("SG0360", "float nonDiff(float x) { return x; }\nfloat n(float x) { return no_diff nonDiff(x); }\n")]
    // 微分の型に無いフィールド、[DerivativeMember] の指す先と置き場所
    [InlineData("SG0361", "struct MyDiff : IDifferentiable\n{\n    typealias Differential = MyDiff;\n    float dy;\n}\nstruct MyType : IDifferentiable\n{\n    typealias Differential = MyDiff;\n    float x;\n}\n")]
    [InlineData("SG0361", "struct Bar : IDifferentiable\n{\n    typealias Differential = Bar;\n    float y;\n}\nstruct Foo : IDifferentiable\n{\n    typealias Differential = Bar;\n    [DerivativeMember(Differential.nonexistent)]\n    float x;\n}\n")]
    [InlineData("SG0361", "struct D : IDifferentiable\n{\n    typealias Differential = D;\n    float y;\n}\n[DerivativeMember(D.y)]\nfloat globalVar;\n")]
    // 微分する関数の中の後ろ向きの微分、let の DifferentialPair、__constref と [constref]、インターフェイスの要件と微分の属性
    [InlineData("SG0360", "[Differentiable]\nfloat fff(float x, float y) { return x + y; }\n[Differentiable]\nfloat ggg(float x, float y)\n{\n    var xp = diffPair(x, 1.0);\n    var yp = diffPair(y, 1.0);\n    bwd_diff(fff)(xp, yp, 1.0);\n    return fff(x, y) + xp.d;\n}\nfloat h(float x)\n{\n    var p = diffPair(x, 1.0);\n    var q = diffPair(x, 1.0);\n    bwd_diff(ggg)(p, q, 1.0);\n    return p.d;\n}\n")]
    [InlineData("SG0360", "[Differentiable]\nfloat square(float x) { return x * x; }\nvoid main()\n{\n    let x = diffPair(3.0, 1.0);\n    bwd_diff(square)(x, 1.0);\n}\n")]
    [InlineData("SG0360", "[Differentiable]\nfloat f(__constref float3 val) { return val.x; }\n")]
    [InlineData("SG0360", "interface IBar\n{\n    [Differentiable]\n    float original(float x);\n    [ForwardDerivativeOf(original)]\n    DifferentialPair<float> fwd_original(DifferentialPair<float> x);\n}\n")]
    // 自分を含む構造体、制約の無いジェネリックの型の値の演算子、get だけの property への書き込み
    [InlineData("SG0407", "struct TA { TB tb; }\nstruct TB { TA ta[2]; }\n")]
    [InlineData("SG0407", "struct Outer\n{\n    Outer next;\n    int y;\n}\n")]
    [InlineData("SG0301", "T add<T>(T a, T b) { return a + b; }\n")]
    [InlineData("SG0309", "struct MyType\n{\n    property float3 prop { get { return float3(1, 2, 3); } }\n}\nvoid test()\n{\n    MyType t;\n    t.prop.x += 1.0;\n}\n")]
    // ByteAddressBuffer の揃えと Load の offset の、エントリポイントの仮引数から作った値
    [InlineData("SG0352", "RWByteAddressBuffer buf;\n[shader(\"compute\")]\n[numthreads(1, 1, 1)]\nvoid computeMain(uint3 tid: SV_DispatchThreadID)\n{\n    uint a = tid.x;\n    float v = buf.LoadAligned<float>(0, a);\n}\n")]
    [InlineData("SG0352", "Texture2D<float4> t;\nRWStructuredBuffer<float4> o;\n[shader(\"compute\")]\n[numthreads(1, 1, 1)]\nvoid computeMain(uint3 tid: SV_DispatchThreadID)\n{\n    o[0] = t.Load(int3(0, 0, 0), int2(tid.xy));\n}\n")]
    // register の書き方、sizeof の並べ方の型、bit_cast の大きさ、スレッドの番号の型、ステージの名前、エントリポイントの throws
    [InlineData("SG0103", "TextureCube b : register(t);\n")]
    [InlineData("SG0103", "SamplerState c : register(s0, s1);\n")]
    [InlineData("SG0103", "SamplerState d : register(s2, space);\n")]
    [InlineData("SG0301", "int f<L>()\n{\n    return sizeof(float3, L) + sizeof(float3, 14);\n}\n")]
    [InlineData("SG0301", "StructuredBuffer<uint64_t> i;\nRWStructuredBuffer<uint32_t> o;\n[shader(\"compute\")]\n[numthreads(4, 1, 1)]\nvoid computeMain() { o[0] = bit_cast<uint32_t, uint64_t>(i[0]); }\n")]
    [InlineData("SG0407", "[numthreads(4, 1, 1)]\nvoid computeMain(float3 tid : SV_DispatchThreadID) { }\n")]
    [InlineData("SG0304", "[shader(\"invalid\")]\nvoid main() { }\n")]
    [InlineData("SG0345", "uint g(uint n) throws uint\n{\n    if (n < 1)\n        throw 0x1U;\n    return n;\n}\nRWStructuredBuffer<uint> o;\n[shader(\"compute\")]\n[numthreads(1, 1, 1)]\nvoid computeMain(uint3 tid : SV_DispatchThreadID) throws uint\n{\n    o[tid.x] = try g(tid.x);\n}\n")]
    [InlineData("SG0321", "uniform uint U;\nint f(uint x)\n{\n    switch (x)\n    {\n    case U: return 0;\n    }\n    return 1;\n}\n")]
    // 型の適合の表: 標準ライブラリのインターフェイスと組み込みの型、どの型にも当たる extension、外の関数の型の仮引数、既定の型引数、型引数を書いた別名
    [InlineData("SG0349", "void f<T : IFloat>(T x) { }\nvoid g(int v) { f<int>(v); }\n")]
    [InlineData("SG0349", "void f<T : IFloat>(T x) { }\nvoid g(int3 v) { f<int3>(v); }\n")]
    [InlineData("SG0349", "void f<T : IRangedValue>(T x) { }\nvoid g(float3 v) { f<float3>(v); }\n")]
    [InlineData("SG0349", "interface IBar { }\ninterface IFoo { }\nextension<T : IBar> T : IFoo { }\nvoid f<T : IFoo>(T x) { }\nstruct D { int v; }\nvoid g(D d) { f(d); }\n")]
    [InlineData("SG0349", "interface IThing { void thing(); }\nvoid g<T>(T t) where T : IThing { }\nvoid f<T>(T t) where optional T : IThing\n{\n    g<T>(t);\n}\n")]
    [InlineData("SG0349", "interface ITest { int doThing(int x); }\nstruct Impl2 { int v; }\n__generic<T : ITest = Impl2>\nstruct GenStruct { T obj; }\nint test(GenStruct gs) { return 0; }\n")]
    [InlineData("SG0349", "interface IFoo { }\nstruct NotIFoo { }\nvoid invalidDefault<T : IFoo = NotIFoo>() { }\nvoid other() { invalidDefault(); }\n")]
    [InlineData("SG0349", "interface IValueProvider { int getValue(); }\nstruct Wrapper<T : IValueProvider> { T provider; }\ntypealias WrappedProvider<T : IValueProvider> = Wrapper<T>;\nstruct NotAProvider { int data; }\nvoid test() { WrappedProvider<NotAProvider> w; }\n")]
    // 実引数から決まらない型の仮引数と値の仮引数、制約の無い型の仮引数の extension
    [InlineData("SG0363", "T pick<T>() { T v; return v; }\nvoid main() { pick(); }\n")]
    [InlineData("SG0363", "void h<let N : int>(int x) { }\nvoid main() { h(3); }\n")]
    [InlineData("SG0358", "interface IMy { }\nextension<T> T : IMy { }\n")]
    // Slang の unsigned と signed はふつうの名前 (修飾子でない)
    [InlineData("CF0001", "void f()\n{\n    unsigned int x = 1;\n}\n")]
    // 継承したインターフェイスの要件、制約の合わない型への extension のメソッド、型の仮引数に当たる型の食い違い、ジェネリックの多重定義のどれにも合わない呼び出し
    [InlineData("SG0359", "interface IA { int a(); }\ninterface IB : IA { int b(); }\nstruct S : IB { int b() { return 1; } }\n")]
    [InlineData("SG0359", "interface IA { int a(); }\ninterface IM { }\nextension<T : IM> T { int a() { return 0; } }\nstruct S : IA { int v; }\n")]
    [InlineData("SG0363", "struct A { int v; }\nstruct B { int v; }\nvoid foo<T>(T a, T b) { }\nvoid m(A a, B b) { foo(a, b); }\n")]
    [InlineData("SG0363", "void bar<let N : int>(int a[N], int b[N]) { }\nvoid m() { int x[3]; int y[4]; bar(x, y); }\n")]
    [InlineData("CF0401", "interface IA {}\ninterface IB {}\nstruct W { int v; }\nT h<T : IA>(T x) { return x; }\nT h<T : IB>(T x) { return x; }\nvoid useit(W w) { h(w); }\n")]
    [InlineData("CF0401", "struct A { int v; }\nstruct B { int v; }\nstruct C { int v; }\nT pick<T>(T l, T r) where T == int { return l; }\nA pick(A l, A r) { return l; }\nB pick(B l, B r) { return l; }\nvoid useit(C c) { pick(c, c); }\n")]
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
    [InlineData("interface ICounter { int add(int x); }\nextension<T : ICounter> T { int increment() { return add(1); } }\nstruct C : ICounter { int s; int add(int x) { return s + x; } }\nint f(C c) { return c.increment(); }\n")]
    [InlineData("struct P { float _v; property float v { get { return _v; } } }\nfloat f(P p) { return p.v; }\n")]
    // 64 ビットに広がる整数のリテラル
    [InlineData("void f()\n{\n    uint64_t a = 0x9E3779B97F4A7C15ull;\n    int64_t b = 0x1122334455667788;\n}\n")]
    // 循環しない適合: ポインターのフィールド、ほかのインターフェイスだけを持つ構造体
    [InlineData("interface INode { float get(); }\ninterface ILeaf { float v(); }\nstruct Node : INode { INode* next; ILeaf leaf; float get() { return 0; } }\nstruct Leaf : ILeaf { float x; float v() { return x; } }\n")]
    // 形のそろった演算子: スカラーと 1 成分の値はどの形とも組める、import glsl の行列とベクトルの積
    [InlineData("float3 f(float3 a, float4 b, float1 one, float2x2 m)\n{\n    float3 c = a + b.xyz + one + 2.0;\n    c *= 0.5;\n    float2x2 n = m * m + 1.0;\n    return c + n[0].x;\n}\n")]
    [InlineData("import glsl;\nfloat2 f(float2 v, float2x2 m)\n{\n    return m * v;\n}\n")]
    // extension とインターフェイスに書けるもの: static のメンバー・typedef・メソッド。局所変数は参照にできる
    [InlineData("struct T { int a; }\nextension T\n{\n    static int counter;\n    typedef float Real;\n    int get() { return a; }\n}\ninterface I\n{\n    static const int N;\n    float get();\n}\nvoid g(float x)\n{\n    Ref<float> r = x;\n}\n")]
    // 正しい可視性:public の型を使う public の宣言、internal のフィールド、public の構造体の private のメンバー
    [InlineData("struct IS { int a; }\npublic struct PS { public int b; }\npublic struct Q { IS s; private int c; public PS p; }\npublic PS make() { return {}; }\ninternal IS hidden() { return {}; }\n")]
    // 構造体の演算子: 利用者が定義したもの、IArithmetic などに適合する型 (標準ライブラリの演算子)
    [InlineData("struct T { int v; }\nT operator+(T a, T b) { return a; }\nstruct N : IComparable\n{\n    int v;\n    bool equals(N o) { return v == o.v; }\n    bool lessThan(N o) { return v < o.v; }\n    bool lessThanOrEquals(N o) { return v <= o.v; }\n}\nbool f(T t, N n)\n{\n    let u = t + t;\n    return n == n;\n}\n")]
    // ?: の枝: 列挙型と基になる型・int、同じ型、スカラーとベクトル
    [InlineData("enum E { A, B }\nenum F : uint { X }\nuint f(bool c, int i, uint u)\n{\n    let a = c ? E::A : i;\n    let b = c ? F::X : u;\n    let d = c ? F::X : 0;\n    let e = c ? E::A : E::B;\n    let g = c ? float3(1) : 2.0;\n    return b;\n}\n")]
    // ビット演算とシフト: 整数と bool は使える。複合代入の右は左の型へ変換される
    [InlineData("int f(bool a, bool b, int i, uint u, float x)\n{\n    bool r1 = a | b;\n    bool r2 = ~a;\n    int r3 = i | u;\n    uint r4 = u;\n    r4 |= x;\n    return r3 << 2;\n}\n")]
    // 書き換えてよいもの:[mutating] と [[mutating]]、__init、static、仮引数、資源の要素、ポインターの先
    [InlineData("struct S\n{\n    float r;\n    static int n;\n    RWStructuredBuffer<float> buf;\n    float* p;\n    [mutating] void a(float x) { r = x; }\n    [[mutating]] void b(float x) { r = x; }\n    __init() { r = 0; }\n    void c(float r) { r = 1; }\n    void d(float x) { buf[0] = x; *p = x; n = 1; }\n    static void e(S s) { s.r = 1; }\n}\n")]
    // __return_val に書き込んで返す関数、expand の実引数、関数の中の構造体のメソッドの return、__requireCapability の能力の名前
    [InlineData("#lang 2026\n[__NonCopyableType]\nstruct M { float x; __init() { x = 1.0; } }\nM make(float y)\n{\n    __return_val = M();\n    __return_val.x += y;\n}\nint f(int a, float b) { return a; }\nint g()\n{\n    Tuple<expand each Tuple<int, float>> x = (2, 3.0f);\n    return f(expand each x);\n}\nvoid h()\n{\n    __requireCapability(shadermemorycontrol);\n    struct R\n    {\n        int tag() { return 7; }\n    };\n}\n")]
    // 正しいリテラル: エスケープ、16 進の浮動小数、64 ビットに収まる整数、import のパスの \
    [InlineData("uint f()\n{\n    uint a = '\\n' + '\\0' + '\\101' + '\\x41' + '\\x{41}' + '\\u0041' + '\\u{41}' + '\\U00000041' + '\\'';\n    double d = 0x1.8p3;\n    uint64_t u = 18446744073709551615;\n    return a;\n}\n")]
    // 修飾した名前の extension (extension Container.Nested) が足すメンバー
    [InlineData("struct Container\n{\n    struct Nested { int v; }\n}\nextension Container.Nested\n{\n    int twice() { return v * 2; }\n}\nint f(Container.Nested n)\n{\n    return n.twice();\n}\n")]
    // 書ける資源と、読むだけの資源から読むこと
    [InlineData("StructuredBuffer<uint> sb;\nRWStructuredBuffer<uint> rw;\nRWTexture2D<float4> img;\nvoid g(inout uint x) { x += 1; }\nvoid f(uint i)\n{\n    rw[i] = sb[i];\n    InterlockedAdd(rw[0], 1);\n    g(rw[1]);\n    img[uint2(0, 0)] = float4(0);\n    uint v = sb[0];\n    g(v);\n}\n")]
    // 条件にできる値:スカラー (int を含む)。?: と ! のベクトルは成分ごと。列挙子は基になる型へ変換できる
    [InlineData("enum Color { Red, Green }\nfloat4 f(int i, bool4 mask)\n{\n    int c = Color.Green;\n    if (i) { }\n    bool4 n = !mask;\n    return n ? float4(1.0) : float4(0.0);\n}\n")]
    // テクスチャの要素にできる型: bool でないスカラーとベクトル。StructuredBuffer は構造体と行列を入れられる
    [InlineData("struct S { float a; }\nTexture2D<half2> t1;\nRWTexture2D<uint4> t2;\nBuffer<int> t3;\nTexture3D<vector<float, 4>> t4;\nStructuredBuffer<S> s1;\nStructuredBuffer<float4x4> s2;\n")]
    // out に渡せる値: 同じ型、同じ大きさの int と uint
    [InlineData("void oi(out int x) { x = 0; }\nvoid ou(out uint x) { x = 0; }\nvoid of(out float x) { x = 0; }\nvoid g(int i, uint u, float f)\n{\n    oi(u);\n    ou(i);\n    of(f);\n}\n")]
    // 通る変換: スカラーからの複製、1 成分の値、ConstantBuffer の行列の行、import glsl の == (1 つの bool)
    [InlineData("ConstantBuffer<float4x4> m;\nfloat4 f(float1 a, float b)\n{\n    float c = a;\n    float4 v = b;\n    float4 row = m[3];\n    return v + row + c;\n}\n")]
    [InlineData("import glsl;\nbool f(float4 a, float4 b)\n{\n    bool same = a == b;\n    return same;\n}\n")]
    // 型の名前: ジェネリックの仮引数、組み立てる標準の型、associatedtype、typealias、type_param、文の頭の try
    [InlineData("type_param TParam : IDifferentiable;\nRWTexture1D<uint> t1;\nDepthTexture2D depth;\nRasterizerOrderedByteAddressBuffer rov;\ntypealias Real = float;\nT pick<T>(T a, Real b) { return a; }\nstruct P : IDifferentiable { float x; Differential get() { Differential d = {0.0}; return d; } }\nint thrower() throws int { return 1; }\nint caller() throws int\n{\n    try thrower();\n    return 0;\n}\n")]
    // 初期値の中の同じ名前は外側の宣言、波括弧の無い if の宣言は後ろでも見える
    [InlineData("static int e = 1;\nint f(int p)\n{\n    int e = e;\n    int q = p, r = q;\n    if (p > 0) int j = 1;\n    return e + r + j;\n}\n")]
    // 書けるもの: static const、特殊化定数、static groupshared、合うレジスタ、型の static のメソッド、特殊化定数から求める static const
    [InlineData("static const float k = 1.0;\n[vk::constant_id(0)]\nconst int N = 4;\n[[SpecializationConstant]] const float NEAR = 0.1;\nstatic const int M = N * 2;\nstatic const float pi = float.getPi();\nTexture2D tex : register(t0);\nRWStructuredBuffer<float> buf : register(u0);\nSamplerState samp : register(s0);\nstruct P { float x; }\nP items[4];\nvoid f()\n{\n    static groupshared int shared[4];\n}\n")]
    // まだ取り除かれていない版、一部の多重定義だけが取り除かれた関数
    [InlineData("[RemovedSince(2030, \"later\")]\nstruct Later { int x; }\n[RemovedSince(-1, \"one overload\")]\nint g(float x) { return 0; }\nint g(int x) { return 1; }\nint f() { Later l; return g(1); }\n")]
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
    // 括弧で包んだ条件の代入、?: の条件の代入、ループの中の switch の break と continue、値の違う case
    [InlineData("enum class E { A, B }\nvoid f(int a, E e)\n{\n    if ((a = 0)) { }\n    bool c = (a = 2) ? true : false;\n    for (int i = 0; i < 2; i++)\n    {\n        switch (e) { case E.A: break; case E::B: continue; default: break; }\n    }\n}\n")]
    // 最後のフィールドの大きさを決めない配列、関数の外の大きさを決めない配列、初期値のある局所の配列、bool の列挙型、uint8_t のビットフィールド、
    // 多重定義、ジェネリックの制約だけが違う関数、extern の static const、インターフェイスの static const
    [InlineData("struct V { int n; int a[]; }\nTexture2D textures[];\nenum Flag : bool { No, Yes }\nstruct B { uint8_t a : 4; uint8_t b : 4; }\nint bar(int x) { return 1; }\nfloat bar(float x) { return 1; }\ninterface IA { static const int N; }\ninterface IB { int b(); }\nint g<T>(T x) where T : IA { return 1; }\nint g<T>(T x) where T : IB { return 2; }\nextern static const int count;\nvoid f()\n{\n    int a[] = { 1, 2 };\n}\n")]
    // 4 から 128 までの 2 の累乗の WaveSize、- で区切らない GUID
    [InlineData("[numthreads(64, 1, 1)]\n[WaveSize(32)]\nvoid main() { }\n[COM(\"00000000000000000000000000000001\")]\ninterface IC { void f(); }\n")]
    // catch で受ける throw と try、throws の関数の throw、仮引数のある extension の __init、dyn のインターフェイスと仮引数、static groupshared のフィールド、
    // new で作る class と同じ class の値の型変換、set(v)、override の付いたメソッド
    [InlineData("enum MyError { Fail }\nint g() throws MyError { throw MyError.Fail; }\nvoid f()\n{\n    do\n    {\n        let n = try g();\n    }\n    catch (e : MyError)\n    {\n    }\n}\nstruct S { int x; }\nextension S { __init(int v) { x = v; } }\ndyn interface IBar { int b(); }\nint callBar(dyn IBar x) { return x.b(); }\nstruct Ty<T> { static groupshared T y[2]; }\nclass Counter { int v; __init(int s) { v = s; } }\nvoid h(Counter one)\n{\n    Counter c = new Counter(1);\n    Counter d = (Counter)one;\n}\nstruct P { int _v; property v : int { get { return _v; } set(newValue) { _v = newValue; } } }\ninterface IFoo { int getVal() { return 0; } }\nstruct Impl : IFoo { override int getVal() { return 1; } }\n")]
    // 呼び出しの operator() は型の中に書ける。演算子は関数の外に書く
    [InlineData("struct S\n{\n    int x;\n    int operator()(int a) { return a + x; }\n}\nS operator-(S a, S b) { return a; }\nM operator?:(bool c, M a, M b) { return c ? a : b; }\nstruct M { int v; }\n")]
    // 同じ型引数のジェネリックの型、受け取れる多重定義のある構造体の値
    [InlineData("struct G<T> { T v; }\nstruct Test {}\nfloat pick(float x) { return x; }\nfloat pick(Test t) { return 0; }\nTexture1D<float> tex;\nvoid f(G<int> gi)\n{\n    G<int> g2 = gi;\n    Texture1D<float> t3 = tex;\n    float r = pick(Test()) + pick(1.0);\n}\n")]
    // 宣言した型と標準の型の制約、ジェネリックの仮引数の throws、then の側の if-let の変数
    [InlineData("interface IShape { float area(); }\nenum MyError { Fail }\nstruct Circle : IShape { float r; float area() { return r; } }\nT pick<T : IShape>(T a) { return a; }\nT twice<T : IArithmetic, let N : int>(T x) where T : IComparable { return x; }\nint g() throws MyError { return 1; }\nfloat use(IShape s)\n{\n    if (let c = s as Circle) { return c.area(); }\n    return 0;\n}\n")]
    // インターフェイスの継承、extension での適合、ジェネリックの extension
    [InlineData("interface IFoo { int get(); }\ninterface IBar : IFoo { }\nstruct ViaBar : IBar { int get() { return 2; } }\nstruct Ext { }\nextension Ext : IFoo { int get() { return 3; } }\nextension float : IFoo { int get() { return 4; } }\ninterface IBase {}\nextension<T : IBase> T : IFoo { int get() { return 5; } }\nstruct B : IBase {}\nint f<T : IFoo>(T x) { return x.get(); }\nstruct G<T : IFoo> { T v; }\nvoid main(ViaBar v, Ext e, B b)\n{\n    f(v);\n    f(e);\n    f(b);\n    f(1.0);\n    G<Ext> ge;\n}\n")]
    // 定数の回数のループ、[MaxIters]、break で抜けるループ、同じ型のメソッドの微分の関数、インターフェイスの [MaybeDifferentiable]、no_diff の呼び出しの文
    [InlineData("static const int N = 4;\nvoid trace() { }\n[Differentiable]\nfloat f(float x)\n{\n    float s = 0;\n    for (int i = 0; i < N; i++) { s += x; }\n    for (int j = 5; j > 0; j -= 1) { s += x; }\n    [MaxIters(8)]\n    for (int k = 0; k < (int)x; k++) { s += x; }\n    for (;;) { if (x > 0) break; }\n    no_diff trace();\n    return s;\n}\nstruct S\n{\n    [BackwardDerivative(eval_bwd)]\n    float eval(float x) { return x; }\n    void eval_bwd(inout DifferentialPair<float> x, float d) { }\n}\ninterface IFoo\n{\n    [MaybeDifferentiable]\n    float compute(float x);\n}\n[shader(\"compute\")]\n[DerivativeGroupLinear]\n[numthreads(4, 1, 1)]\nvoid main() { }\n")]
    // 並びの数を受け取る __init、__init の無い基底の継承、資源の型のフィールドを埋めた並び
    [InlineData("struct Two { int x; __init() { x = 0; } __init(int a, int b) { x = a + b; } }\nstruct BaseNo { int a; }\nstruct DerNo : BaseNo { int b; }\nTexture1D<float> tex;\nstruct WithTex { int v; Texture1D<float> t; }\nvoid f()\n{\n    Two t0 = {};\n    Two t2 = {1, 2};\n    DerNo d = {1, 2};\n    WithTex w = {1, tex};\n}\n")]
    // 定数の offset、代入し直さない変数、static const、Gather の定数でない offset
    [InlineData("Texture2D t;\nSamplerState s;\nstatic const int2 kOff = int2(1, 0);\nuniform int2 rtOffset;\nfloat4 f(float2 uv)\n{\n    int ii = 1;\n    return t.Sample(s, uv, int2(0, 0)) + t.Sample(s, uv, kOff) + t.Sample(s, uv, int2(ii, ii)) + t.Gather(s, uv, rtOffset);\n}\n")]
    // 基底の構造体への as、インターフェイスへの is、ジェネリックの値の as
    [InlineData("interface IFoo { int get(); }\nstruct A { int x; }\nstruct B : A { int y; }\nstruct Impl : IFoo { int get() { return 1; } }\nbool f<T>(B b, IFoo i, T v)\n{\n    Optional<A> o = b as A;\n    let d = v as Impl;\n    return i is Impl;\n}\n")]
    // 浮動小数のリテラルの __floatAsInt、2025 の言語のベクトルを含む並び、成分のそろった並び
    [InlineData("static const int k = __floatAsInt(float(1));\nvoid f()\n{\n    int a = __floatAsInt(1.5) + __floatAsInt(-2.0);\n    int4 v = { int2(1, 2), 3 };\n    int4 w = { 1, 2, 3 };\n}\n")]
    // nonempty の並びの __first と、nonempty の並びを渡す呼び出し
    [InlineData("int goodValueUse<let each D : int>() where nonempty(D)\n{\n    return __first(D);\n}\nvoid consumeNonEmpty<let each D : int>() where nonempty(D) {}\nvoid goodForward<let each D : int>() where nonempty(D)\n{\n    consumeNonEmpty<D>();\n}\n")]
    // T* から同じ Ptr への代入、Ptr<…>(p) の変換、読むだけのポインターからの読み出し
    [InlineData("int* processMemory;\nint f(int id)\n{\n    Ptr<int, Access::ReadWrite, AddressSpace::Device> a = processMemory;\n    Ptr<int, Access::Read> r = Ptr<int, Access::Read>(processMemory + id);\n    Ptr<float> b = Ptr<float>(r);\n    return r[0] + *r;\n}\n")]
    // countof(…) の制約と、数の合う呼び出し
    [InlineData("void load<let N : int, each TIndex>(TIndex indices) where countof(TIndex) == N { }\nvoid f()\n{\n    load<3>(1, 2, 3);\n}\n")]
    // 同じ型のポインターとアドレス
    [InlineData("struct Foo { int val; }\nint f()\n{\n    Foo x;\n    Foo* pf = &x;\n    Foo* copy = pf;\n    return copy->val;\n}\n")]
    // 型の等しさの制約と、組み込みの型の制約のある型の仮引数
    [InlineData("int eq<T>(T t) where T == int\n{\n    int x = t;\n    return x;\n}\nint en<T : __BuiltinArithmeticType>(T t)\n{\n    T y = t;\n    return 0;\n}\n")]
    // ラムダ式の中の局所変数と仮引数への書き込み、取り込んだ値の読み出し
    [InlineData("void test()\n{\n    int c = 0;\n    let lam = (int a) => {\n        int d = a + c;\n        d = d + 1;\n        a = d;\n        return d;\n    };\n}\n")]
    // 配列を受け取る __init への並び、public のフィールドの並び
    [InlineData("struct G\n{\n    float[4] data;\n    __init(float[4] d) { data = d; }\n}\npublic struct P\n{\n    public int a;\n    public int b;\n}\nvoid f(float x)\n{\n    G g = G({x, x, x, x});\n    P p = {1, 2};\n}\n")]
    // 組み込みの演算子の結果は捨ててよい
    [InlineData("[NoDiscard]\nint compute() { return 1; }\nstruct W { int v; }\n[NoDiscard]\nW operator+(W a, W b) { return a; }\nvoid f(int c)\n{\n    compute() + c;\n}\n")]
    // 局所変数と [mutating] のメソッドの中からの呼び出し
    [InlineData("struct Counter\n{\n    int count;\n    [mutating] void increment() { count++; }\n    [mutating] void twice() { increment(); increment(); }\n}\nvoid main()\n{\n    Counter c;\n    c.increment();\n}\n")]
    // 大きさを書いた配列の countof、static const の値、each の型の仮引数
    [InlineData("int arr[4];\nstatic const int k = 2;\nint count<each T>(expand each T values) { return countof(T); }\nint f()\n{\n    return countof(arr) + k;\n}\n")]
    // 値の仮引数、インターフェイスの制約、前の仮引数を指す制約、制約を通して決まる extension の型の仮引数、既定の型引数
    [InlineData("interface IFloat {}\ninterface IBuf<T> {}\ninterface IFoo<T : IFloat> {}\nvoid test3<T : IFloat, Foo : IFoo<T>>() { }\nvoid sized<let N : int, let B : bool>() { }\nextension<T, U : IBuf<T>> U\n{\n    void bar() { }\n}\nstruct D<T = float> { T v; }\nvoid f(D d, Buffer<float4> b) { }\n")]
    // 要件を満たす型、extension で満たす型、言語のバージョンを書かないファイルの動的なディスパッチ
    [InlineData("interface IFoo { void bar(); static int count(); }\nstruct A : IFoo { void bar() { } static int count() { return 1; } }\nstruct B : IFoo { }\nextension B { void bar() { } static int count() { return 2; } }\nfloat g<T : IFoo>(T obj) { return 0; }\nfloat f(IFoo obj)\n{\n    return g(obj);\n}\n")]
    // 型引数の既定の値と可変長の並び、制約のある is、__subscript の accessor の既定の実装、[COM] を付けた class
    [InlineData("struct Foo<T, U = int> { T t; }\nstruct Bar<each T> { int x; }\nvoid f() { Foo<int> a; Foo<int, float> b; Bar<> c; Bar<int, float> d; }\n")]
    [InlineData("interface I { int g(); }\nstruct C : I { int g() { return 1; } }\nbool f<T : I>(T t) { return t is I; }\nbool g<T>(T t) where optional T : I { return T is I; }\nbool h(C c) { return c is I; }\n")]
    [InlineData("interface I\n{\n    int load(int i);\n    __subscript(int i) -> int { get { return load(i); } set { } }\n}\n")]
    [InlineData("[COM(\"00000000-0000-0000-0000-000000000001\")]\ninterface ICOMService { float query(); }\nclass Impl : ICOMService { float query() { return 1; } }\n")]
    // accessor の属性と修飾子、キーワードの名前のジェネリックの仮引数 (slangc は警告だけにする)
    [InlineData("struct S\n{\n    int v;\n    property int value\n    {\n        [mutating] set { v = newValue; }\n        get { return v; }\n    }\n}\nint useGenericTypeParam<struct>() { return 0; }\n")]
    // 名前空間のメンバーと、型引数の同じジェネリックの型のフィールドどうしの代入
    [InlineData("namespace ns { int fb() { return 1; } }\nstruct G<T> { T v; }\nstruct Outer<T> { G<T> g; }\nvoid f(Outer<int> a, Outer<int> b)\n{\n    a.g = b.g;\n    int x = ns.fb();\n}\n")]
    // 形の合う微分の関数、型のメソッドの微分 (自分の DifferentialPair を受け取る)、微分の関数の中の no_diff と no_diff の局所変数
    [InlineData("[ForwardDerivative(fwd_f)]\n[BackwardDerivative(bwd_f)]\nfloat f(float x, int n) { return x * n; }\nDifferentialPair<float> fwd_f(DifferentialPair<float> x, int n) { return diffPair(x.p * n, x.d * n); }\nvoid bwd_f(inout DifferentialPair<float> x, int n, float dOut) { x = diffPair(x.p, dOut * n); }\nstruct A : IDifferentiable\n{\n    float v;\n    [ForwardDerivative(diff_m)]\n    float m(float w) { return w; }\n    static DifferentialPair<float> diff_m(DifferentialPair<A> a, DifferentialPair<float> w) { return w; }\n}\nfloat g(float x) { return x; }\n[ForwardDerivativeOf(g)]\nDifferentialPair<float> fwd_g(DifferentialPair<float> x) { float s = no_diff g(x.p); return diffPair(s, x.d); }\nvoid k() { no_diff float2 pos = float2(1, 2); }\n")]
    // [DerivativeMember] で当てたフィールド、no_diff のフィールドと微分しない型のフィールド
    [InlineData("struct MyDiff : IDifferentiable\n{\n    typealias Differential = MyDiff;\n    float x;\n    float dy;\n}\nstruct MyType : IDifferentiable\n{\n    typealias Differential = MyDiff;\n    [DerivativeMember(MyDiff.x)]\n    float x;\n    [DerivativeMember(MyDiff.dy)]\n    float y;\n    no_diff float z;\n    int count;\n}\n")]
    // 微分しない関数の中の後ろ向きの微分と var の DifferentialPair、no_diff の __constref
    [InlineData("[Differentiable]\nfloat square(float x) { return x * x; }\nfloat h(float v)\n{\n    var x = diffPair(v, 1.0);\n    bwd_diff(square)(x, 1.0);\n    return x.d;\n}\n[Differentiable]\nfloat g(float x, __constref no_diff float y) { return x + y; }\n")]
    // ポインターで自分を指す構造体、制約のあるジェネリックの型の演算子、set のある property への書き込み
    [InlineData("struct Node { Node* next; int y; }\nT add<T : IArithmetic>(T a, T b) { return a + b; }\nstruct MyType\n{\n    float3 v;\n    property float3 prop { get { return v; } set { v = newValue; } }\n}\nvoid test()\n{\n    MyType t;\n    t.prop = float3(1, 2, 3);\n}\n")]
    // 定数の揃え、extension で足した多重定義のメソッドの呼び出し
    [InlineData("RWByteAddressBuffer buf;\n[shader(\"compute\")]\n[numthreads(1, 1, 1)]\nvoid computeMain(uint3 tid: SV_DispatchThreadID)\n{\n    float v = buf.LoadAligned<float>(0, 16);\n    buf.Store<float>(16, v, 16);\n}\nstruct Test { void m<int u>(float2 x) {} }\nextension Test { void m<int u>() {} }\nvoid f() { Test t; t.m<3>(); }\n")]
    // 正しい register と space、並べ方の型の sizeof、大きさの同じ bit_cast、uint2 のスレッドの番号、能力の名前のステージ
    [InlineData("Texture2D t : register(t0, space1);\nSamplerState s : register(s0);\nint f() { return sizeof(float3, DefaultDataLayout); }\nfloat g(uint x) { return bit_cast<float, uint>(x); }\n[shader(\"compute\")]\n[numthreads(4, 1, 1)]\nvoid computeMain(uint2 tid : SV_DispatchThreadID, uint gi : SV_GroupID) { }\n")]
    // 標準ライブラリの適合 (float3 と IFloat、uint と IInteger、float4x4 と IFloat)、どの型にも当たる extension をたどる適合、列挙型と __EnumType
    [InlineData("void f<T : IFloat>(T x) { }\nvoid i<T : IInteger>(T x) { }\nvoid g(float3 a, uint b, float4x4 c) { f<float3>(a); i<uint>(b); f<float4x4>(c); }\n")]
    [InlineData("interface IA { }\ninterface IB { }\ninterface IC { }\nextension<T : IA> T : IB { }\nextension<T : IB> T : IC { }\nextension<T : IFloat> T : IA { }\nvoid f<T : IC>(T x) { }\nstruct S : IA { int v; }\nvoid g(S s) { f(s); f(1.0f); }\n")]
    [InlineData("enum class Hints : uint { a = 1, b = 2 }\nbool is_set<T : __EnumType>(T value, T flag) { return true; }\nbool use(Hints h) { return is_set(h, Hints.b); }\n")]
    // 既定の値か実引数から決まる型の仮引数、配列の大きさから決まる値の仮引数、型を前に書いた値の仮引数
    [InlineData("T pick<T = float>() { T v; return v; }\nvoid sum<let N : int>(int a[N]) { }\nvoid process<int N>(float v[N]) { }\nvoid main() { float x = pick(); int a[3]; sum(a); float b[2]; process(b); }\n")]
    // Slang の unsigned と signed はふつうの名前で、型の名前にできる
    [InlineData("struct unsigned {}\nstruct signed {}\nunsigned int;\nsigned value;\n")]
    // 継承したインターフェイスの既定の実装、制約に合う型への extension のメソッド、組み込みの数の型どうしの型の仮引数、制約に合うジェネリックの多重定義
    [InlineData("interface IA { int a() { return 0; } }\ninterface IB : IA { int b(); }\nstruct S : IB { int b() { return 1; } }\ninterface IC { int c(); }\ninterface IM { }\nextension<T : IM> T { int c() { return 0; } }\nstruct U : IC, IM { int v; }\n")]
    [InlineData("void foo<T>(T a, T b) { }\nstruct A { int v; }\nstruct B : A { int w; }\nT h<T : IFloat>(T x) { return x; }\nint h(int a, int b) { return a; }\nvoid m(int i, float f, A a) { foo(i, f); h(1.0f); h(1, 2); foo(a, a); }\n")]
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
    [InlineData(null, false)]
    [InlineData("spirv", true)]
    [InlineData("cpp", false)]
    public async Task 局所変数のgetAddressは設定ファイルでGPUのコンパイル先にしたときだけ報告する(string? target, bool reported)
    {
        if (target is not null)
        {
            File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), $"slang:\n  target: {target}\n");
        }

        File.WriteAllText(Path.Combine(_workDirectory, "a.slang"), "void sink(int* p) { }\nvoid f(int param)\n{\n    int* a = __getAddress(param);\n    sink(a);\n}\n");
        StringWriter output = new();
        StringWriter error = new();

        await Program.RunAsync([Path.Combine(_workDirectory, "a.slang")], BuiltInAnalyzers.All, output, error);

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Equal(reported, output.ToString().Contains("error SG0356", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 別のモジュールのpublicでない__initの呼び出しを報告する()
    {
        File.WriteAllText(Path.Combine(_workDirectory, "helper.slang"), "module helper;\npublic struct B { __init(float b) {} }\npublic struct D { public __init(float d) {} }\n");

        string output = await AnalyzeAsync("import helper;\nvoid f()\n{\n    B bar = B(0.0);\n    D ok = D(1.0);\n}\n", "user.slang");

        Assert.Contains("user.slang(4:13): error SG0401", output, StringComparison.Ordinal);
        Assert.Equal(1, output.Split("SG0401").Length - 1);
    }

    [Fact]
    public async Task 構文の誤りのあるモジュールのimportを報告する()
    {
        // たどって取り込んだモジュール (c) の誤りも、解析するファイルの import の行に伝える。
        File.WriteAllText(Path.Combine(_workDirectory, "b.slang"), "import c;\n");
        File.WriteAllText(Path.Combine(_workDirectory, "c.slang"), "int a(;\n");

        string output = await AnalyzeAsync("import b;\nint main() { return 0; }\n", "user.slang");

        Assert.Contains("user.slang(1:1): error SG0353", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 分けて書いたモジュールのpublicでない宣言の使用を報告する()
    {
        // module の宣言のファイルと、__include で足した implementing のファイル。どちらの宣言もそのモジュールのものである。
        File.WriteAllText(Path.Combine(_workDirectory, "that.slang"), "module that;\n__include \"that-impl\";\nvoid internalMethod();\npublic void publicMethod();\nstruct InternalStruct { int x; }\n");
        File.WriteAllText(Path.Combine(_workDirectory, "that-impl.slang"), "implementing that;\npublic struct PublicStruct { int x; public int y; }\n");

        string output = await AnalyzeAsync(
            "import that;\nvoid test()\n{\n    PublicStruct s;\n    s.x = 1;\n    s.y = 2;\n    InternalStruct t;\n    internalMethod();\n    publicMethod();\n}\n", "user.slang");

        Assert.Contains("user.slang(5:7): error SG0401: 'x'", output, StringComparison.Ordinal);
        Assert.Contains("user.slang(7:5): error SG0401: 'InternalStruct'", output, StringComparison.Ordinal);
        Assert.Contains("user.slang(8:5): error SG0401: 'internalMethod'", output, StringComparison.Ordinal);
        Assert.Equal(3, output.Split("SG0401").Length - 1);
    }

    [Fact]
    public async Task importにexportは書けない()
    {
        File.WriteAllText(Path.Combine(_workDirectory, "leaf.slang"), "module leaf;\npublic int leafHelper(int x) { return x; }\n");

        string output = await AnalyzeAsync("module user;\nexport import leaf;\n", "user.slang");

        Assert.Contains("user.slang(2:1): error SG0102", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 利用者のexperimentalのフォルダーのモジュールは実験中のモジュールにしない()
    {
        Directory.CreateDirectory(Path.Combine(_workDirectory, "experimental"));
        File.WriteAllText(Path.Combine(_workDirectory, "experimental", "tools.slang"), "module tools;\npublic int helper(int x) { return x; }\n");

        string output = await AnalyzeAsync("import experimental.tools;\nint f() { return helper(1); }\n", "user.slang");

        Assert.DoesNotContain("SG0362", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("metal", false)]
    [InlineData("cpp", false)]
    [InlineData("spirv", true)]
    public async Task 資源を含む構造体のStructuredBufferはMetalとCPUで通る(string target, bool reported)
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), $"slang:\n  target: {target}\n");
        File.WriteAllText(Path.Combine(_workDirectory, "a.slang"), "struct M { Texture2D t; float v; }\nStructuredBuffer<M> b;\n");
        StringWriter output = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.slang")], BuiltInAnalyzers.All, output, new StringWriter());

        Assert.Equal(reported, output.ToString().Contains("error SG0404", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cuda", false)]
    [InlineData("cpp", true)]
    public async Task numthreadsだけのジェネリックの関数はCUDAでだけエントリポイントにならない(string target, bool reported)
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), $"slang:\n  target: {target}\n");
        File.WriteAllText(Path.Combine(_workDirectory, "a.slang"), "interface IM { float f(); }\n[numthreads(1, 1, 1)]\nvoid computeMain<M : IM>(uint3 id : SV_DispatchThreadID) { }\n");
        StringWriter output = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.slang")], BuiltInAnalyzers.All, output, new StringWriter());

        Assert.Equal(reported, output.ToString().Contains("error SG0358", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 値を書かないdefineは空のマクロにする()
    {
        // slangc の -DFOO と同じく空のマクロ。#if FOO == 1 は前処理の式の誤り (HLSL の 1 ではない)。
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "defines:\n  - FOO\n");
        File.WriteAllText(Path.Combine(_workDirectory, "a.slang"), "#if FOO == 1\nint isOne;\n#endif\n");
        StringWriter output = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.slang")], BuiltInAnalyzers.All, output, new StringWriter());

        Assert.Contains("error CF0002", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 構文の誤りを含む関数の外では宣言の検査を続ける()
    {
        // f の構文の誤りは f の中だけ。g の宣言されていない名前は報告し、h が後ろの k を使うのは通す。
        string output = await AnalyzeAsync(
            "float f(float x)\n{\n    return x +\n}\n\nfloat g(float y)\n{\n    return undefinedName * y;\n}\n\nfloat h(float z)\n{\n    return g(z) + k(z);\n}\n\nfloat k(float w) { return w; }\n", "broken.slang");

        Assert.Contains("broken.slang(4:1): error CF0001", output, StringComparison.Ordinal);
        Assert.Contains("broken.slang(8:12): error CF0310: 'undefinedName'", output, StringComparison.Ordinal);
        Assert.Equal(1, output.Split("error CF0310").Length - 1);
    }

    [Theory]
    // } が足りずに後ろの関数を本体に飲み込んだもの、関数の外の構文の誤り、誤りを含む文の中 (vArray[i] var c = 3; を宣言と読み違える)
    [InlineData("float f(float x)\n{\n    return x + 1;\n\nfloat g(float y)\n{\n    return undefinedName * y;\n}\n")]
    [InlineData("struct S { int a }\nfloat g(float y) { return undefinedName * y; }\n")]
    [InlineData("void m()\n{\n    float3 vArray[3];\n    let i = 1;\n    vArray[i]\n    var c = 3;\n}\n")]
    public async Task 構文の誤りで宣言が読めていない所では宣言の検査をしない(string code)
        => Assert.DoesNotContain("error CF0310", await AnalyzeAsync(code, "broken.slang"), StringComparison.Ordinal);

    [Fact]
    public async Task ファイルの終わりで足りないものはファイルの終わりの位置で伝える()
    {
        // slangc と同じく、最後の改行の後ろ (4 行目) で伝える。
        string output = await AnalyzeAsync("int foo()\n{\n    int a = 33\n", "eof.slang");

        Assert.Contains("eof.slang(4:1): error CF0001", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取り込んだモジュールの取り込みと名前空間の宣言は見えない()
    {
        // import はたどらない (__exported import はたどる)。取り込んだモジュールの using namespace は効かない。名前空間の中の using は、その名前空間を using すれば効く。
        File.WriteAllText(Path.Combine(_workDirectory, "leaf.slang"), "module leaf;\npublic int leafHelper(int x) { return x; }\n");
        File.WriteAllText(Path.Combine(_workDirectory, "shared.slang"), "module shared;\npublic int sharedHelper(int x) { return x; }\n");
        File.WriteAllText(Path.Combine(_workDirectory, "mid.slang"),
            "module mid;\nimport leaf;\n__exported import shared;\nnamespace Foo { public int fooHelper(int x) { return x; } }\nnamespace Bar { public int barHelper(int x) { return x; } }\nnamespace Outer { using Bar; }\nusing namespace Foo;\n");

        string output = await AnalyzeAsync(
            "module user;\nimport mid;\nusing Outer;\nint test()\n{\n    return leafHelper(1) + sharedHelper(2) + fooHelper(3) + Foo.fooHelper(4) + barHelper(5);\n}\n", "user.slang");

        Assert.Contains("user.slang(6:12): error CF0310: 'leafHelper'", output, StringComparison.Ordinal);
        Assert.Contains("user.slang(6:46): error CF0310: 'fooHelper'", output, StringComparison.Ordinal);
        Assert.Equal(2, output.Split("CF0310").Length - 1);
    }

    [Fact]
    public async Task 別のモジュールのpublicでない中の型と名前空間のメンバーの使用を報告する()
    {
        // 2026 の言語の public の構造体の中の型は、書かなければ public。internal と private を書いたものは使えない。public の名前空間のメンバーは、書かなければ internal。
        File.WriteAllText(Path.Combine(_workDirectory, "outer.slang"),
            "#language slang 2026\nmodule outer;\npublic struct PublicOuter\n{\n    struct Inner { int a; }\n    internal struct InternalInner { int b; }\n    internal enum Hidden { A, B }\n}\npublic namespace Space\n{\n    static int internalVar;\n    public static int publicVar;\n}\n");

        string output = await AnalyzeAsync(
            "import outer;\nvoid test(PublicOuter.Inner inner)\n{\n    PublicOuter.InternalInner ii;\n    PublicOuter.Hidden h = PublicOuter.Hidden.A;\n    Space.internalVar = 1;\n    Space.publicVar = 2;\n}\n", "user.slang");

        Assert.Contains("user.slang(4:17): error SG0401: 'InternalInner'", output, StringComparison.Ordinal);
        Assert.Contains("user.slang(5:17): error SG0401: 'Hidden'", output, StringComparison.Ordinal);
        Assert.Contains("user.slang(5:40): error SG0401: 'Hidden'", output, StringComparison.Ordinal);
        Assert.Contains("user.slang(6:11): error SG0401: 'internalVar'", output, StringComparison.Ordinal);
        Assert.Equal(4, output.Split("SG0401").Length - 1);
    }

    [Fact]
    public async Task 別のモジュールのpublicでない宣言の使用を報告する()
    {
        // module の宣言のあるモジュール: 書かなければ internal。2025 の言語では public の構造体のメンバーも internal が既定。
        File.WriteAllText(Path.Combine(_workDirectory, "lib.slang"), "module lib;\npublic struct P { int x; public int y; }\nstruct Q { int z; }\nint helper() { return 1; }\npublic int pub() { return 2; }\n");

        // module の宣言も可視性も無いファイルは、古い書き方のモジュールとして、すべてが public。
        File.WriteAllText(Path.Combine(_workDirectory, "legacy.slang"), "struct L { int w; }\nint old() { return 3; }\n");

        // 2026 の言語のモジュールは、public の構造体のメンバーが public が既定。
        File.WriteAllText(Path.Combine(_workDirectory, "modern.slang"), "#language slang 2026\nmodule modern;\npublic struct M { int v; }\n");

        string output = await AnalyzeAsync(
            "import lib;\nimport legacy;\nimport modern;\nint test(P p, L l, M m)\n{\n    Q q;\n    return p.x + p.y + helper() + pub() + l.w + old() + m.v;\n}\n", "main.slang");

        Assert.Contains("main.slang(6:5): error SG0401: 'Q'", output, StringComparison.Ordinal);
        Assert.Contains("main.slang(7:14): error SG0401: 'x'", output, StringComparison.Ordinal);
        Assert.Contains("main.slang(7:24): error SG0401: 'helper'", output, StringComparison.Ordinal);
        Assert.Equal(3, output.Split("SG0401").Length - 1);
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
    public async Task UTF8として読めないバイトを行ごとに報告し本物のU_FFFDは報告しない()
    {
        string path = Path.Combine(_workDirectory, "bytes.slang");

        // 1 行目は Latin-1 の Ö (0xD6)、2 行目は本物の U+FFFD (EF BF BD)。
        File.WriteAllBytes(path, [.. "int a = 1; // "u8, 0xD6, 0x0A, .. "int b = 2; // "u8, 0xEF, 0xBF, 0xBD, 0x0A]);
        StringWriter output = new();
        StringWriter error = new();

        await Program.RunAsync([path, "--no-config"], BuiltInAnalyzers.All, output, error);

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Contains("bytes.slang(1:15): error SG0005", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bytes.slang(2:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 値を返さない経路は警告にする()
        => Assert.Contains("warning CF0501", await AnalyzeAsync("int f(int a)\n{\n    if (a > 0)\n    {\n        return 1;\n    }\n}\n"), StringComparison.Ordinal);

    [Theory]
    [InlineData(null, true)]
    [InlineData("spirv", true)]
    [InlineData("cuda", false)]
    [InlineData("cpp", false)]
    public async Task 資源を含む構造体のStructuredBufferはGPUのコンパイル先でだけ報告する(string? target, bool reported)
    {
        if (target is not null)
        {
            File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), $"slang:\n  target: {target}\n");
        }

        File.WriteAllText(Path.Combine(_workDirectory, "a.slang"), "struct WithTexture { Texture2D t; }\nStructuredBuffer<WithTexture> items;\n");
        StringWriter output = new();
        StringWriter error = new();

        await Program.RunAsync([Path.Combine(_workDirectory, "a.slang")], BuiltInAnalyzers.All, output, error);

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Equal(reported, output.ToString().Contains("error SG0404", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task 修飾しない列挙子は設定でだけ書ける(bool unscoped, bool reported)
    {
        if (unscoped)
        {
            File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "slang:\n  unscoped-enums: true\n");
        }

        // [UnscopedEnum] の列挙型と、名前の無い列挙型と、列挙型の中の前の列挙子は、いつでも修飾せずに書ける。
        File.WriteAllText(Path.Combine(_workDirectory, "a.slang"),
            "enum Fruit { Orange, Apple }\n[UnscopedEnum]\nenum Color { Red, Green = Red }\nenum { Loose }\nint f()\n{\n    return (int)Apple + (int)Red + Loose + (int)Fruit.Orange;\n}\n");
        StringWriter output = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.slang")], BuiltInAnalyzers.All, output, new StringWriter());

        Assert.Equal(reported, output.ToString().Contains("CF0310: 'Apple'", StringComparison.Ordinal));
        Assert.Equal(reported ? 1 : 0, output.ToString().Split("CF0310").Length - 1);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task 実験中のモジュールのimportは設定でだけ通る(bool experimental, bool reported)
    {
        if (experimental)
        {
            File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "slang:\n  experimental-features: true\n");
        }

        File.WriteAllText(Path.Combine(_workDirectory, "a.slang"), "import slang.numerics;\nvoid f() { }\n");
        StringWriter output = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.slang")], BuiltInAnalyzers.All, output, new StringWriter());

        Assert.Equal(reported, output.ToString().Contains("a.slang(1:8): error SG0362", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ジェネリックのエントリポイントは設定でだけ通る(bool allowed, bool reported)
    {
        if (allowed)
        {
            File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "slang:\n  generic-entry-points: true\n");
        }

        File.WriteAllText(Path.Combine(_workDirectory, "a.slang"),
            "RWStructuredBuffer<float> o;\n[shader(\"compute\")]\n[numthreads(1, 1, 1)]\nvoid main<T : IFloat>(uint3 id : SV_DispatchThreadID) { o[0] = 1.0; }\nT twice<T : IFloat>(T x) { return x + x; }\n");
        StringWriter output = new();
        await Program.RunAsync([Path.Combine(_workDirectory, "a.slang")], BuiltInAnalyzers.All, output, new StringWriter());

        Assert.Equal(reported, output.ToString().Contains("a.slang(4:6): error SG0358", StringComparison.Ordinal));
        Assert.Equal(reported ? 1 : 0, output.ToString().Split("SG0358").Length - 1);
    }

    [Fact]
    public async Task 知らないコンパイル先を設定の誤りにする()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "slang:\n  target: vulkan\n");
        File.WriteAllText(Path.Combine(_workDirectory, "a.slang"), "void f() { }\n");
        StringWriter output = new();

        await Program.RunAsync([Path.Combine(_workDirectory, "a.slang")], BuiltInAnalyzers.All, output, new StringWriter());

        Assert.Contains("slang: の target には", output.ToString(), StringComparison.Ordinal);
    }
}
