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

    /// <summary>前置きの構造体を使う文を解析し、報告された規則を比べる。</summary>
    /// <param name="statement">main の中に書く文。</param>
    /// <param name="expected">報告されるはずの規則。正しいなら <see langword="null"/>。</param>
    private static void AssertReported(string statement, string? expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze($$"""
            {{Prelude}}
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
