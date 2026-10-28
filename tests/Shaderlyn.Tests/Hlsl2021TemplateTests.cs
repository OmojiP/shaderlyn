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
        ShaderSyntaxTree tree = Parse2021("""
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
        ShaderSyntaxTree tree = Parse2021("""
            ByteAddressBuffer raw;
            template<typename T> T Twice(T x) { return x + x; }
            float4 main() : SV_Target { return Twice<float4>(raw.Load<float4>(0)); }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));

        InvocationExpressionSyntax[] calls =
        [
            .. tree.Root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Where(c => c.HasTypeArguments),
        ];
        Assert.Equal(2, calls.Length);
        Assert.All(calls, c => Assert.True(c.HasTypeArguments));
        Assert.All(calls, c => Assert.Equal("float4", Assert.Single(c.TypeArgumentTokens).Text));
    }

    [Fact]
    public void 型でないtemplate引数に式を書いた呼び出しを構文木にする()
    {
        // Unreal Engine の NaniteRasterizer.usf の形 (SetupTriangle< NANITE_SUBPIXEL_SAMPLES, !NANITE_TWO_SIDED >)。
        ShaderSyntaxTree tree = Parse2021("""
            #define SAMPLES (1 << 8)
            #define TWO_SIDED 0
            template<uint N, bool B> float Setup(float x) { return B ? x * N : x; }
            float main() : SV_Target { return Setup< SAMPLES, !TWO_SIDED >(1.0); }
            """);

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));

        InvocationExpressionSyntax call = Assert.Single(
            tree.Root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(),
            c => c.HasTypeArguments);
        Assert.Equal("(1<<8),!0", string.Concat(call.TypeArgumentTokens.Select(t => t.Text)));
    }

    [Fact]
    public void templateでない名前の後ろの山括弧は比較として読む()
    {
        // Unreal Engine の VirtualShadowMapDebug.usf の形。OutputPixel を template の名前と読むと、引数が 1 つになる。
        ShaderSyntaxTree tree = Parse2021("bool2 f(uint2 p, uint w, uint d) { return or(p < w, p >(d + w)); }");

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));
        InvocationExpressionSyntax call = Assert.Single(tree.Root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>());
        Assert.Equal(2, call.ArgumentExpressions.Count());
    }

    [Fact]
    public void HLSL2018では比較として読む()
    {
        ShaderSyntaxTree tree = HlslHarness.Parse("float f(float a, float b, float c) { return a < b > (c); }");

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));
        Assert.DoesNotContain(tree.Root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>(), c => c.HasTypeArguments);
    }

    [Fact]
    public void HLSL2018では今までどおり使えない構文として報告する()
    {
        ShaderSyntaxTree tree = HlslHarness.Parse("template<typename T> T Twice(T x) { return x + x; }");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("HL0003", diagnostic.Id);
    }

    [Fact]
    public void 明示的な特殊化を構文木にする()
    {
        ShaderSyntaxTree tree = Parse2021("""
            template<> float Twice<float>(float x) { return x * 2; }
            template<> struct Box<float> { float v; };
            """);

        Assert.True(tree.Diagnostics.IsEmpty, HlslHarness.Describe(tree.Diagnostics));

        TemplateDeclarationSyntax function = Assert.IsType<TemplateDeclarationSyntax>(tree.Root.Declarations[0]);
        Assert.Empty(function.ParameterNames);
        FunctionDeclarationSyntax twice = Assert.IsType<FunctionDeclarationSyntax>(function.Declaration);
        Assert.Equal("Twice", twice.Name);
        Assert.Equal(["<", "float", ">"], twice.SpecializationTokens.Select(t => t.Text));

        TemplateDeclarationSyntax structure = Assert.IsType<TemplateDeclarationSyntax>(tree.Root.Declarations[1]);
        StructDeclarationSyntax box = Assert.IsType<StructDeclarationSyntax>(structure.Declaration);
        Assert.Equal("Box", box.Name);
        Assert.Equal(["<", "float", ">"], box.SpecializationTokens.Select(t => t.Text));
        Assert.Empty(tree.UnanalyzedIdentifiers);
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
            diagnostics.Where(d => d.Id == "CF0310").Select(d => d.GetMessage().Split('\'')[1]).Order());
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
        Assert.Equal("CF0312", diagnostic.Id);
        Assert.Contains("成分 q ", diagnostic.GetMessage(), StringComparison.Ordinal);
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

    /// <summary>
    /// 型引数の推論で、DXC (`-HV 2021`) が拒む呼び出しだけを報告することを検証する。
    /// </summary>
    /// <param name="statement">main の中に書く文。</param>
    /// <param name="expected">報告されるはずの規則。正しい呼び出しなら <see langword="null"/>。</param>
    /// <remarks>どの行も DXC で結果を確かめた。</remarks>
    [Theory]
    [InlineData("float a = MaxOf(k, k)", null)]
    [InlineData("float a = MaxOf(1.0, k)", "HL0345")]
    [InlineData("float a = MaxOf(1.0f, k)", null)]
    [InlineData("float a = MaxOf(1, k)", "HL0345")]
    [InlineData("float a = MaxOf(1.0, 2.0)", null)]
    [InlineData("float a = MaxOf(1, 2)", null)]
    [InlineData("float a = MaxOf(1, 2.0)", "HL0345")]
    [InlineData("float a = MaxOf(k, h)", "HL0345")]
    [InlineData("float a = MaxOf(k, i)", "HL0345")]
    [InlineData("float a = MaxOf(i, u)", "HL0345")]
    [InlineData("float3 a = MaxOf(v, k)", "HL0345")]
    [InlineData("float a = MaxOf(k, d)", "HL0345")]
    [InlineData("float a = MaxOf(k)", "CF0402")]
    [InlineData("float a = MaxOf(k, k, k)", "CF0402")]
    [InlineData("float a = One(1.0)", null)]
    [InlineData("float4 a = One(f4)", null)]
    [InlineData("float a = First(k, v)", null)]
    [InlineData("float a = MaxOf<float>(1.0, k)", null)]
    [InlineData("float a = MaxOf<float>(k, v)", null)]
    [InlineData("float a = MaxOf(v.x, f4.y)", null)]
    [InlineData("float a = MaxOf(k * 2, k)", null)]
    [InlineData("float a = MaxOf(k * 2.0, k)", null)]
    [InlineData("uint a = MaxOf(dw, u)", null)]
    [InlineData("float a = MaxOf(k1, k)", "HL0345")]
    [InlineData("float a = MaxOf(m, k)", "HL0345")]
    [InlineData("float a = MaxOf(k32, k)", null)]
    [InlineData("float a = MaxOf(-1.0, 2.0)", null)]
    [InlineData("float a = MaxOf(-1.0, k)", "HL0345")]
    [InlineData("float a = MaxOf((1.0), k)", "HL0345")]
    [InlineData("float a = MaxOf(k, 1.0L)", "HL0345")]
    [InlineData("float a = MaxOf(i, 1)", "HL0345")]
    [InlineData("float a = MaxOf(u, 1u)", null)]
    [InlineData("float a = Opt(k)", null)]
    [InlineData("float a = Opt(k, 1.0)", "HL0345")]
    [InlineData("float a = Opt(k, k, k)", "CF0402")]
    [InlineData("float a; Out(k, a)", null)]
    [InlineData("float3 a; Out(k, a)", "HL0345")]
    [InlineData("float a = One(f4).q", "CF0312")]
    [InlineData("float a = One(f4).w", null)]
    [InlineData("float a = First(v, k).w", "CF0312")]
    public void 型引数の推論をDXCと同じに扱う(string statement, string? expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze($$"""
            template<typename T> T MaxOf(T a, T b) { return max(a, b); }
            template<typename T> T One(T a) { return a; }
            template<typename T, typename U> T First(T a, U b) { return a; }
            template<typename T> T Opt(T a, T b = (T)0) { return a; }
            template<typename T> void Out(T a, out T b) { b = a; }

            cbuffer C { float k; half h; float3 v; int i; uint u; float4 f4; dword dw; float1 k1; min16float m; float32_t k32; double d; };

            float4 main() : SV_Target { {{statement}}; return 0; }
            """);

        string[] reported = [.. diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.Id)];

        Assert.Equal(expected is null ? [] : [expected], reported);
    }

    /// <summary>
    /// <c>vector&lt;T, N&gt;</c> の仮引数からの推論を、DXC と同じに扱うことを検証する (G10)。
    /// </summary>
    /// <param name="statement">main の中に書く文。</param>
    /// <param name="expected">報告されるはずの規則。正しいなら <see langword="null"/>。</param>
    [Theory]
    [InlineData("float a = Sum3(f3)", null)]
    [InlineData("int a = Sum3(i3)", null)]
    [InlineData("float a = Sum3(f4)", "HL0345")]
    [InlineData("float a = Sum3(k)", "HL0345")]
    [InlineData("float a = Sum3(m)", "HL0345")]
    [InlineData("float3 a = Scale(f3, k)", null)]
    [InlineData("float3 a = Scale(f3, 2.0f)", null)]
    [InlineData("float3 a = Scale(f3, 2)", "HL0345")]
    [InlineData("float3 a = Scale(f3, i)", "HL0345")]
    [InlineData("float4 a = Scale(f4, k)", null)]
    [InlineData("float3 a = Scale(h3, k)", "HL0345")]
    [InlineData("float a = Dot(f3, f3)", null)]
    [InlineData("float a = Dot(f3, f4)", "HL0345")]
    [InlineData("float a = Dot(f3, i3)", "HL0345")]
    [InlineData("float a = Dot(f3, h3)", "HL0345")]
    [InlineData("float a = Sum3(f3).x", null)]
    [InlineData("float a = Scale(f3, k).w", "CF0312")]
    [InlineData("float a = Sum3(f3).y", "CF0312")]
    [InlineData("float a = Ones(f3).z", null)]
    [InlineData("float a = Ones(f3).w", "CF0312")]
    [InlineData("float a = Ones(i3).x", "HL0345")]
    public void vectorの仮引数からの推論をDXCと同じに扱う(string statement, string? expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze($$"""
            template<typename T> T Sum3(vector<T, 3> v) { return v.x + v.y + v.z; }
            template<typename T, int N> vector<T, N> Scale(vector<T, N> v, T s) { return v * s; }
            template<typename T, int N> T Dot(vector<T, N> a, vector<T, N> b) { return dot(a, b); }
            template<int N> vector<float, N> Ones(vector<float, N> v) { return v; }
            cbuffer C { float k; int i; float3 f3; float4 f4; int3 i3; half3 h3; float3x3 m; };
            float4 main() : SV_Target { {{statement}}; return 0; }
            """);

        string[] reported = [.. diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.Id)];

        Assert.Equal(expected is null ? [] : [expected], reported);
    }

    [Fact]
    public void 形の合わない実引数の文に仮引数の形を書く()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            template<typename T> T Sum3(vector<T, 3> v) { return v.x + v.y + v.z; }
            cbuffer C { float4 f4; };
            float4 main() : SV_Target { return Sum3(f4); }
            """);

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0345");
        Assert.Equal(
            "template の関数 'Sum3' の型引数を推論できません。第 1 引数 (float4) は仮引数 vector<T, 3> の形と一致しません。仮引数の形に合う値を渡してください。",
            diagnostic.GetMessage());
    }

    [Fact]
    public void 推論の食い違いの文に両方の型を書く()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            template<typename T> T MaxOf(T a, T b) { return max(a, b); }
            cbuffer C { float k; half h; };
            float4 main() : SV_Target { return MaxOf(k, h) + MaxOf(1.0, k); }
            """);

        string[] messages = [.. diagnostics.Where(d => d.Id == "HL0345").Select(d => d.GetMessage())];

        Assert.Equal(2, messages.Length);
        Assert.Equal(
            "template の関数 'MaxOf' の型引数 T を推論できません。第 1 引数 (float) と第 2 引数 (half) で T の型が一致しません。"
            + "実引数の型を揃えるか、MaxOf<float>(...) のように型引数を書いてください。",
            messages[0]);
        Assert.Equal(
            "template の関数 'MaxOf' の型引数 T を推論できません。第 1 引数 (1.0) と第 2 引数 (float) で T の型が一致しません。"
            + "1.0 のように f の付かない小数は、float とは別の型として推論されます。1.0f と書くか、MaxOf<float>(...) のように型引数を書いてください。",
            messages[1]);
    }

    [Fact]
    public void 条件の中の宣言と呼び出しでは推論の食い違いを報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            #if defined(USE_HALF)
            template<typename T> T MaxOf(T a, T b) { return max(a, b); }
            #endif
            cbuffer C { float k; half h; };
            float4 main() : SV_Target
            {
            #if defined(USE_HALF)
                return MaxOf(k, h);
            #else
                return k;
            #endif
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0345");
    }

    [Fact]
    public void 呼び出しの案内にtemplateの関数を出す()
    {
        const string Code = """
            template<typename T, typename U>
            T Mix(T a, U b, float t) { return a; }

            float4 main(float4 p : P) : SV_Target { return Mix(p, 1.0f, 0.5f); }
            """;

        SourceText text = SourceText.From(Code, "test.hlsl");
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [HlslEnvironment.WholeFile(text)], text, new HlslEnvironment(), ShaderSourceInfo.None);

        LanguageServer.SignatureHelp? help = LanguageServer.SignatureHelpBuilder.Build(
            compilation, Code.IndexOf("1.0f", StringComparison.Ordinal));

        Assert.NotNull(help);
        Assert.Equal("template<T, U> T Mix(T a, U b, float t)", Assert.Single(help.Value.Signatures).Label);
        Assert.Equal(1, help.Value.ActiveParameter);
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
