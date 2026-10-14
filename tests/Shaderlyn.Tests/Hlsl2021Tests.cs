using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Tests;

/// <summary>
/// HLSL 2021 の構文を受け付ける環境での解析の検証。
/// </summary>
/// <remarks>
/// <para>
/// 既定の方言 (FXC。Unity の既定のコンパイラ) は <c>template</c> / <c>enum</c> / 演算子の多重定義 / ビットフィールドを受け付けないので、
/// 使えない構文 (HL0003) として報告する。DXC の HLSL 2021 は受け付ける。
/// <c>class</c> / <c>interface</c> は FXC でも DXC でも使える。
/// </para>
/// <para>
/// <c>enum</c>・演算子の多重定義・<c>template</c>・<c>class</c>・<c>interface</c> は構文木にする
/// (<see cref="Hlsl2021EnumTests"/>、<see cref="Hlsl2021OperatorTests"/>、<see cref="Hlsl2021TemplateTests"/>)。
/// まだ構文木にしない構文は読み飛ばし、その名前を「解析していない場所の名前」として扱う。
/// 呼び出しを「宣言されていない」と報告すると、正しいコードを誤りとして指摘することになる。
/// </para>
/// </remarks>
public sealed class Hlsl2021Tests
{
    /// <summary>HLSL 2021 で書いた、FXC (Unity の既定) では使えない構文。</summary>
    public static TheoryData<string, string> Constructs { get; } = new()
    {
        { "template<typename T> T Twice(T x) { return x + x; }", "template" },
        { "enum Mode { ModeA, ModeB };", "enum" },
        { "struct S { uint flags : 4; };", "ビットフィールド" },
    };

