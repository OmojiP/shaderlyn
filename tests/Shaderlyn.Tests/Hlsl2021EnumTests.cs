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
/// HLSL 2021 の <c>enum</c> / <c>enum class</c> を構文木にして検査することの検証 (U9b)。
/// </summary>
public sealed class Hlsl2021EnumTests
{
    [Fact]
    public void enumを構文木にする()
    {
        HlslSyntaxTree tree = Parse2021("""
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
        HlslSyntaxTree tree = HlslHarness.Parse("enum Mode { First, Second };");

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

    private static HlslSyntaxTree Parse2021(string code)
        => HlslSyntaxTree.Parse(
            SourceText.From(code, "test.hlsl"),
            new PreprocessorOptions { LanguageVersion = HlslLanguageVersion.Hlsl2021 });

    private static ImmutableArray<Diagnostic> Analyze(string code)
    {
        SourceText text = SourceText.From(code, "test.hlsl");
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [HlslEnvironment.WholeFile(text)], text, new HlslEnvironment(), ShaderSourceInfo.None);
        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }
}
