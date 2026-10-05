using System.Collections.Immutable;
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
        HlslCompilationUnit unit = HlslEnvironment.WholeFile(text) with { AllowsExternalDefinitions = external };

        ImmutableArray<Diagnostic> diagnostics = Analyze(text, unit);

        Assert.Equal(reported, diagnostics.Any(d => d.Id == "HL0311"));
    }

    private static ImmutableArray<Diagnostic> Analyze(string code)
    {
        SourceText text = SourceText.From(code, "test.hlsl");
        return Analyze(text, HlslEnvironment.WholeFile(text));
    }

    private static ImmutableArray<Diagnostic> Analyze(SourceText text, HlslCompilationUnit unit)
    {
        ShaderCompilation compilation = ShaderCompilation.Create(text, [unit], text, new HlslEnvironment(), ShaderSourceInfo.None);
        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }
}
