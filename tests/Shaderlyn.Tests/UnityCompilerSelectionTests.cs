using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;
using Shaderlyn.Unity;

namespace Shaderlyn.Tests;

/// <summary>
/// Unity がパスをどのコンパイラでコンパイルするかの判定 (<see cref="UnityCompilerSelection"/>) の検証。
/// </summary>
/// <remarks>
/// 期待値は Unity 2022.3〜6.6 の実測 (ShaderProbePack、HLSL_Compatibility_Findings.md) から取った。
/// </remarks>
public sealed class UnityCompilerSelectionTests
{
    [Theory]
    [InlineData("6000.3.0f1", UnityVersionBand.Unity2023_2To6_3)]
    [InlineData("2022.3.62f2", UnityVersionBand.Unity2022_3)]
    [InlineData("2023.2.22f1", UnityVersionBand.Unity2023_2To6_3)]
    [InlineData("2023.1", UnityVersionBand.Unity2022_3)]
    [InlineData("6.0", UnityVersionBand.Unity2023_2To6_3)]
    [InlineData("6.4", UnityVersionBand.Unity6_4To6_5)]
    [InlineData("6000.5.1f1", UnityVersionBand.Unity6_4To6_5)]
    [InlineData("6000.6.4f1", UnityVersionBand.Unity6_6OrLater)]
    [InlineData("7.0", UnityVersionBand.Unity6_6OrLater)]
    public void バージョンをまとまりに分ける(string version, UnityVersionBand expected)
    {
        Assert.True(UnityVersions.TryGetBand(version, out UnityVersionBand? band));
        Assert.Equal(expected, band);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("6")]
    public void 読めないバージョンはまとまりにしない(string version)
        => Assert.False(UnityVersions.TryGetBand(version, out _));

    [Fact]
    public void Windows向けのグラフィックスAPIを読む()
    {
        string[] lines =
        [
            "  m_BuildTargetGraphicsAPIs:",
            "  - m_BuildTarget: AndroidPlayer",
            "    m_APIs: 150000000b000000",
            "    m_Automatic: 0",
            "  - m_BuildTarget: WindowsStandaloneSupport",
            "    m_APIs: 1200000002000000",
            "    m_Automatic: 1",
        ];

        Assert.Equal(new[] { UnityGraphicsApi.Direct3D12, UnityGraphicsApi.Direct3D11 }, UnityProjectSettings.ReadWindowsGraphicsApis(lines).ToArray());
    }

    [Fact]
    public void Unity66の書き方のグラフィックスAPIを読む()
    {
        string[] lines = ["  - m_BuildTarget: WindowsStandaloneSupport", "    m_APIs: 12000000i"];

        Assert.Equal(new[] { UnityGraphicsApi.Direct3D12 }, UnityProjectSettings.ReadWindowsGraphicsApis(lines).ToArray());
    }

    [Fact]
    public void ビルドプロファイルのBackendの設定を読む()
    {
        string[] lines =
        [
            "    compilerSettings:",
            "    - graphicsAPI: 18",
            "      compilerToolchainOverride: 2",
            "    - graphicsAPI: 21",
            "      compilerToolchainOverride: 1",
        ];

        Assert.Equal(new[] { UnityGraphicsApi.Direct3D12 }, UnityProjectSettings.ReadDxcBackendApis(lines).ToArray());
    }

