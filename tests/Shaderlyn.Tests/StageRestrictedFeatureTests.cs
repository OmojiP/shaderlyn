using System.Text.Json;
using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// HLSL の、そのステージで使用できない機能 (HL0356) と GetAttributeAtVertex の実引数 (HL0355) の検証。
/// </summary>
/// <remarks>
/// 報告する例は、FXC (Windows SDK 10.0.26100.0) か DXC 1.8 がエラーにすることを確かめたもの。報告しない例は、そのコンパイラが通すもの。
/// Slang の SG0364・SG0365 は <see cref="SlangRuleTests"/> で確かめている。
/// </remarks>
public sealed class StageRestrictedFeatureTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), $"shaderlyn-stage-{Guid.NewGuid():N}");

    public StageRestrictedFeatureTests() => Directory.CreateDirectory(_workDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }
    }

    private async Task<string> AnalyzeAsync(string fileName, string code, params string[] extraArgs)
    {
        string path = Path.Combine(_workDirectory, fileName);
        File.WriteAllText(path, code);
        StringWriter output = new();
        StringWriter error = new();
        await Program.RunAsync([path, "--format", "json", .. extraArgs], BuiltInAnalyzers.All, output, error);
        Assert.True(error.ToString().Length == 0, error.ToString());
        return output.ToString();
    }

    /// <summary>そのルールの文を取り出す。</summary>
    private static string[] Reports(string output, string id)
    {
        using JsonDocument document = JsonDocument.Parse(output);
        return
        [
            .. document.RootElement.GetProperty("diagnostics").EnumerateArray()
                .Where(d => d.GetProperty("id").GetString() == id)
                .Select(d => d.GetProperty("message").GetString() ?? string.Empty),
        ];
    }

    /// <summary>頂点シェーダーとピクセルシェーダーの本体を入れた Unity の .shader。</summary>
    private static string UnityShader(string vertexBody, string fragmentBody, string pragmas = "") => $$"""
        Shader "Test/Stage"
        {
            SubShader
            {
                Pass
                {
                    CGPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag
                    {{pragmas}}
                    sampler2D _MainTex;
                    Texture2D _Tex;
                    SamplerState sampler_Tex;
                    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
                    v2f vert(float4 p : POSITION, float2 uv : TEXCOORD0) { v2f o; o.pos = p; o.uv = uv; {{vertexBody}} return o; }
                    float4 frag(v2f i) : SV_Target { float4 c = 0; {{fragmentBody}} return c; }
                    ENDCG
                }
            }
        }
        """;

    [Theory]
    [InlineData("o.pos.y += tex2D(_MainTex, uv).r;", "tex2D (LOD を指定しないサンプリング) は頂点シェーダーでは使用できません", "tex2Dlod を使用してください")]
    [InlineData("o.pos += _Tex.Sample(sampler_Tex, uv);", "Sample (LOD を指定しないサンプリング) は頂点シェーダーでは使用できません", "SampleLevel を使用してください")]
    [InlineData("o.pos.x += ddx(uv.x);", "ddx (微分) は頂点シェーダーでは使用できません", "FXC がコンパイルの誤りにします")]
    [InlineData("clip(uv.x);", "clip はピクセルシェーダーでしか使用できません", "エントリポイント 'vert' から届きます")]
    public async Task Unityの頂点シェーダーで使用できない機能を報告する(string vertexBody, string expected, string detail)
    {
        string output = await AnalyzeAsync("Test.shader", UnityShader(vertexBody, string.Empty), "--no-config");

        string report = Assert.Single(Reports(output, "HL0356"));
        Assert.Contains(expected, report, StringComparison.Ordinal);
        Assert.Contains(detail, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ピクセルシェーダーの機能と_LODを指定するサンプリングは報告しない()
    {
        string output = await AnalyzeAsync("Test.shader", UnityShader(
            "o.pos.y += tex2Dlod(_MainTex, float4(uv, 0, 0)).r + _Tex.SampleLevel(sampler_Tex, uv, 0).r + tex2D(_MainTex, uv, float2(0, 0), float2(0, 0)).r;",
            "clip(i.uv.x); if (i.uv.y < 0) discard; c = tex2D(_MainTex, i.uv) + _Tex.Sample(sampler_Tex, i.uv) + ddx(i.uv.x);"), "--no-config");

        Assert.Empty(Reports(output, "HL0356"));
    }

    [Fact]
    public async Task 頂点シェーダーから間接に届く関数の中の使用を報告する()
    {
        string output = await AnalyzeAsync("Test.shader", """
            Shader "Test/Helper"
            {
                SubShader
                {
                    Pass
                    {
                        CGPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        sampler2D _MainTex;
                        float4 Height(float2 uv) { return tex2D(_MainTex, uv); }
                        float4 Displace(float4 p, float2 uv) { return p + Height(uv); }
                        float4 vert(float4 p : POSITION, float2 uv : TEXCOORD0) : SV_POSITION { return Displace(p, uv); }
                        float4 frag(float2 uv : TEXCOORD0) : SV_Target { return Height(uv); }
                        ENDCG
                    }
                }
            }
            """, "--no-config");

        string report = Assert.Single(Reports(output, "HL0356"));
        Assert.Contains("エントリポイント 'vert' から届きます", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unityのコンピュートシェーダーでは_FXCの場合に微分とサンプリングを報告する()
    {
        string output = await AnalyzeAsync("A.compute", """
            #pragma kernel CSMain
            Texture2D<float4> _Tex;
            SamplerState sampler_Tex;
            RWTexture2D<float4> _Out;
            [numthreads(8, 8, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID)
            {
                _Out[id.xy] = _Tex.Sample(sampler_Tex, id.xy / 8.0) + _Tex.SampleLevel(sampler_Tex, id.xy / 8.0, 0);
            }
            """, "--no-config");

        string report = Assert.Single(Reports(output, "HL0356"));
        Assert.Contains("Sample (LOD を指定しないサンプリング) はコンピュートシェーダーでは使用できません", report, StringComparison.Ordinal);
        Assert.Contains("FXC がコンパイルの誤りにします", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DXCのコンピュートシェーダーの微分は報告せず_discardは報告する()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            environment: hlsl
            hlsl:
              compiler: dxc
            units:
              - files: "*.hlsl"
                entry: main
                target: cs_6_6
            """);

        string output = await AnalyzeAsync("a.hlsl", """
            RWStructuredBuffer<float> o;
            [numthreads(4, 1, 1)]
            void main(uint3 id : SV_DispatchThreadID)
            {
                o[id.x] = ddx(o[id.x]);
                if (id.x == 0) discard;
            }
            """, "--config", Path.Combine(_workDirectory, ".shaderlyn.yaml"));

        string report = Assert.Single(Reports(output, "HL0356"));
        Assert.Contains("discard はピクセルシェーダーでしか使用できません", report, StringComparison.Ordinal);
        Assert.Contains("DXC がコンパイルの誤りにします", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DXCでnointerpolationのない入力とグローバル変数をGetAttributeAtVertexに渡すと報告する()
    {
        string output = await AnalyzeAsync("Test.shader", """
            Shader "Test/AttributeAtVertex"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        #pragma use_dxc
                        struct v2f
                        {
                            float4 pos : SV_POSITION;
                            nointerpolation float4 a : TEXCOORD0;
                            float4 b : TEXCOORD1;
                        };
                        static float4 g;
                        v2f vert(float4 p : POSITION) { v2f o; o.pos = p; o.a = p; o.b = p; return o; }
                        float4 frag(v2f i, nointerpolation float4 c : TEXCOORD2) : SV_Target
                        {
                            float4 l = i.a;
                            return GetAttributeAtVertex(i.a, 0) + GetAttributeAtVertex(c, 1) + GetAttributeAtVertex(l, 2)
                                 + GetAttributeAtVertex(i.b, 0) + GetAttributeAtVertex(g, 0);
                        }
                        ENDHLSL
                    }
                }
            }
            """, "--no-config");

        string[] reports = Reports(output, "HL0355");
        Assert.Equal(2, reports.Length);
        Assert.Contains(reports, r => r.Contains("入力 'i.b' に nointerpolation がありません", StringComparison.Ordinal));
        Assert.Contains(reports, r => r.Contains("グローバル変数 'g' を渡しています", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slangのunitsのentryとstageでエントリポイントのステージを決める()
    {
        // slangc -entry vsMain -stage vertex と同じ。[shader] がなくても、頂点シェーダーとして検査する。
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            units:
              - files: "*.slang"
                entry: vsMain
                stage: vertex
            """);

        string output = await AnalyzeAsync("a.slang", """
            float4 vsMain(uint id : SV_GSInstanceID) : SV_Position
            {
                return ddx(float4(id, 0, 0, 1));
            }
            """, "--config", Path.Combine(_workDirectory, ".shaderlyn.yaml"));

        Assert.Single(Reports(output, "CF0302"));
        Assert.Single(Reports(output, "SG0365"));
    }

    [Fact]
    public async Task unitsのstageに知らない名前を書くと設定の誤りにする()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            units:
              - files: "*.slang"
                entry: main
                stage: vertexx
            """);

        string output = await AnalyzeAsync("a.slang", "void main() {}\n", "--config", Path.Combine(_workDirectory, ".shaderlyn.yaml"));

        Assert.Contains(Reports(output, "TOOL0003"), r => r.Contains("stage 'vertexx' を解釈できません", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FXCでコンパイルされるコードのGetAttributeAtVertexは見ない()
    {
        string output = await AnalyzeAsync("Test.shader", UnityShader(string.Empty, "c = GetAttributeAtVertex(i.uv.xyxy, 0);"), "--no-config");

        Assert.Empty(Reports(output, "HL0355"));
    }
}
