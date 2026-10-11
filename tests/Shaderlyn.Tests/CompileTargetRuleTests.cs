using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Semantics.Environments;
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

        Assert.Equal("型 int64_t は 環境：dx11 (FXC)・dx12 (FXC) では使えません。", diagnostic.GetMessage());
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
        Assert.Equal("enum は 環境：dx11 (FXC) では使えません。", diagnostic.GetMessage());
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

        Assert.Equal("型 float16_t は 環境：dx12 (DXC (HLSL 2018)) では使えません。", diagnostic.GetMessage());
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
            "型 float16_t は 環境：UNITY_DEVICE_SUPPORTS_NATIVE_16BIT が無効な構成の dx11 (FXC)・UNITY_DEVICE_SUPPORTS_NATIVE_16BIT が無効な構成の dx12 (FXC) では使えません。",
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
    [InlineData("#pragma require int64", "6.6", "SL1050", "require int64 は DXC が要るので、このシェーダーは DX11 では使えません (コンパイルされないか、作られても使われません)。DX11 でも使うなら、キーワードで分けてください (require int64 : キーワード)。")]
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
    public void require_native16bitのパスで定数バッファのhalfを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            "#pragma require native16bit",
            "return _BaseColor + _Other;",
            "6.3",
            "cbuffer UnityPerMaterial { half4 _BaseColor; float4 _Other; };");

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "SL1052");
        Assert.Equal(
            "定数バッファ 'UnityPerMaterial' の _BaseColor は half4 ですが、dx12 (DXC (HLSL 2018、16 ビットの型)) では 16 ビットの型になり、"
            + "Unity が書き込んだ値を正しく読めません。float4 で宣言し、使うところで変換してください。",
            diagnostic.GetMessage());
    }

    [Fact]
    public void NATIVE_16BITが有効な構成に届く定数バッファのhalfを報告する()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            "#pragma multi_compile _ UNITY_DEVICE_SUPPORTS_NATIVE_16BIT",
            "return _BaseColor;",
            "6.3",
            "cbuffer UnityPerMaterial { min16float4 _BaseColor; };");

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "SL1052");
        Assert.Contains("UNITY_DEVICE_SUPPORTS_NATIVE_16BIT が有効な構成の dx12", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#pragma use_dxc", "cbuffer UnityPerMaterial { half4 _BaseColor; };")]
    [InlineData("#pragma require native16bit", "cbuffer UnityPerMaterial { float4 _BaseColor; };")]
    [InlineData("", "cbuffer UnityPerMaterial { half4 _BaseColor; };")]
    public void 十六ビットの型を有効にしないか定数バッファがfloatなら報告しない(string pragma, string declarations)
        => Assert.DoesNotContain(Analyze(pragma, "return _BaseColor;", "6.3", declarations), d => d.Id == "SL1052");

    private const string LightInterface = "interface ILight { float3 Get(); }; ILight g_light;";

    [Fact]
    public void グローバルなinterfaceの変数を報告する()
    {
        Diagnostic diagnostic = Assert.Single(Analyze("", "return 1;", "6.3", LightInterface), d => d.Id == "HL0003");

        Assert.Equal("interface 型 ILight のグローバル変数 は 環境：dx11 (FXC)・dx12 (FXC) では使えません。", diagnostic.GetMessage());
    }

    [Fact]
    public void シェーダーモデル5のFXCではinterfaceの変数を報告しない()
        => Assert.DoesNotContain(Analyze("#pragma target 5.0", "return 1;", "6.3", LightInterface), d => d.Id == "HL0003");

    [Fact]
    public void DXCに届くinterfaceの変数を報告する()
    {
        UnityOptions options = new() { EditorVersion = "6.6", GraphicsApis = [UnityGraphicsApi.Direct3D12] };
        Diagnostic diagnostic = Assert.Single(Analyze("#pragma target 5.0\n#pragma use_dxc", "return 1;", options, LightInterface), d => d.Id == "HL0003");

        Assert.Contains("dx12 (DXC (HLSL 2018))", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HlslCompiler.Dxc, true)]
    [InlineData(HlslCompiler.Fxc, false)]
    public void 汎用のHLSLではDXCのときinterfaceの変数を報告する(HlslCompiler compiler, bool expected)
    {
        SourceText text = SourceText.From(LightInterface + " float4 main() : SV_Target { return 1; }", "test.hlsl");
        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [HlslEnvironment.WholeFile(text)],
            text,
            new HlslEnvironment(new HlslEnvironmentOptions { Compiler = compiler }),
            ShaderSourceInfo.None);

        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Assert.Equal(expected, diagnostics.Any(d => d.Id == "HL0003"));
    }

    [Theory]
    [InlineData(HlslCompiler.Dxc, 3)]
    [InlineData(HlslCompiler.Fxc, 0)]
    public void 汎用のHLSLではDXCのときDX9の書き方とエントリのuniform引数を報告する(HlslCompiler compiler, int expected)
    {
        SourceText text = SourceText.From("""
            sampler2D s2;
            float4 main(float2 uv : TEXCOORD0, uniform float k) : SV_Target { return tex2D(s2, uv) * k; }
            """, "test.hlsl");
        ShaderCompilation compilation = ShaderCompilation.Create(
            text,
            [HlslEnvironment.WholeFile(text) with { EntryPoints = [new HlslEntryPoint("main", "test")] }],
            text,
            new HlslEnvironment(new HlslEnvironmentOptions { Compiler = compiler }),
            ShaderSourceInfo.None);

        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Assert.Equal(expected, diagnostics.Count(d => d.Id == "HL0003"));
    }

    [Fact]
    public void UnityのDXCはDX9の書き方を受け付けるがエントリのuniform引数を報告する()
    {
        UnityOptions options = new() { EditorVersion = "6.6", GraphicsApis = [UnityGraphicsApi.Direct3D12] };
        string source = """
            Shader "Test"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #pragma vertex vert
                        #pragma fragment frag
                        #pragma use_dxc
                        sampler2D _MainTex;
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        float4 frag(float4 p : SV_POSITION, uniform float k) : SV_Target { return tex2D(_MainTex, p.xy) * k; }
                        ENDHLSL
                    }
                }
            }
            """;
        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));
        ShaderCompilation compilation = UnityCompilation.Create(text, ShaderLabSyntaxTree.Parse(text), new SemanticsOptions(), options);
        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0003");
        Assert.Equal("エントリ関数 frag の uniform 引数 k は 環境：dx12 (DXC (HLSL 2018)) では使えません。", diagnostic.GetMessage());
    }

    /// <summary>
    /// <c>_</c> の無い multi_compile のデバイスのキーワードは常に有効なので、キーワードが無効な構成 (FXC) を考えないことを検証する。
    /// </summary>
    [Fact]
    public void 常に有効なデバイスのキーワードではFXCの構成を考えない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            "#pragma multi_compile UNITY_DEVICE_SUPPORTS_NATIVE_16BIT",
            "Mode m = ModeA; float16_t v = 1; return m == ModeB ? v : 0;",
            "6.3",
            "enum Mode { ModeA, ModeB };");

        Assert.DoesNotContain(diagnostics, d => d.Id is "HL0003" or "HL0331");
    }

    [Fact]
    public void コンピュートシェーダーの常に有効なWAVEのキーワードではFXCの構成を考えない()
    {
        SourceText text = SourceText.From("""
            #pragma kernel CSMain
            #pragma multi_compile UNITY_DEVICE_SUPPORTS_WAVE_32
            enum Mode { ModeA, ModeB };
            RWStructuredBuffer<float> Out;
            [numthreads(8,1,1)]
            void CSMain(uint3 id : SV_DispatchThreadID) { Mode m = ModeB; Out[id.x] = m == ModeB ? 1 : 0; }
            """, Path.Combine("Assets", "K.compute"));
        ShaderCompilation compilation = UnityCompilation.CreateForHlsl(text, new SemanticsOptions(), new UnityOptions { EditorVersion = "6.3" });
        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Assert.DoesNotContain(diagnostics, d => d.Id is "HL0003" or "HL0331");
    }

    /// <summary>
    /// コンパイラを切り替えるデバイスのキーワードは、条件に書かなくても HL0331 にしないことを検証する。
    /// .shader の WAVE のキーワードはコンパイラを切り替えないので、使っていなければ報告する。
    /// </summary>
    [Theory]
    [InlineData("UNITY_DEVICE_SUPPORTS_NATIVE_16BIT", false)]
    [InlineData("UNITY_DEVICE_SUPPORTS_INLINE_RAY_TRACING", false)]
    [InlineData("UNITY_DEVICE_SUPPORTS_WAVE_ANY", true)]
    public void コンパイラを切り替えるキーワードは条件に無くても使われている(string keyword, bool expected)
        => Assert.Equal(expected, Analyze($"#pragma multi_compile _ {keyword}", "return 1;", "6.3").Any(d => d.Id == "HL0331"));

    /// <summary>カーネルの無いコンピュートシェーダーには、頂点・フラグメントではなく #pragma kernel を求めることを検証する (HL0302)。</summary>
    [Fact]
    public void カーネルの無いコンピュートシェーダーにはkernelを求める()
    {
        SourceText text = SourceText.From("float F() { return 1; }\n", Path.Combine("Assets", "K.compute"));
        ShaderCompilation compilation = UnityCompilation.CreateForHlsl(text, new SemanticsOptions(), new UnityOptions());
        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        Diagnostic diagnostic = Assert.Single(diagnostics, d => d.Id == "HL0302");
        Assert.Equal("コードブロックに #pragma kernel が存在しません。", diagnostic.GetMessage());
    }

    /// <summary><c>sampler_state { ... }</c> を、宣言されていない名前として報告しないことを検証する。</summary>
    [Fact]
    public void sampler_stateを名前として報告しない()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(
            "",
            "return tex2D(_ProbeSampler, float2(0.5, 0.5));",
            "6.3",
            "Texture2D _ProbeTexture; sampler2D _ProbeSampler = sampler_state { Texture = <_ProbeTexture>; };");

        Assert.DoesNotContain(diagnostics, d => d.Id is "HL0310" or "HL0001");
    }

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
