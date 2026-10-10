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
