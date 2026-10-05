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
/// HLSL 2021 の <c>template</c> を構文木にして検査することの検証 (U9d)。
/// </summary>
/// <remarks>
/// 期待する結果は DXC (<c>-HV 2021</c>) で確かめた。
/// 型引数 (<c>T</c>) を含む型は分からない型として扱い、それを根拠に指摘しない。
/// </remarks>
public sealed class Hlsl2021TemplateTests
{
    [Fact]
    public void templateを構文木にする()
    {
        HlslSyntaxTree tree = Parse2021("""
            template<typename T, uint N = 4>
            struct Array { T items[N]; };

            template<typename T>
            T Twice(T x) { return x + x; }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));

        TemplateDeclarationSyntax array = Assert.IsType<TemplateDeclarationSyntax>(tree.Root.Declarations[0]);
        Assert.Equal(["T", "N"], array.ParameterNames.AsEnumerable());
        Assert.Equal("Array", Assert.IsType<StructDeclarationSyntax>(array.Declaration).Name);

        TemplateDeclarationSyntax twice = Assert.IsType<TemplateDeclarationSyntax>(tree.Root.Declarations[1]);
        Assert.Equal(["T"], twice.ParameterNames.AsEnumerable());
        Assert.Equal("Twice", Assert.IsType<FunctionDeclarationSyntax>(twice.Declaration).Name);
        Assert.Empty(tree.UnanalyzedIdentifiers);
    }

    [Fact]
    public void 型引数を書いた呼び出しを構文木にする()
    {
        HlslSyntaxTree tree = Parse2021("""
            ByteAddressBuffer raw;
            float4 main() : SV_Target { return Twice<float4>(raw.Load<float4>(0)); }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));

        InvocationExpressionSyntax[] calls = [.. tree.Root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()];
        Assert.Equal(2, calls.Length);
        Assert.All(calls, c => Assert.True(c.HasTypeArguments));
        Assert.All(calls, c => Assert.Equal("float4", Assert.Single(c.TypeArgumentTokens).Text));
    }

    [Fact]
    public void HLSL2018では比較として読む()
    {
        HlslSyntaxTree tree = HlslHarness.Parse("float f(float a, float b, float c) { return a < b > (c); }");

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));
        Assert.DoesNotContain(tree.Root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(), c => c.HasTypeArguments);
    }

    [Fact]
    public void HLSL2018では今までどおり使えない構文として報告する()
    {
        HlslSyntaxTree tree = HlslHarness.Parse("template<typename T> T Twice(T x) { return x + x; }");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("HL0003", diagnostic.Id);
    }

    [Fact]
    public void 明示的な特殊化は読み飛ばす()
    {
        HlslSyntaxTree tree = Parse2021("template<> float Twice<float>(float x) { return x * 2; }");

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));
        Assert.IsType<IncompleteDeclarationSyntax>(Assert.Single(tree.Root.Declarations));
        Assert.Contains(tree.UnanalyzedIdentifiers, t => t.Text == "Twice");
    }

    [Fact]
    public void 型引数を使った正しいコードを報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            template<typename T>
            T Lerp3(T a, T b, float t) { T d = b - a; return a + d * t; }

            template<typename T, uint N>
            struct Array
            {
                T items[N];
                T Get(uint i) { return items[i]; }
                T Last() { return items[N - 1]; }
            };

            template<typename T>
            struct Box
            {
                T v;
                T Scaled(float s) { return v * s; }
                float Length() { return length(v); }
            };

            template<typename T>
            T Zero() { return (T)0; }

            template<typename T>
            vector<T, 3> Splat3(T x) { vector<T, 3> r = (vector<T, 3>)x; return r; }

            template<typename T>
            void Store(RWStructuredBuffer<T> buffer, uint index, T value) { buffer[index] = value; }

            ByteAddressBuffer raw;
            RWStructuredBuffer<float4> output;
            cbuffer C { float4 tint; float k; };

            float4 main() : SV_Target
            {
                Box<float4> b;
                b.v = tint;
                Array<float, 4> arr;
                arr.items[0] = 1;
                float3 u = Lerp3<float3>(tint.xyz, tint.zyx, k);
                float4 z = Zero<float4>();
                float4 loaded = raw.Load<float4>(0);
                float3 s = Splat3(1.0f);
                Store(output, 0, tint);
                return b.Scaled(2) + arr.Get(0) + float4(u + s, 1) + z + loaded + b.Length();
            }
            """);

        Assert.True(
            !diagnostics.Any(d => d.Severity >= DiagnosticSeverity.Warning),
            string.Join("\n", diagnostics.Select(d => $"{d.Id} {d.Severity}: {d.GetMessage()}")));
    }

    [Fact]
    public void templateの本体を検査する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            template<typename T>
            T Twice(T x) { return x + missingA; }

            template<typename T>
            struct Box
            {
                T v;
                T Scaled(float s) { return v * missingB; }
            };

            float4 main() : SV_Target
            {
                Box<float4> b;
                b.v = 1;
                return Twice<float4>(b.v);
            }
            """);

        Assert.Equal(
            ["missingA", "missingB"],
            diagnostics.Where(d => d.Id == "HL0310").Select(d => d.GetMessage().Split('\'')[1]).Order());
    }

    [Fact]
    public void 型引数を書いた呼び出しは戻り値の型を置き換える()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            template<typename T>
            T Twice(T x) { return x + x; }

            template<typename T>
            float Dot3(T a, T b) { return dot(a, b); }

            float4 main(float4 p : P) : SV_Target
            {
                float b = Twice<float4>(p).w;
                float c = Twice<float4>(p).q;
                float d = Dot3(p, p) + Dot3<float4>(p, p).x;
                float f = Twice(p).w;
                return b + c + d + f;
            }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
        Assert.Equal("HL0312", diagnostic.Id);
        Assert.Contains("'q'", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// template と同じ名前のふつうの関数があっても、template のほうの呼び出しを指摘しないことを検証する。
    /// </summary>
    /// <remarks>
    /// DXC はこの順 (ふつうの関数が先) なら受け付ける。逆の順では、後ろの関数を特殊化と読んで拒む。
    /// </remarks>
    [Fact]
    public void 同じ名前のふつうの関数があれば型を主張しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            float2 Pick(float x, float y) { return float2(x, y); }

            template<typename T>
            T Pick(T x) { return x; }

            float4 main(float4 p : P) : SV_Target
            {
                return Pick<float4>(p).w;
            }
            """);

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
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
