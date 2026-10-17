using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;

namespace Shaderlyn.Tests;

/// <summary>
/// DirectX 12 のサンプル (外部のコーパス) に出てくる書き方を、誤りにしないことの検証。
/// </summary>
/// <remarks>
/// どれも DXC がコンパイルを通す書き方である。外部のコーパス (ExternalCorpusTests) で見つけた誤検出を、ここに小さな例として残す。
/// </remarks>
public sealed class Dx12SyntaxTests
{
    [Theory]
    [InlineData("export void LogInt(int value) { }")]
    [InlineData("""
        struct Record { uint index; };
        [Shader("node")] [NodeLaunch("broadcasting")] [NodeDispatchGrid(1, 1, 1)] [NumThreads(1, 1, 1)]
        void Entry(
            DispatchNodeInputRecord<Record> input,
            [MaxRecords(2)] NodeOutput<Record> second,
            [MaxRecords(2)] [NodeArraySize(2)] NodeOutputArray<Record> third,
            [MaxRecords(1)] EmptyNodeOutput fourth,
            uint threadIndex : SV_GroupIndex)
        {
        }
        """)]
    [InlineData("""
        #define ROOT_SIG "CBV(b0), \
                          RootConstants(b1, num32bitconstants=2)"
        [RootSignature(ROOT_SIG)]
        float4 main() : SV_Target { return 1; }
        """)]
    [InlineData("""
        using namespace dx;
        float4 main() : SV_Target { return 1; }
        """)]
    [InlineData("static const float Infinity = 1.#INF;")]
    [InlineData("""
        struct [raypayload] RayPayload
        {
            float4 color : read(caller) : write(closesthit, miss);
        };
        """)]
    [InlineData("float4 f() { float4 c = 0; c += (0.1, 0.1, 0.1, 1); return c; }")]
    // Unreal Engine の HLSL_STATIC_ASSERT が DXC のときに書く形 (Shared/HLSLStaticAssert.h)。
    [InlineData("struct S { float4 a; };\n_Static_assert(sizeof(S) == 16, \"S must be 16 bytes\");")]
    [InlineData("float4 f() { _Static_assert(1 == 1, \"ok\"); static_assert(true, \"ok\"); return 1; }")]
    // 2 進数のリテラル (Unreal Engine の Nanite のシェーダー)。
    [InlineData("uint f(uint x) { return (x & 0b1000) > 0 ? 0b0111u : 0B1; }")]
    // 構造体のメソッドから、同じ名前の大域の関数を呼ぶ (Unreal Engine の PathTracingMaterialCommon.ush)。
    [InlineData("void Add(inout float a, float b) { a += b; }\nstruct S { float A; void Add(float b) { ::Add(A, b); } };")]
    // globallycoherent を付けた型への型変換 (Unreal Engine の WorkDistribution.ush)。
    [InlineData("struct Q { RWByteAddressBuffer D; globallycoherent RWByteAddressBuffer Get() { return (globallycoherent RWByteAddressBuffer)D; } };")]
    // 仮引数の型の前の struct (Unreal Engine の bend_sss_gpu.ush)。
    [InlineData("struct P { int a; };\nstatic bool F(struct P p, int2 xy) { return p.a > xy.x; }")]
    public void DXCが受け付ける書き方を構文の誤りにしない(string code)
        => HlslHarness.ParseValid(code);

    [Fact]
    public void Gatherは要素の成分を4つ集めたベクトルを返す()
    {
        // Texture2D<float3> の GatherRed は float4 (赤を 4 つ)。要素の型 (float3) ではない。
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            Texture2D<float3> Color : register(t0);
            Texture2D<uint2> Ids : register(t1);
            SamplerState Linear : register(s0);
            float4 main(float2 uv : TEXCOORD0) : SV_Target
            {
                float4 reds = Color.GatherRed(Linear, uv);
                uint4 ids = Ids.Gather(Linear, uv);
                return reds + ids;
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0350");
    }

    [Fact]
    public void 構造体と同じ名前の変数は二重宣言ではない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            struct Constants { float4 tint; };
            ConstantBuffer<Constants> Constants : register(b0);
            float4 main() : SV_Target { return Constants.tint; }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0314");
    }

