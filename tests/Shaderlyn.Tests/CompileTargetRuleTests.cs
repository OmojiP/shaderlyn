using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;
using Shaderlyn.Unity;

namespace Shaderlyn.Tests;

/// <summary>
/// コードが届くコンパイル先 (API と方言) で使えない構文と型の検査 (HL0003、<see cref="Rules.CompileTargetAnalyzer"/>) の検証。
/// </summary>
public sealed class CompileTargetRuleTests
{
    [Fact]
    public void FXCのパスで64ビットの型を報告する()
    {
        Diagnostic diagnostic = Assert.Single(Analyze("", "int64_t v = 1; return (float)v;", "6.3"), d => d.Id == "HL0003");

        Assert.Equal("型 int64_t は dx11 (FXC)・dx12 (FXC) では使えません。", diagnostic.GetMessage());
    }

    [Theory]
    [InlineData("float32_t3 v = 1; return v.x;", "型 float32_t3")]
    [InlineData("vector<uint32_t, 2> v = 1; return v.x;", "型 uint32_t")]
    [InlineData("float16_t v = 1; return v;", "型 float16_t")]
    public void FXCのパスで明示のビット幅の型を報告する(string body, string expected)
    {
        Diagnostic diagnostic = Assert.Single(Analyze("", body, "6.3"), d => d.Id == "HL0003");

        Assert.StartsWith(expected, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void use_dxcのパスは6_6でDX11がFXCなのでenumを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("#pragma use_dxc", "Mode m = ModeA; return m == ModeB ? 1 : 0;", "6.6", "enum Mode { ModeA, ModeB };");

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0003");
        Assert.Equal("enum は dx11 (FXC) では使えません。", diagnostic.GetMessage());
    }

    [Fact]
    public void use_dxcのパスは6_5以前ではDX11が使えないのでenumを報告しない()
        => Assert.DoesNotContain(
            Analyze("#pragma use_dxc", "Mode m = ModeA; return m == ModeB ? 1 : 0;", "6.3", "enum Mode { ModeA, ModeB };"),
            d => d.Id == "HL0003");

    [Fact]
    public void require_native16bitのパスでは16ビットの型を使える()
        => Assert.DoesNotContain(
            Analyze("#pragma require native16bit", "float16_t v = 1; int64_t w = 2; return v + (float)w;", "6.3"),
            d => d.Id == "HL0003");

    [Fact]
    public void use_dxcだけでは16ビットの型を使えない()
    {
        UnityOptions options = new() { EditorVersion = "6.3", GraphicsApis = [UnityGraphicsApi.Direct3D12] };
        Diagnostic diagnostic = Assert.Single(Analyze("#pragma use_dxc", "float16_t v = 1; return v;", options), d => d.Id == "HL0003");

        Assert.Equal("型 float16_t は dx12 (DXC (HLSL 2018)) では使えません。", diagnostic.GetMessage());
    }

    [Fact]
    public void キーワードが有効な構成にだけある16ビットの型は報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            "#pragma multi_compile _ UNITY_DEVICE_SUPPORTS_NATIVE_16BIT",
            "#ifdef UNITY_DEVICE_SUPPORTS_NATIVE_16BIT\nfloat16_t v = 1;\n#else\nfloat v = 1;\n#endif\nreturn v;",
            "6.3");

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0003");
    }

    [Fact]
    public void キーワードが無効な構成に届く16ビットの型を報告する()
    {
        Diagnostic diagnostic = Assert.Single(
            Analyze("#pragma multi_compile _ UNITY_DEVICE_SUPPORTS_NATIVE_16BIT", "float16_t v = 1; return v;", "6.3"),
            d => d.Id == "HL0003");

        Assert.Equal(
            "型 float16_t は UNITY_DEVICE_SUPPORTS_NATIVE_16BIT が無効な構成の dx11 (FXC)・UNITY_DEVICE_SUPPORTS_NATIVE_16BIT が無効な構成の dx12 (FXC) では使えません。",
            diagnostic.GetMessage());
    }

    [Fact]
    public void FXCだけのパスでは構造体の増減を何もしないと警告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("", "P a = (P)0; a++; return a.x;", "6.3", "struct P { float x; };");

        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0346");
        Assert.Single(diagnostics, d => d.Id == "HL0348");
    }

    [Fact]
    public void DXCに届く構造体の増減はAPIを添えて報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("#pragma use_dxc", "P a = (P)0; a++; return a.x;", "6.6", "struct P { float x; };");

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0346");
        Assert.Contains("は dx12 (DXC (HLSL 2018)) では使えません", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0348");
    }

    [Theory]
    [InlineData("#pragma use_dxc d3d12", "6.6", "SL1050", "d3d12 は、どのバージョンの Unity でも無視されます")]
    [InlineData("#pragma use_dxc dx12", "6.3", "SL1050", "Unity 6.5 以前は use_dxc dx12 を無視します")]
    [InlineData("#pragma use_dxc d3d11", "6.6", "SL1051", "非推奨")]
    [InlineData("#pragma use_dxc d3d11", "6.3", "SL1050", "DX11 では使えなくなります")]
    [InlineData("#pragma use_dxc", "6.3", "SL1050", "API を書かない use_dxc で DX11 も DXC")]
    [InlineData("#pragma require int64", "6.6", "SL1050", "require int64 は DXC が要るので")]
    [InlineData("#pragma require inlineraytracing", "2022.3", "SL1050", "知らないので無視します")]
    [InlineData("#pragma require inlineraytracing", "6.3", "SL1051", "このシェーダーをコンパイルしません")]
    [InlineData("#pragma never_use_dxc|#pragma require wavebasic", "6.6", "SL1050", "6.5 以前は FXC、6.6 からは DXC")]
    public void コンパイラの指定を対象のAPIとバージョンに照らして報告する(string pragma, string version, string id, string expected)
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(pragma.Replace("|", "\n", StringComparison.Ordinal), "return 1;", version);

        Assert.Contains(diagnostics, d => d.Id == id && d.GetMessage().Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("#pragma use_dxc", null)]
    [InlineData("#pragma use_dxc dx12", null)]
    [InlineData("#pragma use_dxc", "6.6")]
    [InlineData("#pragma require int64 : _USE_INT64", "6.3")]
    public void 問題の無いコンパイラの指定は報告しない(string pragma, string? version)
        => Assert.DoesNotContain(
            Analyze(pragma, "return 1;", new UnityOptions { EditorVersion = version }),
            d => d.Id is "SL1050" or "SL1051");

    [Fact]
    public void DX11を対象にしなければrequireを報告しない()
        => Assert.DoesNotContain(
            Analyze("#pragma require int64", "return 1;", new UnityOptions { EditorVersion = "6.3", GraphicsApis = [UnityGraphicsApi.Direct3D12] }),
            d => d.Id is "SL1050");

    private static ImmutableArray<Diagnostic> Analyze(string pragma, string body, string version, string declarations = "")
        => Analyze(pragma, body, new UnityOptions { EditorVersion = version }, declarations);

    private static ImmutableArray<Diagnostic> Analyze(string pragma, string body, UnityOptions unity, string declarations = "")
    {
        string source = $$"""
            Shader "Test"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        {{pragma}}
                        {{declarations}}
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        float4 frag() : SV_Target
                        {
                        {{body}}
                        }
                        ENDHLSL
                    }
                }
            }
            """;

        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));
        ShaderCompilation compilation = UnityCompilation.Create(text, ShaderLabSyntaxTree.Parse(text), new SemanticsOptions(), unity);

        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }
}