    [Theory]
    [MemberData(nameof(Constructs))]
    public void 既定ではUnityで使えない構文として報告する(string code, string expected)
    {
        HlslSyntaxTree tree = HlslSyntaxTree.Parse(SourceText.From(code, "test.hlsl"));

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("HL0003", diagnostic.Id);
        Assert.Contains(expected, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Empty(tree.UnanalyzedIdentifiers);
    }

    [Theory]
    [MemberData(nameof(Constructs))]
    public void HLSL2021では報告しない(string code, string unused)
    {
        _ = unused;

        HlslSyntaxTree tree = HlslSyntaxTree.Parse(
            SourceText.From(code, "test.hlsl"),
            new PreprocessorOptions { LanguageVersion = HlslLanguageVersion.Hlsl2021 });

        Assert.Empty(tree.Diagnostics);
    }

    /// <summary>型引数の並びを読めない template (既定値の中の <c>&gt;&gt;</c> を 1 つの記号として読む) は読み飛ばし、名前を残すことを検証する。</summary>
    [Fact]
    public void 読み飛ばした宣言の名前を残す()
    {
        HlslSyntaxTree tree = HlslSyntaxTree.Parse(
            SourceText.From("template<typename T = vector<float, 3>> T Make() { return (T)0; }", "test.hlsl"),
            new PreprocessorOptions { LanguageVersion = HlslLanguageVersion.Hlsl2021 });

        Assert.Empty(tree.Diagnostics);
        Assert.Contains(tree.UnanalyzedIdentifiers, t => t.Text == "Make");
    }

    /// <summary>
    /// 読み飛ばした template の関数を呼んでも、宣言されていないと報告しないことを検証する。
    /// </summary>
    [Fact]
    public void 読み飛ばした宣言を呼んでも宣言が無いとは言わない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            template<typename T = vector<float, 3>> T Make() { return (T)0; }

            float4 main(float4 p : POSITION) : SV_POSITION
            {
                return float4(Make(), 1);
            }
            """);

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    /// <summary>
    /// HLSL 2021 / SM6 で正しく書いたコードに、警告やエラーを出さないことを検証する。
    /// </summary>
    /// <param name="code">正しいコード。</param>
    [Theory]
    [InlineData("struct S { float x; float Get() { return x; } }; float4 main() : SV_Target { S s; s.x = 1; return s.Get(); }")]
    [InlineData("struct S { float x; S operator+(S o) { S r; r.x = x + o.x; return r; } }; float4 main() : SV_Target { S a; S b; a.x = 1; b.x = 2; S c = a + b; return c.x; }")]
    [InlineData("template<typename T> struct Box { T v; }; float4 main() : SV_Target { Box<float> b; b.v = 1; return b.v; }")]
    [InlineData("template<typename T> T Twice(T x) { return x + x; } float4 main() : SV_Target { return Twice<float>(1.0); }")]
    [InlineData("enum class Color : uint { Red, Green = 2 }; float4 main() : SV_Target { uint c = (uint)Color::Red; return c; }")]
    [InlineData("enum Mode { ModeA, ModeB }; float4 main() : SV_Target { Mode m = ModeA; return m == ModeB ? 1 : 0; }")]
    [InlineData("namespace N { float h() { return 1; } } float4 main() : SV_Target { return N::h(); }")]
    [InlineData("struct P { uint a : 4; uint b : 28; }; float4 main() : SV_Target { P p; p.a = 1; return p.a; }")]
    [InlineData("float4 main() : SV_Target { uint16_t a = 1; float16_t b = 2; int64_t c = 3; return a + b + c; }")]
    [InlineData("[numthreads(8,8,1)] void cs(uint3 id : SV_DispatchThreadID) { uint w = WaveGetLaneIndex(); float s = WaveActiveSum(1.0); }")]
    public void HLSL2021で正しく書いたコードを報告しない(string code)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(code);

        Assert.True(
            !diagnostics.Any(d => d.Severity >= DiagnosticSeverity.Warning),
            string.Join("\n", diagnostics.Select(d => $"{d.Id} {d.Severity}: {d.GetMessage()}")));
    }

    /// <summary>
    /// ビットフィールドを構造体のフィールドとして読み、無いメンバーを DXC と同じく報告することを検証する (U9e)。
    /// </summary>
    [Fact]
    public void ビットフィールドをフィールドとして読む()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            struct Packed { uint a : 4; uint b : 28; float f; };

            float4 main(uint v : V) : SV_Target
            {
                Packed p;
                p.a = v;
                p.b = v >> 4;
                p.f = 1;
                float x = p.c;
                return p.a + p.b + p.f + x;
            }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        Assert.Equal("HL0312", diagnostic.Id);
        Assert.Contains("a, b, f", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// HLSL 2021 では for の初期化の変数をループの外で使えないことを検証する (DXC 1.8: use of undeclared identifier)。
    /// </summary>
    [Fact]
    public void forの変数をループの外で使うと報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            float4 main() : SV_Target
            {
                float s = 0;
                for (int i = 0; i < 4; i++) { s += i; }
                return s + i;
            }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        Assert.Equal("HL0310", diagnostic.Id);
        Assert.Contains("'i'", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// HLSL 2021 ではベクトルと行列に &amp;&amp; / || / ?: を使えないことを検証する (HL0349)。行は DXC 1.8 がエラーにした式。
    /// </summary>
    [Fact]
    public void ベクトルへの論理演算子と条件演算子を報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            float4 main(float3 a : A, float3 b : B, float1 c : C, float d : D) : SV_Target
            {
                bool3 x = a && b;
                bool3 y = a || b;
                float3 z = a > 0 ? a : b;
                bool1 w = c && c;
                float m = (a > 0).x ? 1 : 0;
                bool n = d > 0 && d < 1;
                float2x2 mm = (float2x2)1;
                bool2x2 q = mm && mm;
                return float4(x.x + y.x + z.x + w.x + m + n + q[0][0], 0, 0, 1);
            }
            """);

