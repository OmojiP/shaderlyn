using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Tests;

/// <summary>
/// HLSL 2021 の演算子の多重定義 (<c>operator+</c> など) を関数として読むことの検証 (U9c)。
/// </summary>
/// <remarks>
/// 期待する結果は DXC (<c>-HV 2021</c>) で確かめた。演算子は構造体の中でだけ定義できる。
/// </remarks>
public sealed class Hlsl2021OperatorTests
{
    [Fact]
    public void 演算子を関数として構文木にする()
    {
        HlslSyntaxTree tree = Parse2021("""
            struct Complex
            {
                float re;
                Complex operator+(Complex other) { return other; }
                bool operator==(Complex other) { return re == other.re; }
                float operator[](uint i) { return re; }
                float operator()(float t) { return re * t; }
                bool operator<(Complex other) { return re < other.re; }
            };
            """);

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));

        StructDeclarationSyntax structure = Assert.IsType<StructDeclarationSyntax>(tree.Root.Declarations[0]);
        FunctionDeclarationSyntax[] operators = [.. structure.Members.OfType<FunctionDeclarationSyntax>()];

        Assert.All(operators, o => Assert.True(o.IsOperator));
        Assert.Equal(
            ["operator+", "operator==", "operator[]", "operator()", "operator<"],
            operators.Select(o => o.Name));
        Assert.Equal(["t"], operators[3].ParameterList.Select(p => p.Name));
        Assert.All(operators, o => Assert.NotNull(o.Body));
    }

    [Fact]
    public void HLSL2018では今までどおり使えない構文として報告する()
    {
        HlslSyntaxTree tree = HlslHarness.Parse("struct S { float v; S operator+(S o) { return o; } };");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("HL0003", diagnostic.Id);
    }

    [Fact]
    public void 演算子の本体を検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            struct Complex
            {
                float re;
                Complex operator+(Complex other) { Complex r; r.re = re + missingName; return r; }
            };

            float4 main() : SV_Target { Complex a = (Complex)0; return (a + a).re; }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        Assert.Equal("HL0310", diagnostic.Id);
        Assert.Contains("missingName", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 演算の結果を演算子の戻り値の型として読む()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            struct Complex
            {
                float re;
                float im;
                Complex Conjugate() { Complex r; r.re = re; r.im = -im; return r; }
                Complex operator+(Complex other) { Complex r; r.re = re + other.re; r.im = im + other.im; return r; }
                Complex operator*(float s) { Complex r; r.re = re * s; r.im = im * s; return r; }
                Complex operator*(Complex o) { Complex r; r.re = re * o.re; r.im = im * o.im; return r; }
                float2 operator-(Complex o) { return float2(re - o.re, im - o.im); }
            };

            float4 main() : SV_Target
            {
                Complex a = (Complex)0;
                Complex b = (Complex)1;
                float r = (a + b).re;
                Complex c = (a + b).Conjugate();
                float i = (a * 2.0).im + (a * b).re;
                float2 d = (a - b).xy;
                float bad = (a + b).missing;
                return float4(r, c.im, i, d.x + bad);
            }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        Assert.Equal("HL0312", diagnostic.Id);
        Assert.Contains("missing", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void 戻り値の型が割れる演算子からは型を主張しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            struct Complex
            {
                float re;
                Complex operator*(Complex o) { return o; }
                float2 operator*(float s) { return float2(re, s); }
            };

            float4 main() : SV_Target
            {
                Complex a = (Complex)0;
                return (a * 2.0).x + (a * a).re;
            }
            """);

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    private static HlslSyntaxTree Parse2021(string code)
        => HlslSyntaxTree.Parse(
            SourceText.From(code, "test.hlsl"),
            new PreprocessorOptions { Language = new HlslLanguageOptions { LanguageVersion = HlslLanguageVersion.Hlsl2021 } });

    private static ImmutableArray<Diagnostic> Analyze(string code)
    {
        SourceText text = SourceText.From(code, "test.hlsl");
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [HlslEnvironment.WholeFile(text)], text, new HlslEnvironment(), ShaderSourceInfo.None);
        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }
}
