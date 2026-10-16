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
/// HLSL 2021 の <c>enum</c> / <c>enum class</c> を構文木にして検査することの検証 (U9b)。
/// </summary>
public sealed class Hlsl2021EnumTests
{
    [Fact]
    public void enumを構文木にする()
    {
        ShaderSyntaxTree tree = Parse2021("""
            enum Mode { First, Second = 4, Third };
            enum class Filter : uint { Point, Linear };
            """);

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));

        EnumDeclarationSyntax mode = Assert.IsType<EnumDeclarationSyntax>(tree.Root.Declarations[0]);
        Assert.Equal("Mode", mode.Name);
        Assert.False(mode.IsScoped);
        Assert.Equal(["First", "Second", "Third"], mode.Members.Select(m => m.Name));
        Assert.NotNull(mode.Members[1].Value);

        EnumDeclarationSyntax filter = Assert.IsType<EnumDeclarationSyntax>(tree.Root.Declarations[1]);
        Assert.True(filter.IsScoped);
        Assert.Equal(["Point", "Linear"], filter.Members.Select(m => m.Name));
    }

    [Fact]
    public void HLSL2018では今までどおり使えない構文として報告する()
    {
        ShaderSyntaxTree tree = HlslHarness.Parse("enum Mode { First, Second };");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("HL0003", diagnostic.Id);
    }

    [Fact]
    public void 列挙子を宣言された名前として扱う()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            enum Mode { Opaque, Cutout };
            enum class Filter { Point, Linear };

            namespace AnalyticPrimitive
            {
                enum Enum { AABB = 0, Spheres, Count };
            }

            cbuffer Constants { uint primitive; };

            float4 main() : SV_Target
            {
                Mode mode = Opaque;
                Filter filter = Filter::Linear;
                AnalyticPrimitive::Enum type = (AnalyticPrimitive::Enum) primitive;

                switch (type)
                {
                    case AnalyticPrimitive::Spheres: return 1;
                    default: break;
                }

                return mode == Cutout && filter == Filter::Point ? 0.5 : 0;
            }
            """);

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Fact]
    public void 宣言されていない列挙子は報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            enum Mode { Opaque, Cutout };
            float4 main() : SV_Target { return Transparent; }
            """);

        Assert.Contains(diagnostics, d => d.Id == "HL0310" && d.GetMessage().Contains("Transparent", StringComparison.Ordinal));
    }

    /// <summary>
    /// enum の列挙子・変換・演算子を、DXC (<c>-HV 2021</c>) と同じに扱うことを検証する (G5)。
    /// </summary>
    /// <param name="statement">main の中に書く文。</param>
    /// <param name="expected">報告されるはずの規則。正しいなら <see langword="null"/>。</param>
    /// <remarks>どの行も DXC で結果を確かめた。</remarks>
    [Theory]
    [InlineData("Mode m = Mode::C", "HL0312")]
    [InlineData("Plain p = Plain::P2", "HL0312")]
    [InlineData("Plain p = Plain::P1", null)]
    [InlineData("Mode m = Mode::A; int x = m", "HL0347")]
    [InlineData("int x = Mode::A", "HL0347")]
    [InlineData("uint x = (uint)Mode::A", null)]
    [InlineData("Mode m = 1", "HL0347")]
    [InlineData("Mode m = (Mode)1", null)]
    [InlineData("Plain p = 1", "HL0347")]
    [InlineData("Plain p = P0; int x = p", null)]
    [InlineData("float f = P0", null)]
    [InlineData("Plain p = P0; Mode m = p", "HL0347")]
    [InlineData("Mode m = Mode::A; Plain p = m", "HL0347")]
    [InlineData("int x = Mode::A + 1", "HL0347")]
    [InlineData("int x = P0 + 1", null)]
    [InlineData("Mode m = Mode::A; bool b = m == Mode::B", null)]
    [InlineData("Mode m = Mode::A; bool b = m == 1", "HL0347")]
    [InlineData("Plain p = P0; bool b = p == 1", null)]
    [InlineData("float r = Take(Mode::A)", "HL0347")]
    [InlineData("float r = Take(P1)", null)]
    [InlineData("float r = TakeMode(Mode::A)", null)]
    [InlineData("float r = TakeMode(1)", "HL0347")]
    [InlineData("float r = TakeMode(P0)", "HL0347")]
    [InlineData("Typed t = Typed::T1; uint u = (uint)t", null)]
    [InlineData("Mode m = Mode::A; switch (m) { case Mode::A: break; default: break; }", null)]
    [InlineData("Mode m = Mode::A; float f = m", "HL0347")]
    [InlineData("Mode m = Mode::A; m = Mode::B", null)]
    [InlineData("Mode m = Mode::A; m = P0", "HL0347")]
    [InlineData("Mode m = Mode::A; int x = -m", "HL0347")]
    [InlineData("Mode m = Mode::A; bool x = !m", "HL0347")]
    [InlineData("Mode m = Mode::A; m++", "HL0347")]
    [InlineData("Mode m = Mode::A; bool x = m < Mode::B", "HL0347")]
    [InlineData("Plain p = P0; p++", "HL0347")]
    [InlineData("Plain p = P0; int x = -p", null)]
    [InlineData("Mode m = Mode::A; int x = (int)m * 2", null)]
    [InlineData("Plain p = (Plain)ci", null)]
    [InlineData("Mode m = Mode::A; m |= Mode::B", "HL0347")]
    [InlineData("S s; s.mode = 1", "HL0347")]
    [InlineData("S s; s.mode = Mode::B; bool b = s.mode == Mode::A", null)]
    public void enumの値をDXCと同じに扱う(string statement, string? expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze($$"""
            enum class Mode { A, B };
            enum Plain { P0, P1 };
            enum class Typed : uint { T0 = 1, T1 };
            struct S { Mode mode; };
            cbuffer C { int ci; float k; };
            float Take(int v) { return v; }
            float TakeMode(Mode m) { return 0; }
            float4 main() : SV_Target { {{statement}}; return 0; }
            """);

        string[] reported = [.. diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.Id)];

        Assert.True(
            (expected is null ? [] : new[] { expected }).SequenceEqual(reported),
            $"期待: [{expected}] 実際: [{string.Join(", ", diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}"))}]");
    }

    [Fact]
    public void returnの値も検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            enum class Mode { A, B };
            int RetInt() { return Mode::A; }
            Mode RetMode() { return 1; }
            Mode Good() { return Mode::B; }
            float4 main() : SV_Target { return 0; }
            """);

        Assert.Equal(["HL0347", "HL0347"], diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.Id));
    }

    private static ShaderSyntaxTree Parse2021(string code)
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