    [Fact]
    public void プロジェクトのフォルダーから設定を読む()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderlyn-unity-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(root, "Assets", "Settings", "Build Profiles"));
            File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.6.4f1\nm_EditorVersionWithRevision: 6000.6.4f1 (x)\n");
            File.WriteAllText(
                Path.Combine(root, "ProjectSettings", "ProjectSettings.asset"),
                "  m_BuildTargetGraphicsAPIs:\n  - m_BuildTarget: WindowsStandaloneSupport\n    m_APIs: 12000000i\n    m_Automatic: 0\n");
            File.WriteAllText(
                Path.Combine(root, "Assets", "Settings", "Build Profiles", "Windows.asset"),
                "    compilerSettings:\n    - graphicsAPI: 18\n      compilerToolchainOverride: 2\n--- !u!114 &11400000\n  m_BuildTarget: 19\n");

            UnityProjectSettings settings = UnityProjectSettings.Read(root);

            Assert.Equal("6000.6.4f1", settings.EditorVersion);
            Assert.Equal(new[] { UnityGraphicsApi.Direct3D12 }, settings.GraphicsApis.ToArray());
            Assert.Equal(new[] { UnityGraphicsApi.Direct3D12 }, settings.DxcBackendApis.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// <c>#pragma</c> と Unity のバージョンごとの判定。DX11 と DX12 を対象にする。
    /// </summary>
    /// <param name="pragmas"><c>#pragma</c> の行 (<c>|</c> で区切る)。</param>
    /// <param name="version">Unity のバージョン。</param>
    /// <param name="expected">期待するコンパイル先 (<c>|</c> で区切る)。</param>
    [Theory]
    [InlineData("", "6.3", "dx11: FXC|dx12: FXC")]
    [InlineData("use_dxc", "6.3", "dx11: DXC (HLSL 2018) (この API では使えない、6.5 以前の #pragma use_dxc は DX11 も DXC にするので、DX11 では使えない)|dx12: DXC (HLSL 2018) (#pragma use_dxc)")]
    [InlineData("use_dxc", "6.6", "dx11: FXC|dx12: DXC (HLSL 2018) (#pragma use_dxc)")]
    [InlineData("use_dxc d3d11", "6.3", "dx11: DXC (HLSL 2018) (この API では使えない、6.5 以前の #pragma use_dxc は DX11 も DXC にするので、DX11 では使えない)|dx12: DXC (HLSL 2018) (#pragma use_dxc)")]
    [InlineData("use_dxc d3d11", "6.6", "dx11: FXC|dx12: DXC (HLSL 2018) (#pragma use_dxc)")]
    [InlineData("use_dxc dx12", "6.5", "dx11: FXC|dx12: FXC")]
    [InlineData("use_dxc dx12", "6.6", "dx11: FXC|dx12: DXC (HLSL 2018) (#pragma use_dxc)")]
    [InlineData("use_dxc d3d12", "6.6", "dx11: FXC|dx12: FXC")]
    [InlineData("use_dxc vulkan", "6.3", "dx11: FXC|dx12: FXC")]
    [InlineData("require int64", "2022.3", "dx11: DXC (HLSL 2018) (この API では使えない、#pragma require int64 は DXC が要るので DX11 では使えない)|dx12: DXC (HLSL 2018) (#pragma require int64)")]
    [InlineData("require Native16Bit", "6.3", "dx11: DXC (HLSL 2018、16 ビットの型) (この API では使えない、#pragma require native16bit は DXC が要るので DX11 では使えない)|dx12: DXC (HLSL 2018、16 ビットの型) (#pragma require native16bit)")]
    [InlineData("require 2darray", "6.3", "dx11: FXC|dx12: FXC")]
    [InlineData("never_use_dxc|require int64", "6.3", "dx11: DXC (HLSL 2018) (この API では使えない、#pragma require int64 は DXC が要るので DX11 では使えない)|dx12: FXC (#pragma never_use_dxc)")]
    [InlineData("never_use_dxc|require int64", "6.6", "dx11: DXC (HLSL 2018) (この API では使えない、#pragma require int64 は DXC が要るので DX11 では使えない)|dx12: DXC (HLSL 2018) (#pragma require int64)")]
    [InlineData("never_use_dxc|use_dxc", "6.6", "dx11: FXC (#pragma never_use_dxc)|dx12: FXC (#pragma never_use_dxc)")]
    [InlineData("require inlineraytracing", "2022.3", "dx11: FXC|dx12: FXC")]
    [InlineData("require inlineraytracing", "6.3", "dx11: DXC (HLSL 2018) (この API では使えない、#pragma require inlineraytracing はコンパイルされない)|dx12: DXC (HLSL 2018) (この API では使えない、#pragma require inlineraytracing はコンパイルされない)")]
    [InlineData("require inlineraytracing", "6.4", "dx11: DXC (HLSL 2018) (この API では使えない、#pragma require inlineraytracing は DXC が要るので DX11 では使えない)|dx12: DXC (HLSL 2018) (#pragma require inlineraytracing)")]
    [InlineData("multi_compile _ UNITY_DEVICE_SUPPORTS_NATIVE_16BIT", "6.3", "dx11: FXC (UNITY_DEVICE_SUPPORTS_NATIVE_16BIT が無効な構成)|dx11: DXC (HLSL 2018、16 ビットの型) (UNITY_DEVICE_SUPPORTS_NATIVE_16BIT が有効な構成、この API では使えない、DX11 では有効にならないキーワード)|dx12: FXC (UNITY_DEVICE_SUPPORTS_NATIVE_16BIT が無効な構成)|dx12: DXC (HLSL 2018、16 ビットの型) (UNITY_DEVICE_SUPPORTS_NATIVE_16BIT が有効な構成)")]
    [InlineData("multi_compile _ UNITY_DEVICE_SUPPORTS_INLINE_RAY_TRACING", "2022.3", "dx11: FXC|dx12: FXC")]
    [InlineData("multi_compile _ UNITY_DEVICE_SUPPORTS_WAVE_ANY", "6.3", "dx11: FXC|dx12: FXC")]
    public void パスのコンパイラを決める(string pragmas, string version, string expected)
        => Assert.Equal(expected, Describe(Select(pragmas, new UnityOptions { EditorVersion = version }, isCompute: false)));

    [Fact]
    public void コンピュートシェーダーはWAVEのキーワードでDXCになる()
    {
        string actual = Describe(Select(
            "multi_compile _ UNITY_DEVICE_SUPPORTS_WAVE_ANY",
            new UnityOptions { EditorVersion = "6.3" },
            isCompute: true));

        Assert.Equal(
            "dx11: FXC (UNITY_DEVICE_SUPPORTS_WAVE_ANY が無効な構成)"
            + "|dx11: DXC (HLSL 2018) (UNITY_DEVICE_SUPPORTS_WAVE_ANY が有効な構成、この API では使えない、DX11 では有効にならないキーワード)"
            + "|dx12: FXC (UNITY_DEVICE_SUPPORTS_WAVE_ANY が無効な構成)"
            + "|dx12: DXC (HLSL 2018) (UNITY_DEVICE_SUPPORTS_WAVE_ANY が有効な構成)",
            actual);
    }

    [Fact]
    public void BackendをDXCにしたAPIはDXCになりnever_use_dxcが勝つ()
    {
        UnityOptions options = new() { EditorVersion = "6.6", DxcBackendApis = [UnityGraphicsApi.Direct3D12] };

        Assert.Equal("dx11: FXC|dx12: DXC (HLSL 2018) (Shader Compiler Backend が DXC)", Describe(Select(string.Empty, options, isCompute: false)));
        Assert.Equal(
            "dx11: FXC (#pragma never_use_dxc)|dx12: FXC (#pragma never_use_dxc)",
            Describe(Select("never_use_dxc", options, isCompute: false)));
    }

    [Fact]
    public void VulkanとMetalにはd3d11の指定が効かない()
    {
        UnityOptions options = new()
        {
            EditorVersion = "6.3",
            GraphicsApis = [UnityGraphicsApi.Vulkan, UnityGraphicsApi.Metal],
        };

        Assert.Equal("vulkan: FXC|metal: FXC", Describe(Select("use_dxc d3d11", options, isCompute: false)));
        Assert.Equal("vulkan: DXC (HLSL 2018) (#pragma use_dxc)|metal: FXC", Describe(Select("use_dxc vulkan", options, isCompute: false)));
        Assert.Equal(
            "vulkan: DXC (HLSL 2018) (#pragma use_dxc)|metal: DXC (HLSL 2018) (#pragma use_dxc)",
            Describe(Select("use_dxc", options, isCompute: false)));
    }

    [Fact]
    public void バージョンが分からなければまとまりごとの結果を並べる()
    {
        string actual = Describe(Select("use_dxc", UnityOptions.Default, isCompute: false));

        Assert.Equal(
            "dx11: DXC (HLSL 2018) (この API では使えない、6.5 以前の #pragma use_dxc は DX11 も DXC にするので、DX11 では使えない、Unity 6.5 以前)"
            + "|dx11: FXC (Unity 6.6 以降)"
            + "|dx12: DXC (HLSL 2018) (#pragma use_dxc)",
            actual);
    }

    [Fact]
    public void DXCになるパスではenumを使える()
    {
        ImmutableArray<Diagnostic> withDxc = Analyze("#pragma use_dxc", out ImmutableArray<HlslCompileTarget> targets);
        ImmutableArray<Diagnostic> withoutDxc = Analyze(string.Empty, out _);

        Assert.DoesNotContain(withDxc, d => d.Id == "HL0003");
        Assert.Contains(targets, t => t.Dialect.Compiler == HlslCompiler.Dxc && t.IsUsable);
        Assert.Contains(withoutDxc, d => d.Id == "HL0003");
    }

    [Theory]
    [InlineData("2022.3", "DXC (HLSL 2018)")]
    [InlineData("6.3", "FXC")]
    public void include_ファイルの中のuse_dxcは2022_3でだけ効く(string version, string expected)
    {
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
                        #include "Dxc.hlsl"
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        float4 frag() : SV_Target { return 1; }
                        ENDHLSL
                    }
                }
            }
            """;

        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));
        SemanticsOptions semantics = new()
        {
            IncludeResolver = new Hlsl.Preprocessing.InMemoryIncludeResolver(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Dxc.hlsl"] = "#include \"Inner.hlsl\"\n", ["Inner.hlsl"] = "#pragma use_dxc\n" }),
        };

        ShaderCompilation compilation = UnityCompilation.Create(
            text, ShaderLabSyntaxTree.Parse(text), semantics, new UnityOptions { EditorVersion = version });

        HlslCompileTarget dx12 = Assert.Single(compilation.Programs[0].Tree.CompileTargets, t => t.Platform == "dx12");
        Assert.Equal(expected, dx12.Dialect.DisplayName);
    }

    [Fact]
    public void HLSL単体のファイルはコンパイル先を決めない()
    {
        SourceText text = SourceText.From("float4 f() { return 0; }", Path.Combine("Assets", "Test.hlsl"));
        ShaderCompilation compilation = UnityCompilation.CreateForHlsl(text, new SemanticsOptions());

        Assert.All(compilation.Programs, p => Assert.Empty(p.Tree.CompileTargets));
    }

    /// <summary><c>#pragma</c> の行からコンパイル先を決める。</summary>
    private static ImmutableArray<HlslCompileTarget> Select(string pragmas, UnityOptions options, bool isCompute)
    {
        string code = string.Join("\n", pragmas.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => "#pragma " + p)) + "\nfloat4 f() { return 0; }\n";
        ImmutableArray<HlslSyntaxToken> tokens = new Hlsl.Parsing.HlslLexer(SourceText.From(code, "test.hlsl")).Lex(out _);

        return UnityCompilerSelection.Select(tokens, isCompute, options);
    }

    private static string Describe(ImmutableArray<HlslCompileTarget> targets) => string.Join("|", targets.Select(t => t.Describe()));

    /// <summary>enum を使うシェーダーを解析する。</summary>
    private static ImmutableArray<Diagnostic> Analyze(string pragma, out ImmutableArray<HlslCompileTarget> targets)
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
                        enum Mode { ModeA, ModeB };
                        float4 vert(float4 p : POSITION) : SV_POSITION { return p; }
                        float4 frag() : SV_Target { Mode m = ModeA; return m == ModeB ? 1 : 0; }
                        ENDHLSL
                    }
                }
            }
            """;

        SourceText text = SourceText.From(source, Path.Combine("Assets", "Test.shader"));
        ShaderCompilation compilation = UnityCompilation.Create(
            text, ShaderLabSyntaxTree.Parse(text), new SemanticsOptions(), new UnityOptions { EditorVersion = "6.3" });

        targets = compilation.Programs[0].Tree.CompileTargets;
        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());
    }
}