    [Fact]
    public void 構造体どうしの二重宣言は報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            struct Constants { float4 tint; };
            struct Constants { float4 other; };
            float4 main() : SV_Target { return 1; }
            """);

        Assert.Contains(diagnostics, d => d.Id == "HL0314");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ほかのライブラリとリンクする単位では宣言だけの関数を報告しない(bool external, bool reported)
    {
        const string code = """
            void Helper();
            float4 main() : SV_Target { Helper(); return 1; }
            """;

        SourceText text = SourceText.From(code, "lib.hlsl");
        ShaderCompilationUnit unit = HlslEnvironment.WholeFile(text) with { AllowsExternalDefinitions = external };

        ImmutableArray<Diagnostic> diagnostics = Analyze(text, unit);

        Assert.Equal(reported, diagnostics.Any(d => d.Id == "HL0311"));
    }

    /// <summary>
    /// Unreal Engine のエンジンのシェーダーで見つけた誤検出を、小さな例として残す。
    /// </summary>
    [Fact]
    public void マクロで作った多重定義を見分ける()
    {
        // 同じマクロの本体から作った多重定義は、どれも同じ位置になる (DualPixelVectorization.ush の dpv_min)。
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            #define MV(TYPE, C) TYPE##C##x2
            #define VEC(TYPE, C) TYPE##C
            #define DEFV(TYPE, C)                 MV(TYPE, C) my_min(MV(TYPE, C) a, MV(TYPE, C) b) { return min(a, b); }                 MV(TYPE, C) my_min(MV(TYPE, C) a, VEC(TYPE, C) b) { return a; }
            #define FOR_ALL(DEF) DEF(float, 2) DEF(float, 3)
            FOR_ALL(DEFV)
            float4 main() : SV_Target
            {
                float3x2 m = 0;
                float3x2 q = my_min(m, float3(1, 2, 3));
                return q[0].xyxy;
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id is "HL0340" or "HL0341");
    }

    [Fact]
    public void マクロを変えて取り込み直したヘッダの別名の関数を見分ける()
    {
        // DoubleFloat.ush は DFConstructor を変えながら同じヘッダを取り込み、同じ位置から別の名前の関数を作る。
        SourceText text = SourceText.From("""
            #define CTOR MakeA
            #include "Ops.hlsl"
            #undef CTOR
            #define CTOR MakeB
            #include "Ops.hlsl"
            float4 main() : SV_Target { return MakeA(1) + MakeB(2); }
            """, "test.hlsl");
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            text,
            HlslEnvironment.WholeFile(text),
            new InMemoryIncludeResolver(new Dictionary<string, string>(StringComparer.Ordinal) { ["Ops.hlsl"] = "float CTOR(float x) { return x; }" }));

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0310");
    }

    [Fact]
    public void 型の分からない左辺と構造体の演算は報告しない()
    {
        // 左辺の構造体の演算子が使われるかもしれない (TLaneVector2D<float, 2>::Const(1.0) - a)。
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            template<typename T, uint N>
            struct TVec
            {
                vector<T, N> V;
                static TVec<T, N> Const(T s) { TVec<T, N> r; r.V = s; return r; }
                TVec<T, N> operator - (TVec<T, N> b) { TVec<T, N> r; r.V = V - b.V; return r; }
            };
            float4 main() : SV_Target
            {
                TVec<float, 2> a;
                a.V = 0;
                TVec<float, 2> c = TVec<float, 2>::Const(1.0) - a;
                return c.V.xyxy;
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id is "HL0346" or "HL0001");
    }

    [Theory]
    // 同じ構造体のメソッドを this. 無しで呼ぶ。大域の GetExtinction (引数 1 個) とは別のもの (PathTracingCommon.ush)。
    [InlineData("""
        float3 GetExtinction(float3 m) { return m; }
        struct S { float3 M; float3 GetExtinction() { return M; } float3 T() { return exp(-GetExtinction()); } };
        float4 main() : SV_Target { S s; s.M = 1; return s.T().xyzz; }
        """)]
    // 仮引数の関数オブジェクトを呼ぶ。大域の WritePixel (引数 4 個) とは別のもの (NaniteRasterizer.ush)。
    [InlineData("""
        struct FTri { float a; };
        void WritePixel(uint id, uint2 p, float3 c, float d) { }
        struct FWriter { void operator()(uint2 p, float3 c, FTri t) { } };
        template<typename FWritePixel> void Raster(FTri Tri, FWritePixel WritePixel) { WritePixel(uint2(0, 0), float3(0, 0, 0), Tri); }
        float4 main() : SV_Target { FWriter w; FTri t; t.a = 0; Raster(t, w); return 0; }
        """)]
    public void 大域の関数でないものの呼び出しを大域の関数と照らさない(string code)
        => Assert.DoesNotContain(Analyze(code), d => d.Id is "HL0340" or "HL0341");

    [Theory]
    // 配列の次元だけが違う多重定義 (GPUFastFourierTransformCore.ush)。
    [InlineData("void Merge(inout float a[4]) { }\nvoid Merge(inout float a[2][4]) { }\nfloat4 main() : SV_Target { return 0; }")]
    // 型引数だけが違う多重定義 (TSRShadingAnalysis.ush)。
    [InlineData("template<typename T, uint N> struct V { vector<T, N> x; };\nV<float, 3> F(V<float, 3> a) { return a; }\nV<float, 1> F(V<float, 1> a) { return a; }\nfloat4 main() : SV_Target { return 0; }")]
    public void 配列の次元や型引数の違う多重定義を重複と報告しない(string code)
        => Assert.DoesNotContain(Analyze(code), d => d.Id == "HL0314");

    private static ImmutableArray<Diagnostic> Analyze(string code)
    {
        SourceText text = SourceText.From(code, "test.hlsl");
        return Analyze(text, HlslEnvironment.WholeFile(text));
    }

    private static ImmutableArray<Diagnostic> Analyze(SourceText text, ShaderCompilationUnit unit, IIncludeResolver? resolver = null)
    {
        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [unit], text, new HlslEnvironment(), ShaderSourceInfo.None, new SemanticsOptions { IncludeResolver = resolver });
        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }
}
