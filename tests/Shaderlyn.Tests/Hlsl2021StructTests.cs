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
