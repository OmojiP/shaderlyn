using System.Collections.Immutable;
using Shaderlyn.Cli;
using Shaderlyn.Cli.Environments;
using Shaderlyn.Configuration;
using Shaderlyn.Core.Syntax;

namespace Shaderlyn.Tests;

/// <summary>
/// 汎用の HLSL 環境の設定 (<c>hlsl:</c> / <c>symbols:</c> / <c>units:</c>) と、CLI を通した解析の検証。
/// </summary>
public sealed class HlslConfigurationTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), "shaderlyn-hlsl-" + Guid.NewGuid().ToString("N"));

    public HlslConfigurationTests() => Directory.CreateDirectory(_workDirectory);

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

    [Fact]
    public void 計画に書いた設定例を読める()
    {
        ShaderlynConfiguration configuration = ParseWithoutProblems("""
            version: 1
            environment: hlsl
            hlsl:
              version: 2018
            symbols:
              - name: QUALITY
                values: [0, 1, 2]
              - name: USE_FOG
              - one-of: [MODE_A, MODE_B]
              - at-most-one: [FX_A, FX_B]
            units:
              - files: Shaders/**/*.hlsl
                entry: MainPS
                target: ps_6_6
                defines: [USE_FOO=1]
                symbols: [QUALITY]
            """);

        Assert.Equal(2018, configuration.HlslVersion);
        Assert.Equal(["QUALITY", "USE_FOG"], configuration.Symbols.Select(s => s.Name));
        Assert.Equal([0, 1, 2], configuration.Symbols[0].Values.ToArray());
        Assert.True(configuration.Symbols[1].Values.IsEmpty);
        Assert.Equal([true, false], configuration.SymbolGroups.Select(g => g.RequiresOne));

        ConfiguredUnit unit = Assert.Single(configuration.Units);
        Assert.Equal("MainPS", unit.Entry);
        Assert.Equal("ps_6_6", unit.Target);
        Assert.Equal(["USE_FOO=1"], unit.Defines.ToArray());
        Assert.Equal(["QUALITY"], unit.Symbols!.Value.ToArray());
        Assert.True(unit.Matches(Path.Combine("C:", "project", "Shaders", "Lit", "Lit.hlsl")));
        Assert.True(unit.Matches(Path.Combine("C:", "project", "Shaders", "Lit.hlsl")));
        Assert.False(unit.Matches(Path.Combine("C:", "project", "Other", "Lit.hlsl")));
        Assert.False(unit.Matches(Path.Combine("C:", "project", "Shaders", "Lit.hlsli")));
    }

    [Theory]
    [InlineData("hlsl:\n  version: 2020\n", "2018 か 2021")]
    [InlineData("symbols:\n  - name: Q\n    values: [0]\n", "2 つ以上")]
    [InlineData("symbols:\n  - name: Q\n    values: [a, b]\n", "整数")]
    [InlineData("symbols:\n  - name: Q\n  - name: Q\n", "2 回")]
    [InlineData("symbols:\n  - one-of: [A]\n", "2 つ以上")]
    [InlineData("symbols:\n  - name: Q\n    one-of: [A, B]\n", "どれか 1 つ")]
    [InlineData("symbols:\n  - name: Q\n    values: [0, 1]\n  - one-of: [Q, B]\n", "組に入れられません")]
    [InlineData("units:\n  - entry: Main\n", "files")]
    [InlineData("units:\n  - files: a.hlsl\n    target: ps66\n", "ps_6_6")]
    [InlineData("units:\n  - files: a.hlsl\n    symbols: [NOPE]\n", "'NOPE' は symbols: で宣言されていません")]
    [InlineData("units:\n  - files: a.hlsl\n    stage: pixel\n", "'stage'")]
    [InlineData("unreal:\n  virtual-paths:\n    Plugin/A: x\n", "/ で始めて")]
    [InlineData("unreal:\n  engines: x\n", "'engines'")]
    public void 書き誤りを報告する(string content, string expected)
    {
        ShaderlynConfiguration configuration = Parse(content);

        Assert.Contains(configuration.Problems, p => p.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void 単位で使うシンボルだけを構成の軸にする()
    {
        ShaderlynConfiguration configuration = ParseWithoutProblems("""
            symbols:
              - name: QUALITY
                values: [0, 1]
              - one-of: [MODE_A, MODE_B]
              - name: USE_FOG
            """);

        VariantSpace all = HlslAnalysisEnvironment.BuildVariants(configuration, null);
        Assert.Equal(
            ["MODE_A", "MODE_B", "QUALITY==0", "QUALITY==1", "USE_FOG"],
            all.Symbols.Order(StringComparer.Ordinal));
        Assert.True(all.Constraints.AreExclusive("MODE_A", "MODE_B"));
        Assert.True(all.Constraints.AreExclusive("QUALITY==0", "QUALITY==1"));

        // 組の一部だけを使う単位では、組の制約を入れない。
        VariantSpace some = HlslAnalysisEnvironment.BuildVariants(configuration, ["QUALITY", "MODE_A"]);
        Assert.Equal(["QUALITY==0", "QUALITY==1"], some.Symbols.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DX12のシェーダーを設定の単位で解析する()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            version: 1
            environment: hlsl
            symbols:
              - name: QUALITY
                values: [0, 1, 2]
            units:
              - files: Shaders/*.hlsl
                entry: MainVS
                target: vs_6_6
              - files: Shaders/*.hlsl
                entry: MainPS
                target: ps_6_6
            """);

        // ピクセルシェーダーのエントリポイントを書き間違えた。QUALITY == 2 のときだけ型を誤っている。
        Directory.CreateDirectory(Path.Combine(_workDirectory, "Shaders"));
        File.WriteAllText(Path.Combine(_workDirectory, "Shaders", "Lit.hlsl"), """
            #include "Common.hlsli"

            struct VsOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

            VsOut MainVS(float3 position : POSITION, float2 uv : TEXCOORD0)
            {
                VsOut o;
                o.Position = mul(ViewProj, float4(position, 1));
                o.Uv = uv;
                return o;
            }

            float4 MainPs(VsOut input) : SV_Target
            {
            #if QUALITY == 2
                float3 color = Albedo.Sample(LinearSampler, input.Uv).rgb;
            #else
                float4 color = Albedo.Sample(LinearSampler, input.Uv);
            #endif
                return color.a;
            }
            """);
        File.WriteAllText(Path.Combine(_workDirectory, "Shaders", "Common.hlsli"), """
            cbuffer PerFrame : register(b0) { float4x4 ViewProj; };
            Texture2D<float4> Albedo : register(t0);
            SamplerState LinearSampler : register(s0);
            """);

        (ExitCode code, string output, string error) = await RunAsync(_workDirectory);

        Assert.True(error.Length == 0, error);
        Assert.Equal(ExitCode.DiagnosticsFound, code);
        Assert.Contains("HL0301", output, StringComparison.Ordinal);
        Assert.Contains("設定ファイルの entry が指定する関数 'MainPS' が見つかりません", output, StringComparison.Ordinal);
        Assert.Contains("HL0312", output, StringComparison.Ordinal);
        Assert.Contains("QUALITY == 2", output, StringComparison.Ordinal);

        // 取り込まれる断片 (.hlsli) はフォルダーの指定では探さない。頂点シェーダーの入口は見つかる。
        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
        Assert.DoesNotContain("'MainVS'", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--profile", "urp")]
    [InlineData("--unity-project", ".")]
    [InlineData("--unity-editor", ".")]
    public async Task Unityだけのオプションをほかの環境で指定するとエラーにする(string option, string value)
    {
        File.WriteAllText(Path.Combine(_workDirectory, "a.hlsl"), "float4 main() : SV_Target { return 1; }");

        (ExitCode code, _, string error) = await RunAsync(_workDirectory, "--environment", "hlsl", option, value);

        Assert.Equal(ExitCode.ToolError, code);
        Assert.Contains($"{option} は Unity の環境でだけ使えます", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Unityの環境ではunitsを使わないことを伝える()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "units:\n  - files: a.hlsl\n");

        Assert.True(CommandLineOptions.TryParse([_workDirectory], out CommandLineOptions? options, out _));
        EffectiveSettings settings = EffectiveSettings.Create(options);

        Assert.Contains(settings.ConfigurationDiagnostics, d => d.GetMessage().Contains("Unity の環境では使いません", StringComparison.Ordinal));
    }

    [Fact]
    public void ほかの環境では設定ファイルのUnityの節を使わない()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "environment: hlsl\nunity:\n  project: .\n");

        Assert.True(CommandLineOptions.TryParse([_workDirectory], out CommandLineOptions? options, out _));
        EffectiveSettings settings = EffectiveSettings.Create(options);

        Assert.Null(options.UnityProjectPath);
        Assert.Empty(settings.UnityOnlyCommandLineOptions);
        Assert.Null(SemanticsSetup.ResolveEditorDataPath(options, settings));
    }

    [Fact]
    public async Task 環境の表示にHLSLの設定を出す()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            environment: hlsl
            symbols:
              - name: QUALITY
                values: [0, 1]
            units:
              - files: "*.hlsl"
                entry: MainPS
                target: ps_6_6
            """);

        (ExitCode code, string output, _) = await RunAsync(_workDirectory, "--env");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("言語のバージョン: 2021 (既定)", output, StringComparison.Ordinal);
        Assert.Contains("QUALITY ∈ {0, 1}", output, StringComparison.Ordinal);
        Assert.Contains("*.hlsl: entry MainPS, target ps_6_6", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unity Editor", output, StringComparison.Ordinal);
    }

    private static ShaderlynConfiguration Parse(string content)
        => ConfigurationLoader.Parse(content, Path.Combine("C:", "project", ".shaderlyn.yaml"));

    private static ShaderlynConfiguration ParseWithoutProblems(string content)
    {
        ShaderlynConfiguration configuration = Parse(content);

        Assert.True(
            configuration.Problems.IsEmpty,
            "予期しない設定の問題:\n"
            + string.Join("\n", configuration.Problems.Select(p => $"  {p.Line + 1}: {p.Message}")));

        return configuration;
    }

    private static async Task<(ExitCode Code, string Output, string Error)> RunAsync(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync(args, BuiltInAnalyzers.All, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
