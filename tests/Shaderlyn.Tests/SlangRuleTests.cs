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
