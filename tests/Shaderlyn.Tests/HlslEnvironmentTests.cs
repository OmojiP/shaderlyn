using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;
using Xunit.Abstractions;

namespace Shaderlyn.Tests;

/// <summary>
/// 汎用の HLSL 環境 (<see cref="HlslEnvironment"/>) の検証。DXC で直接コンパイルする構成を想定する。
/// </summary>
public sealed class HlslEnvironmentTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData("ps_6_6", HlslShaderStage.Pixel, 6, 6)]
    [InlineData("cs_5_0", HlslShaderStage.Compute, 5, 0)]
    [InlineData("lib_6_3", HlslShaderStage.Library, 6, 3)]
    [InlineData("ms_6_5", HlslShaderStage.Mesh, 6, 5)]
    [InlineData("as_6_5", HlslShaderStage.Amplification, 6, 5)]
    public void ターゲットを読む(string text, HlslShaderStage stage, int major, int minor)
    {
        Assert.True(HlslShaderTarget.TryParse(text, out HlslShaderTarget? target));
        Assert.Equal(new HlslShaderTarget(stage, major, minor), target);
        Assert.Equal(text, target.Value.ToString());
    }

    [Theory]
    [InlineData("ps_6")]
    [InlineData("xs_6_0")]
    [InlineData("ps_6_x")]
    [InlineData("")]
    public void ターゲットでないものは読まない(string text)
        => Assert.False(HlslShaderTarget.TryParse(text, out _));

    [Fact]
    public void DXCと同じくシェーダーモデル6未満は60として定義する()
    {
        ImmutableDictionary<string, string> macros =
            new HlslShaderTarget(HlslShaderStage.Pixel, 5, 0).CreateDxcMacros(Hlsl.Parsing.HlslLanguageVersion.Hlsl2021);

        Assert.Equal("0", macros["__SHADER_TARGET_STAGE"]);
        Assert.Equal("6", macros["__SHADER_TARGET_MAJOR"]);
        Assert.Equal("0", macros["__SHADER_TARGET_MINOR"]);
        Assert.Equal("2021", macros["__HLSL_VERSION"]);
        Assert.Equal("13", macros["__SHADER_STAGE_MESH"]);
    }

    [Fact]
    public void 十六ビットの型を使う設定でだけ__HLSL_ENABLE_16_BITを定義する()
    {
        // 16 ビットの型を使うときだけ通る分岐に誤りを置く。
        const string code = """
            float4 main() : SV_Target
            {
            #if __HLSL_ENABLE_16_BIT
                float3 c = 1;
                return c.w;
            #else
                return 0;
            #endif
            }
            """;

        HlslEnvironment enabled = new(new HlslEnvironmentOptions { Enable16BitTypes = true });

        Assert.Contains(Analyze(code, Unit(code, "ps_6_2"), environment: enabled), d => d.Id == "HL0312");
        Assert.DoesNotContain(Analyze(code, Unit(code, "ps_6_2")), d => d.Id == "HL0312");
    }

    [Fact]
    public void ターゲットのマクロで分岐を選ぶ()
    {
        // ピクセルシェーダーのときだけ誤りがある。コンピュートのターゲットでは通らない分岐。
        const string code = """
            #if __SHADER_TARGET_STAGE == __SHADER_STAGE_PIXEL && __SHADER_TARGET_MAJOR >= 6
            float4 main() : SV_Target { float3 c = 1; return c.w; }
            #else
            [numthreads(8, 8, 1)] void main() { }
            #endif
            """;

        Assert.Single(Analyze(code, Unit(code, "ps_6_6")), d => d.Id == "HL0312");
        Assert.DoesNotContain(Analyze(code, Unit(code, "cs_6_6")), d => d.Id == "HL0312");
    }

    [Fact]
    public void 設定で指定したエントリポイントが無ければ単位の先頭に報告する()
    {
        const string code = """
            float4 MainPs() : SV_Target { return 1; }
            """;

        HlslCompilationUnit unit = Unit(code, "ps_6_6") with
        {
            EntryPoints = [new HlslEntryPoint("MainPS", "設定ファイルの entry")],
        };

        Diagnostic diagnostic = Assert.Single(Analyze(code, unit), d => d.Id == "HL0301");
        Assert.Equal("設定ファイルの entry が指定する関数 'MainPS' が見つかりません。", diagnostic.GetMessage());
        Assert.Equal(0, diagnostic.Location.Span.Start);
    }

    [Fact]
    public void エントリポイントがあれば報告しない()
    {
        const string code = """
            float4 MainPS() : SV_Target { return 1; }
            """;

        HlslCompilationUnit unit = Unit(code, "ps_6_6") with
        {
            EntryPoints = [new HlslEntryPoint("MainPS", "設定ファイルの entry")],
        };

        Assert.DoesNotContain(Analyze(code, unit), d => d.Id == "HL0301");
    }

    [Fact]
    public void 設定で与えた値を持つシンボルで構成を作る()
    {
        const string code = """
            #if QUALITY == 2
            float3
            #else
            float4
            #endif
            Get() { return 1; }

            float4 main() : SV_Target { return Get().w; }
            """;

        HlslCompilationUnit unit = Unit(code, "ps_6_6") with
        {
            Variants = VariantSpace.ForValues("QUALITY", [0, 1, 2]),
        };

        Assert.Single(Analyze(code, unit), d => d.Id == "HL0312");
    }

    [Fact]
    public void Unityのルールは報告しない()
    {
        // #pragma を書かない普通の DX12 のシェーダー。Unity なら HL0302 (ステージの指定が無い) になる。
        const string code = """
            cbuffer PerFrame : register(b0) { float4x4 ViewProj; };
            Texture2D<float4> Albedo : register(t0);
            SamplerState LinearSampler : register(s0);

            struct VsOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

            float4 MainPS(VsOut input) : SV_Target
            {
                return Albedo.Sample(LinearSampler, input.Uv);
            }
            """;

        HlslCompilationUnit unit = Unit(code, "ps_6_6") with
        {
            EntryPoints = [new HlslEntryPoint("MainPS", "設定ファイルの entry")],
        };

        Assert.Empty(Analyze(code, unit, "shader.hlsl"));
    }

    [Theory]
    [InlineData("ms_6_5", """
        struct Vertex { float4 Position : SV_Position; };
        [outputtopology("triangle")] [numthreads(32, 1, 1)]
        void main(uint tid : SV_GroupThreadID, out vertices Vertex verts[3], out indices uint3 tris[1])
        {
            SetMeshOutputCounts(3, 1);
            if (tid < 3) { verts[tid].Position = 0; }
            if (tid == 0) { tris[0] = uint3(0, 1, 2); }
        }
        """)]
    [InlineData("as_6_5", """
        struct Payload { uint Index; };
        groupshared Payload s_Payload;
        [numthreads(32, 1, 1)]
        void main(uint tid : SV_GroupThreadID)
        {
            s_Payload.Index = tid;
            DispatchMesh(1, 1, 1, s_Payload);
        }
        """)]
    [InlineData("ps_6_6", """
        float4 main(float2 uv : TEXCOORD0, nointerpolation uint index : MATERIAL) : SV_Target
        {
            Texture2D<float4> albedo = ResourceDescriptorHeap[index];
            SamplerState linearSampler = SamplerDescriptorHeap[0];
            float4 color = albedo.Sample(linearSampler, uv);
            return QuadAny(color.a > 0.5) ? select(color.a > 0.9, color, 0) : 0;
        }
        """)]
    [InlineData("cs_6_8", """
        RWStructuredBuffer<uint> Output : register(u0);
        [numthreads(64, 1, 1)]
        void main(uint id : SV_DispatchThreadID)
        {
            uint wave = GetGroupWaveIndex();
            uint4 mask = WaveMatch(wave);
            Barrier(GROUP_SHARED_MEMORY, GROUP_SYNC);
            Output[id] = WaveMultiPrefixSum(wave, mask) + GetGroupWaveCount();
        }
        """)]
    [InlineData("ps_6_5", """
        Texture2D<float4> Albedo : register(t0);
        SamplerState LinearSampler : register(s0);
        FeedbackTexture2D<SAMPLER_FEEDBACK_MIN_MIP> Feedback : register(u0);
        float4 main(float2 uv : TEXCOORD0) : SV_Target
        {
            Feedback.WriteSamplerFeedback(Albedo, LinearSampler, uv);
            return Albedo.Sample(LinearSampler, uv);
        }
        """)]
    public void シェーダーモデル6のコードを報告しない(string target, string code)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(code, Unit(code, target));

        Assert.Empty(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Fact]
    public void 宣言に無い下線で始まる名前をシンボルの宣言し忘れとは言わない()
    {
        // Unity なら HL0330 (_DEBUG がどこにも宣言されていない)。DX12 のエンジンでは普通のマクロである。
        const string code = """
            float4 main() : SV_Target
            {
            #ifdef _DEBUG
                return float4(1, 0, 1, 1);
            #else
                return 0;
            #endif
            }
            """;

        Assert.DoesNotContain(Analyze(code, Unit(code, "ps_6_6")), d => d.Id == "HL0330");
    }

    [Fact]
    public void HLSL2018の環境では使えない構文をその言語のバージョンで伝える()
    {
        const string code = """
            enum Mode { First, Second };
            float4 main() : SV_Target { return 1; }
            """;

        HlslEnvironment hlsl2018 = new(new HlslEnvironmentOptions { LanguageVersion = Hlsl.Parsing.HlslLanguageVersion.Hlsl2018 });

        Diagnostic diagnostic = Assert.Single(Analyze(code, Unit(code, "ps_6_6"), environment: hlsl2018), d => d.Id == "HL0003");
        Assert.Equal("enum 宣言 は HLSL 2018 では使えません。", diagnostic.GetMessage());

        // HLSL 2021 (既定) では正しい構文なので報告しない。
        Assert.DoesNotContain(Analyze(code, Unit(code, "ps_6_6")), d => d.Id == "HL0003");
    }

    [Fact]
    public void HLSL2021の組み込み関数は2018では宣言されていない()
    {
        const string code = """
            float4 main(float4 c : COLOR) : SV_Target { return select(c.a > 0.5, c, 0); }
            """;

        HlslCompilationUnit unit = Unit(code, "ps_6_6");

        Assert.DoesNotContain(Analyze(code, unit), d => d.Id == "HL0310");
        Assert.Contains(
            Analyze(code, unit, environment: new HlslEnvironment(new HlslEnvironmentOptions { LanguageVersion = Hlsl.Parsing.HlslLanguageVersion.Hlsl2018 })),
            d => d.Id == "HL0310");
    }

    /// <summary>ファイル全体を、ターゲットを指定した 1 つの単位にする。</summary>
    /// <param name="code">コード。</param>
    /// <param name="target">ターゲット。</param>
    /// <returns>単位。</returns>
    private static HlslCompilationUnit Unit(string code, string target)
    {
        Assert.True(HlslShaderTarget.TryParse(target, out HlslShaderTarget? parsed));
        return HlslEnvironment.WholeFile(SourceText.From(code, "shader.hlsl")) with { Target = parsed };
    }

    /// <summary>汎用の HLSL 環境で 1 つの単位を解析する。</summary>
    /// <param name="code">コード。</param>
    /// <param name="unit">単位。テキストはこのコードから作ったもの。</param>
    /// <param name="path">ファイルのパス。</param>
    /// <param name="environment">環境。省略すると既定の設定の汎用の HLSL 環境。</param>
    /// <returns>診断。</returns>
    private ImmutableArray<Diagnostic> Analyze(
        string code,
        HlslCompilationUnit unit,
        string path = "shader.hlsl",
        HlslEnvironment? environment = null)
    {
        SourceText text = SourceText.From(code, path);
        unit = unit with { Text = text, CodeSpan = new TextSpan(0, text.Length) };

        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [unit], text, environment ?? new HlslEnvironment(), ShaderSourceInfo.None);

        ImmutableArray<Diagnostic> diagnostics =
            new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        foreach (Diagnostic diagnostic in diagnostics)
        {
            _output.WriteLine($"{diagnostic.Location.LineSpan.Start} {diagnostic.Id} {diagnostic.Severity}: {diagnostic.GetMessage()}");
        }

        return diagnostics;
    }
}