        Assert.Equal([3, 4, 5, 6, 10], diagnostics.Where(d => d.Id == "HL0349").Select(d => d.Location.LineSpan.Start.Line + 1).Order().ToArray());
        Assert.DoesNotContain(diagnostics, d => d.Severity >= DiagnosticSeverity.Warning && d.Id != "HL0349");
    }

    /// <summary>FXC では for の変数をループの後でも使え、ベクトルへの論理演算子は成分ごとに計算することを検証する。</summary>
    [Fact]
    public void FXCではforの変数とベクトルへの論理演算子を報告しない()
    {
        SourceText text = SourceText.From("""
            float4 main(float3 a : A, float3 b : B) : SV_Target
            {
                float s = 0;
                for (int i = 0; i < 4; i++) { s += i; }
                bool3 x = a && b;
                return s + i + x.x;
            }
            """, "test.hlsl");

        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [HlslEnvironment.WholeFile(text)],
            text,
            new HlslEnvironment(new HlslEnvironmentOptions { Compiler = HlslCompiler.Fxc }),
            ShaderSourceInfo.None);

        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Assert.DoesNotContain(diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    /// <summary>
    /// FXC と DXC 2021 より前では、for の変数をループの外で使う箇所を、2021 へ移せないと情報で伝えることを検証する (HL0354)。
    /// </summary>
    /// <param name="compiler">コンパイラ。</param>
    [Theory]
    [InlineData(HlslCompiler.Fxc)]
    [InlineData(HlslCompiler.Dxc)]
    public void forの変数をループの外で使うと2021へ移せないと伝える(HlslCompiler compiler)
    {
        SourceText text = SourceText.From("""
            float4 main() : SV_Target
            {
                float s = 0;
                for (int i = 0; i < 4; i++) { s += i; }
                float t = s + i;
                for (int j = 0; j < 2; j++) { s += j; }
                return t;
            }
            """, "test.hlsl");

        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [HlslEnvironment.WholeFile(text)],
            text,
            new HlslEnvironment(new HlslEnvironmentOptions { Compiler = compiler, LanguageVersion = HlslLanguageVersion.Hlsl2018 }),
            ShaderSourceInfo.None);

        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0354");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Equal(5, diagnostic.Location.LineSpan.Start.Line + 1);
        Assert.DoesNotContain(diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    /// <summary>HLSL 2021 だけなら、HL0354 ではなく HL0310 (宣言されていない) になることを検証する。</summary>
    [Fact]
    public void HLSL2021ではforの変数の情報を出さない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            float4 main() : SV_Target
            {
                float s = 0;
                for (int i = 0; i < 4; i++) { s += i; }
                return s + i;
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0354");
        Assert.Contains(diagnostics, d => d.Id == "HL0310");
    }

    /// <summary>HLSL 2021 の環境で 1 ファイルを解析する。</summary>
    /// <param name="code">コード。</param>
    /// <returns>診断。</returns>
    private static ImmutableArray<Diagnostic> Analyze(string code)
    {
        SourceText text = SourceText.From(code, "test.hlsl");

        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [new HlslCompilationUnit(text, new TextSpan(0, text.Length))],
            text,
            new Hlsl2021Environment(),
            ShaderSourceInfo.None);

        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }

    /// <summary>HLSL 2021 を受け付け、構成のシンボルを持たない環境。</summary>
    private sealed class Hlsl2021Environment : IHlslEnvironment
    {
        public string Name => "test-hlsl2021";

        public PreprocessorOptions ConfigurePreprocessor(PreprocessorOptions options, SemanticsOptions semantics)
            => options with { LanguageVersion = HlslLanguageVersion.Hlsl2021 };

        public PreprocessorOptions PrepareUnit(
            HlslCompilationUnit unit,
            ImmutableArray<HlslSyntaxToken> tokens,
            PreprocessorOptions options,
            string filePath)
            => options;

        public VariantSpace ReadDeclaredVariants(HlslCompilationUnit unit, ImmutableArray<HlslSyntaxToken> tokens)
            => VariantSpace.Empty;

        public VariantSpace ReadVariants(HlslCompilationUnit unit, PreprocessResult result) => VariantSpace.Empty;

        public ImmutableArray<HlslEntryPoint> ReadEntryPoints(HlslCompilationUnit unit, PreprocessResult result) => [];

        public bool LooksLikeVariantSymbol(string name) => false;

        public bool IsGeneratedInclude(string path) => false;
    }
}
