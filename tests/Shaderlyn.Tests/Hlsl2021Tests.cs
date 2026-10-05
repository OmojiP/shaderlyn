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
/// Unity は <c>template</c> / <c>enum</c> / <c>class</c> / <c>interface</c> / 演算子の多重定義 / ビットフィールドを受け付けないので、
/// 使えない構文 (HL0003) として報告する。DXC の HLSL 2021 は受け付ける。
/// </para>
/// <para>
/// <c>enum</c>・演算子の多重定義・<c>template</c> は構文木にする
/// (<see cref="Hlsl2021EnumTests"/>、<see cref="Hlsl2021OperatorTests"/>、<see cref="Hlsl2021TemplateTests"/>)。
/// まだ構文木にしない構文は読み飛ばし、その名前を「解析していない場所の名前」として扱う。
/// 呼び出しを「宣言されていない」と報告すると、正しいコードを誤りとして指摘することになる。
/// </para>
/// </remarks>
public sealed class Hlsl2021Tests
{
    /// <summary>HLSL 2021 で書いた、Unity では使えない構文。</summary>
    public static TheoryData<string, string> Constructs { get; } = new()
    {
        { "template<typename T> T Twice(T x) { return x + x; }", "template" },
        { "enum Mode { ModeA, ModeB };", "enum" },
        { "class Light { float3 color; };", "class" },
        { "interface ILight { float3 Color(); };", "interface" },
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

    [Fact]
    public void 読み飛ばした宣言の名前を残す()
    {
        HlslSyntaxTree tree = HlslSyntaxTree.Parse(
            SourceText.From("interface ILight { float3 Color(); };", "test.hlsl"),
            new PreprocessorOptions { LanguageVersion = HlslLanguageVersion.Hlsl2021 });

        Assert.Contains(tree.UnanalyzedIdentifiers, t => t.Text == "ILight");
    }

    /// <summary>
    /// 読み飛ばした template の明示的な特殊化の関数を呼んでも、宣言されていないと報告しないことを検証する。
    /// </summary>
    [Fact]
    public void 読み飛ばした宣言を呼んでも宣言が無いとは言わない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            template<> float4 Twice<float4>(float4 x) { return x + x; }

            float4 main(float4 p : POSITION) : SV_POSITION
            {
                return Twice(p);
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
