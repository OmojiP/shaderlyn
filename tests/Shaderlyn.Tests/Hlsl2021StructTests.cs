using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Tests;

/// <summary>
/// HLSL 2021 の構造体 (継承・メソッド・演算子・template の構造体) の検査の検証。
/// </summary>
/// <remarks>
/// どの例も DXC (<c>-HV 2021</c>) で結果を確かめた。報告する場合は、DXC がエラーにする場合だけである。
/// </remarks>
public sealed class Hlsl2021StructTests
{
    /// <summary>構造体の宣言の前置き。</summary>
    private const string Prelude = """
        struct B { float x; float3 n; float Get() { return x; } };
        struct D : B { float y; };
        struct E : D { float z; };
        """;

    [Fact]
    public void 継承した基底の名前を構文木に残す()
    {
        HlslSyntaxTree tree = HlslSyntaxTree.Parse(
            SourceText.From("struct B { float x; }; struct D : B { float y; };", "test.hlsl"),
            new PreprocessorOptions { LanguageVersion = HlslLanguageVersion.Hlsl2021 });

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));
        StructDeclarationSyntax derived = Assert.IsType<StructDeclarationSyntax>(tree.Root.Declarations[1]);
        Assert.Equal("B", derived.BaseTypeName);
        Assert.Null(Assert.IsType<StructDeclarationSyntax>(tree.Root.Declarations[0]).BaseTypeName);
    }

    /// <summary>継承した構造体のメンバーを、DXC と同じに扱うことを検証する (G0)。</summary>
    /// <param name="statement">main の中に書く文。</param>
    /// <param name="expected">報告されるはずの規則。正しいなら <see langword="null"/>。</param>
    [Theory]
    [InlineData("D d = (D)0; float a = d.x + d.y + d.n.z", null)]
    [InlineData("E e = (E)0; float a = e.x + e.y + e.z", null)]
    [InlineData("D d = { 1, float3(0, 0, 1), 2 }", null)]
    [InlineData("E e = { 1, float3(0, 0, 1), 2, 3 }", null)]
    [InlineData("E e = (E)0; float a = e.missing", "HL0312")]
    [InlineData("E e = (E)0; float a = e.n.w", "HL0312")]
    [InlineData("D d = { 1, 2 }", "HL0352")]
    public void 継承した基底のメンバーを使える(string statement, string? expected)
        => AssertReported(statement, expected);

    [Fact]
    public void 基底を引けなければ無いとは言わない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            struct D : Unknown { float y; };
            float4 main() : SV_Target { D d = (D)0; return d.x + d.y; }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0312");
    }

    /// <summary>メソッドの前置き。</summary>
    private const string MethodPrelude = """
        struct S
        {
            float x;
            float Twice() { return x * 2; }
            float Scale(float k, float b = 0) { return x * k + b; }
            float3 Dir();
            float operator[](uint i) { return x; }
            float2 operator()(float t) { return float2(x, t); }
        };
        float3 S::Dir() { return float3(x, 0, 0); }
        struct B { float y; float GetY() { return y; } };
        struct D : B { float z; };
        struct H { float v; float Get() { return v; } float Get(float k) { return v * k; } float2 Pair(float a) { return a; } float3 Pair(float a, float b) { return a; } };
        """;

    /// <summary>
    /// メソッドの呼び出しと <c>operator[]</c> / <c>operator()</c> を、DXC と同じに扱うことを検証する (G2 / G3)。
    /// </summary>
    /// <param name="statement">main の中に書く文。</param>
    /// <param name="expected">報告されるはずの規則。正しいなら <see langword="null"/>。</param>
    [Theory]
    [InlineData("S s = (S)0; float a = s.Twice()", null)]
    [InlineData("S s = (S)0; s.Nope()", "HL0312")]
    [InlineData("S s = (S)0; float a = s.Twice().q", "HL0312")]
    [InlineData("S s = (S)0; float a = s.Twice().x", null)]
    [InlineData("S s = (S)0; s.Twice(1)", "HL0341")]
    [InlineData("S s = (S)0; float a = s.Scale(2)", null)]
    [InlineData("S s = (S)0; float a = s.Scale(2, 1)", null)]
    [InlineData("S s = (S)0; float a = s.Scale()", "HL0341")]
    [InlineData("S s = (S)0; float a = s.Scale(1, 2, 3)", "HL0341")]
    [InlineData("S s = (S)0; float3 d = s.Dir(); float a = s.Dir().z", null)]
    [InlineData("S s = (S)0; float a = s.Dir().w", "HL0312")]
    [InlineData("S s = (S)0; float a = s[0]", null)]
    [InlineData("S s = (S)0; float a = s[0].q", "HL0312")]
    [InlineData("S s = (S)0; float2 a = s(0.5)", null)]
    [InlineData("S s = (S)0; float a = s(0.5).z", "HL0312")]
    [InlineData("D d = (D)0; float a = d.GetY()", null)]
    [InlineData("D d = (D)0; float a = d.GetY().q", "HL0312")]
    [InlineData("D d = (D)0; d.Nope()", "HL0312")]
    [InlineData("H h = (H)0; float a = h.Get() + h.Get(2)", null)]
    [InlineData("H h = (H)0; float a = h.Get(1, 2)", "HL0341")]
    [InlineData("H h = (H)0; float a = h.Pair(1).y", null)]
    [InlineData("H h = (H)0; float a = h.Pair(1).z", "HL0312")]
    [InlineData("H h = (H)0; float a = h.Pair(1, 2).z", null)]
    [InlineData("S s[2]; float a = s[1].Twice()", null)]
    [InlineData("S s[2]; float a = s[1][0]", null)]
    public void メソッドと演算子の呼び出しをDXCと同じに扱う(string statement, string? expected)
        => AssertReported(statement, expected, MethodPrelude);

    /// <summary>template の構造体の前置き。</summary>
    private const string TemplatePrelude = """
        template<typename T> struct Box { T v; float w; T Get() { return v; } float Len() { return 1; } T operator[](uint i) { return v; } };
        template<typename T, uint N> struct Arr { T items[N]; T First() { return items[0]; } };
        template<typename T> struct Derived : Box<T> { T extra; };
        struct Fixed : Box<float3> { float z; };
        typedef float4 F4;
        """;

    /// <summary>template の構造体のメンバーを、DXC と同じに扱うことを検証する (G1)。</summary>
    /// <param name="statement">main の中に書く文。</param>
    /// <param name="expected">報告されるはずの規則。正しいなら <see langword="null"/>。</param>
    /// <remarks>
    /// 型引数を付けた基底 (<c>Fixed : Box&lt;float3&gt;</c>) のフィールドの型は引かない (<c>f.v.w</c> は DXC ではエラーだが報告しない)。
    /// </remarks>
    [Theory]
    [InlineData("Box<float4> b = (Box<float4>)0; float4 a = b.v", null)]
    [InlineData("Box<float4> b = (Box<float4>)0; b.missing = 1", "HL0312")]
    [InlineData("Box<float4> b = (Box<float4>)0; float a = b.v.q", "HL0312")]
    [InlineData("Box<float4> b = (Box<float4>)0; float a = b.v.w", null)]
    [InlineData("Box<float3> b = (Box<float3>)0; float a = b.v.w", "HL0312")]
    [InlineData("Box<float4> b = (Box<float4>)0; float a = b.Get().q", "HL0312")]
    [InlineData("Box<float4> b = (Box<float4>)0; float a = b.Get().w", null)]
    [InlineData("Box<float4> b = (Box<float4>)0; b.Nope()", "HL0312")]
    [InlineData("Box<float4> b = (Box<float4>)0; float a = b.w.y", "HL0312")]
    [InlineData("Box<float4> b = (Box<float4>)0; float a = b.Len().y", "HL0312")]
    [InlineData("Box<float4> b = (Box<float4>)0; float a = b[0].q", "HL0312")]
    [InlineData("Box<F4> b = (Box<F4>)0; float a = b.v.q", "HL0312")]
    [InlineData("Box<float4> b = (Box<float4>)0; b.Get(1)", "HL0341")]
    [InlineData("Arr<float, 4> r = (Arr<float, 4>)0; float a = r.items[0]", null)]
    [InlineData("Arr<float, 4> r = (Arr<float, 4>)0; float a = r.First()", null)]
    [InlineData("Arr<float, 4> r = (Arr<float, 4>)0; r.missing = 1", "HL0312")]
    [InlineData("Box<float4> bs[2]; float a = bs[0].v.x", null)]
    [InlineData("Derived<float4> d = (Derived<float4>)0; float a = d.v.x + d.extra.y", null)]
    [InlineData("Derived<float4> d = (Derived<float4>)0; d.missing = 1", "HL0312")]
    [InlineData("Fixed f = (Fixed)0; float a = f.v.x + f.z + f.Get().y", null)]
    [InlineData("Fixed f = (Fixed)0; float a = f.v.w", null)]
    [InlineData("Fixed f = (Fixed)0; f.missing = 1", "HL0312")]
    public void templateの構造体のメンバーをDXCと同じに扱う(string statement, string? expected)
        => AssertReported(statement, expected, TemplatePrelude);

    /// <summary>演算子の前置き。</summary>
    private const string OperatorPrelude = """
        struct P { float x; float Self() { return this.x; } };
        struct O { float x; O operator+(O o) { return o; } bool operator==(O o) { return x == o.x; } bool operator&&(bool b) { return b; } O operator-() { return this; } };
        struct W { uint i; uint Get(); };
        uint W::Get() { return this.i; }
        cbuffer C { float k; };
        """;

    /// <summary>構造体に使えない演算子を、DXC と同じに扱うことを検証する (G4 / G6、HL0346)。</summary>
    /// <param name="statement">main の中に書く文。</param>
    /// <param name="expected">報告されるはずの規則。正しいなら <see langword="null"/>。</param>
    [Theory]
    [InlineData("P a = (P)0; P b = (P)0; P c = k > 0 ? a : b", "HL0346")]
    [InlineData("P a = (P)0; float c = k > 0 ? a.x : 1", null)]
    [InlineData("O a = (O)0; O b = (O)0; O c = k > 0 ? a : b", "HL0346")]
    [InlineData("P a = (P)0; P b = (P)0; P c = a + b", "HL0346")]
    [InlineData("P a = (P)0; P c = a * 2", "HL0346")]
    [InlineData("P a = (P)0; P c = -a", "HL0346")]
    [InlineData("P a = (P)0; bool c = !a", "HL0346")]
    [InlineData("P a = (P)0; P b = (P)0; bool c = a == b", "HL0346")]
    [InlineData("P a = (P)0; a++", "HL0346")]
    [InlineData("P a = (P)0; ++a", "HL0346")]
    [InlineData("P a = (P)0; P b = (P)0; a += b", "HL0346")]
    [InlineData("P a = (P)0; float c = 2 * a", "HL0346")]
    [InlineData("P a = (P)0; float c = 2 * a.x", null)]
    [InlineData("P a = (P)0; bool c = a && true", "HL0346")]
    [InlineData("P a = (P)0; bool c = true || a", "HL0346")]
    [InlineData("O a = (O)0; O b = (O)0; O c = a + b", null)]
    [InlineData("O a = (O)0; O c = a * 2", "HL0346")]
    [InlineData("O a = (O)0; O b = (O)0; bool c = a == b", null)]
    [InlineData("O a = (O)0; O b = (O)0; bool c = a != b", "HL0346")]
    [InlineData("O a = (O)0; bool c = a && true", null)]
    [InlineData("O a = (O)0; bool c = true && a", "HL0346")]
    [InlineData("O a = (O)0; O b = (O)0; a += b", "HL0346")]
    [InlineData("P a = (P)0; P b = (P)0; a = b", null)]
    [InlineData("P a = (P)0; P c = (a)", null)]
    [InlineData("P a = (P)0; float c = a.Self()", null)]
    [InlineData("W w = (W)0; uint c = w.Get()", null)]
    public void 構造体に使えない演算子をDXCと同じに扱う(string statement, string? expected)
        => AssertReported(statement, expected, OperatorPrelude);

    /// <summary>
    /// 構造体の外に同じ演算子の定義があれば、HL0346 を報告しないことを検証する。
    /// </summary>
    /// <remarks>
    /// DXC 1.6 は構造体の外の演算子を定義としては受け付け、式には使わない (<c>2 * a</c> はエラー)。
    /// 新しい DXC で使われるかもしれないので、定義があれば言い切らない。
    /// </remarks>
    [Fact]
    public void 構造体の外に演算子の定義があれば報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            struct S { float x; };
            S operator*(float k, S s) { S r; r.x = s.x * k; return r; }
            float4 main() : SV_Target { S a = (S)0; S c = 2 * a; S d = a + a; return c.x + d.x; }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0346");
        Assert.Contains("'+'", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// HLSL 2018 では構造体の増減 (<c>a++</c>) を報告しないことを検証する。FXC は受け付ける (成分ごとに増減する)。
    /// </summary>
    [Fact]
    public void HLSL2018では構造体の増減を報告しない()
    {
        SourceText text = SourceText.From("""
            struct P { float x; };
            float4 main() : SV_Target { P a = (P)0; a++; ++a; P b = (P)0; P c = a + b; return c.x; }
            """, "test.hlsl");
        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [HlslEnvironment.WholeFile(text)],
            text,
            new HlslEnvironment(new HlslEnvironmentOptions { LanguageVersion = HlslLanguageVersion.Hlsl2018 }),
            ShaderSourceInfo.None);

        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0346");
        Assert.Contains("'+'", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>多重定義できない演算子の定義を報告することを検証する (HL0346)。</summary>
    /// <param name="definition">構造体の中に書く定義。</param>
    /// <param name="expected">報告されるはずの規則。正しいなら <see langword="null"/>。</param>
    [Theory]
    [InlineData("O operator<<(O o) { return o; }", "HL0346")]
    [InlineData("O operator+=(O o) { return o; }", "HL0346")]
    [InlineData("O operator=(O o) { return o; }", "HL0346")]
    [InlineData("O operator++() { return (O)0; }", "HL0346")]
    [InlineData("O operator--() { return (O)0; }", "HL0346")]
    [InlineData("O operator+(O o) { return o; }", null)]
    [InlineData("O operator-() { return (O)0; }", null)]
    [InlineData("float operator[](uint i) { return x; }", null)]
    public void 多重定義できない演算子の定義を報告する(string definition, string? expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze($$"""
            struct O { float x; {{definition}} };
            float4 main() : SV_Target { return 0; }
            """);

        string[] reported = [.. diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.Id)];

        Assert.Equal(expected is null ? [] : [expected], reported);
    }

    [Fact]
    public void メソッドの中のthisは構造体として読む()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            struct P { float x; float Bad() { return this.missing; } float Y() { return this.x.y; } };
            float4 main() : SV_Target { return 0; }
            """);

        Assert.Equal(["HL0312", "HL0312"], diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.Id));
    }

    /// <summary>構造体の変換の前置き。</summary>
    private const string ConversionPrelude = """
        struct P { float x; };
        struct Q { float x; };
        struct R { float x; float y; };
        struct D : P { float z; };
        struct E : D { float w; };
        template<typename T> struct Box { T v; };
        typedef P PAlias;
        cbuffer C { float k; float4 f4; };
        P MakeP() { return (P)0; }
        float TakeP(P p) { return p.x; }
        """;

    /// <summary>構造体の暗黙の変換を、DXC と同じに扱うことを検証する (G7)。</summary>
    /// <param name="statement">main の中に書く文。</param>
    /// <param name="expected">報告されるはずの規則。正しいなら <see langword="null"/>。</param>
    /// <remarks>
    /// template の構造体は型引数が違えば別の型だが、名前だけでは区別できないので見ない
    /// (<c>Box&lt;float4&gt;</c> から <c>Box&lt;float3&gt;</c> は DXC ではエラーだが報告しない)。
    /// </remarks>
    [Theory]
    [InlineData("Q q = (Q)0; P p = q", "HL0350")]
    [InlineData("R r = (R)0; P p = r", "HL0350")]
    [InlineData("P p = (P)0; float f = p", "HL0350")]
    [InlineData("P p = k", "HL0350")]
    [InlineData("P p = 0", "HL0350")]
    [InlineData("P p = (P)k", null)]
    [InlineData("Q q = (Q)0; P p = (P)q", null)]
    [InlineData("D d = (D)0; P p = d", null)]
    [InlineData("E e = (E)0; P p = e", null)]
    [InlineData("P p = (P)0; D d = p", "HL0350")]
    [InlineData("Box<float4> b = (Box<float4>)0; Box<float3> c = b", null)]
    [InlineData("PAlias a = (P)0; P p = a", null)]
    [InlineData("Q q = (Q)0; P p = MakeP(); p = q", "HL0350")]
    [InlineData("Q q = (Q)0; float r = TakeP(q)", "HL0340")]
    [InlineData("float r = TakeP(MakeP())", null)]
    [InlineData("D d = (D)0; float r = TakeP(d)", null)]
    [InlineData("P p = (P)0; float4 v = p", "HL0350")]
    [InlineData("P p = f4", "HL0350")]
    [InlineData("P p = { 1 }", null)]
    public void 構造体の暗黙の変換をDXCと同じに扱う(string statement, string? expected)
        => AssertReported(statement, expected, ConversionPrelude);

    [Fact]
    public void 構造体を返すreturnの型を検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            struct P { float x; };
            struct Q { float x; };
            P RetQ() { Q q = (Q)0; return q; }
            float RetP() { return (P)0; }
            P Good() { return (P)0; }
            float4 main() : SV_Target { return 0; }
            """);

        Assert.Equal(["HL0351", "HL0351"], diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.Id));
    }

    [Fact]
    public void HLSL2018では構造体の変換を見ない()
    {
        SourceText text = SourceText.From("""
            struct P { float x; };
            struct Q { float x; };
            float4 main() : SV_Target { Q q = (Q)0; P p = q; return p.x; }
            """, "test.hlsl");
        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [HlslEnvironment.WholeFile(text)],
            text,
            new HlslEnvironment(new HlslEnvironmentOptions { LanguageVersion = HlslLanguageVersion.Hlsl2018 }),
            ShaderSourceInfo.None);

        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0350");
    }

    /// <summary>
    /// <c>class</c> / <c>interface</c> / 明示的な特殊化 (<c>template&lt;&gt;</c>) を構造体と同じに検査することを検証する (G8)。
    /// </summary>
    /// <param name="code">シェーダー全体。</param>
    /// <param name="expected">報告されるはずの規則。正しいなら <see langword="null"/>。</param>
    [Theory]
    [InlineData("class Light { float3 color; float3 Get() { return color; } }; float4 main() : SV_Target { Light l; l.color = 1; return float4(l.Get() + l.color, 1); }", null)]
    [InlineData("class Light { float3 color; }; float4 main() : SV_Target { Light l; l.color = 0; return l.missing; }", "HL0312")]
    [InlineData("class Light { float3 color; }; float4 main() : SV_Target { Light l; l.color = 0; return l.Nope(); }", "HL0312")]
    [InlineData("class B { float x; }; class D : B { float y; }; float4 main() : SV_Target { D d; d.x = 1; d.y = 2; return d.x + d.y; }", null)]
    [InlineData("interface ILight { float3 Color(); }; float4 main() : SV_Target { return 0; }", null)]
    [InlineData("interface ILight { float3 Color(); }; class Sun : ILight { float3 Color() { return 1; } }; float4 main() : SV_Target { Sun s; return float4(s.Color(), 1); }", null)]
    [InlineData("interface ILight { float3 Color(); }; class Sun : ILight { float3 Color() { return 1; } }; float4 main() : SV_Target { Sun s; return float4(s.Nope(), 1); }", "HL0312")]
    [InlineData("class Light { float3 color; } g_light; float4 main() : SV_Target { return float4(g_light.color, 1); }", null)]
    [InlineData("struct Light { float3 color; } g_light; float4 main() : SV_Target { return float4(g_light.color, 1); }", null)]
    [InlineData("template<typename T> T Twice(T x) { return x + x; } template<> float Twice<float>(float x) { return x * 2; } float4 main() : SV_Target { return Twice(2.0f) + Twice<float>(1.0f); }", null)]
    [InlineData("template<typename T> T Twice(T x) { return x + x; } template<> float Twice<float>(float x) { return x * missingName; } float4 main() : SV_Target { return Twice(2.0f); }", "HL0310")]
    [InlineData("template<typename T> struct Box { T v; }; template<> struct Box<float> { float v; float extra; }; float4 main() : SV_Target { Box<float> b = (Box<float>)0; return b.extra; }", null)]
    [InlineData("template<typename T> struct Box { T v; }; template<> struct Box<float> { float v; float extra; }; float4 main() : SV_Target { Box<float> b = (Box<float>)0; return b.nope; }", "HL0312")]
    [InlineData("struct Light; struct Light { float x; }; float4 main() : SV_Target { Light l = (Light)0; return l.x; }", null)]
    [InlineData("class ObjModelLoader; float4 main() : SV_Target { return 0; }", null)]
    public void classとinterfaceと明示的な特殊化をDXCと同じに扱う(string code, string? expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(code);
        string[] reported = [.. diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.Id)];

        Assert.True(
            (expected is null ? [] : new[] { expected }).SequenceEqual(reported),
            $"期待: [{expected}] 実際: [{string.Join(", ", diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}"))}]");
    }

    /// <summary>前置きの構造体を使う文を解析し、報告された規則を比べる。</summary>
    /// <param name="statement">main の中に書く文。</param>
    /// <param name="expected">報告されるはずの規則。正しいなら <see langword="null"/>。</param>
    /// <param name="prelude">前に置く宣言。省略すると継承の構造体。</param>
    private static void AssertReported(string statement, string? expected, string prelude = Prelude)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze($$"""
            {{prelude}}
            float4 main() : SV_Target { {{statement}}; return 0; }
            """);

        string[] reported = [.. diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.Id)];

        Assert.True(
            (expected is null ? [] : new[] { expected }).SequenceEqual(reported),
            $"期待: [{expected}] 実際: [{string.Join(", ", diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}"))}]");
    }

    private static ImmutableArray<Diagnostic> Analyze(string code)
    {
        SourceText text = SourceText.From(code, "test.hlsl");
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [HlslEnvironment.WholeFile(text)], text, new HlslEnvironment(), ShaderSourceInfo.None);
        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }
}
