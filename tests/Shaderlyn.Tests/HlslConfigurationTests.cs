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
              enable-16bit-types: true
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
        Assert.True(configuration.HlslEnable16BitTypes);
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
    [InlineData("hlsl:\n  version: 2020\n", "2016 / 2017 / 2018 / 2021")]
    [InlineData("hlsl:\n  version: 2015\n", "2016 / 2017 / 2018 / 2021")]
    [InlineData("hlsl:\n  compiler: clang\n", "dxc か fxc")]
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
    [InlineData("hlsl:\n  enable-16bit-types: yes-please\n", "true か false")]
    [InlineData("hlsl:\n  compiler: fxc\n  enable-16bit-types: true\n", "compiler が fxc では使えません")]
    [InlineData("hlsl:\n  version: 2017\n  enable-16bit-types: true\n", "HLSL 2018 から使えます")]
    [InlineData("hlsl:\n  enable-16bit-types: true\nunits:\n  - files: a.hlsl\n    target: ps_6_0\n", "シェーダーモデル 6.2 から")]
    [InlineData("environment: [unreal]\n", "environment には値を 1 つだけ")]
    [InlineData("unity:\n  profile: [urp]\n", "unity: の profile には値を 1 つだけ")]
    [InlineData("hlsl:\n  version: [2021]\n", "hlsl: の version には値を 1 つだけ")]
    [InlineData("units:\n  - files: [a.hlsl]\n", "units の要素の files には値を 1 つだけ")]
    [InlineData("units:\n  - files: a.hlsl\n    entry: { name: Main }\n", "units の要素の entry には値を 1 つだけ")]
    [InlineData("symbols:\n  - name: [Q]\n", "symbols の要素の name には値を 1 つだけ")]
    [InlineData("rules:\n  HL0301:\n    severity: [error]\n", "ルール HL0301 の severity には値を 1 つだけ")]
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

    /// <summary>
    /// ある単位では関数の外の変数、別の単位では仮引数として宣言される名前を、未宣言と報告しない
    /// (Unreal Engine の CapsuleShadowShaders.usf の LightDirection)。本当に宣言されていない名前は報告する。
    /// </summary>
    [Fact]
    public async Task 単位によって関数の外の変数か仮引数になる名前を未宣言にしない()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            version: 1
            environment: hlsl
            units:
              - files: a.hlsl
                entry: FromCapsule
                target: cs_6_6
                defines: [MODE=1]
              - files: a.hlsl
                entry: Punctual
                target: cs_6_6
            """);
        File.WriteAllText(Path.Combine(_workDirectory, "a.hlsl"), """
            #ifndef MODE
            #define MODE 0
            #endif
            RWBuffer<float> Output;
            #if MODE == 0
            float3 LightDirection;
            [numthreads(1, 1, 1)] void Punctual() { Output[0] = LightDirection.x; }
            #else
            void Read(out float3 LightDirection) { LightDirection = 1; }
            [numthreads(1, 1, 1)] void FromCapsule() { float3 d; Read(d); Output[0] = d.x + Missing; }
            #endif
            """);

        (_, string output, string error) = await RunAsync(_workDirectory);

        Assert.True(error.Length == 0, error);
        Assert.DoesNotContain("'LightDirection'", output, StringComparison.Ordinal);
        Assert.Contains("'Missing' はどこにも宣言されていません", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 構成の軸で切り替わる <c>#include</c> で取り込んだヘッダを、あとで条件無しにもう一度取り込んでも、
    /// ヘッダの宣言はどの構成にもあることを検証する。2 度目の取り込みは <c>#pragma once</c> かインクルードガードで飛ばされる。
    /// </summary>
    /// <param name="header">ヘッダの中身。</param>
    [Theory]
    [InlineData("#pragma once\nstruct S { float x; };\n")]
    [InlineData("#ifndef H_INCLUDED\n#define H_INCLUDED\nstruct S { float x; };\n#endif\n")]
    public async Task 軸で切り替わる取り込みのあとの二度目の取り込みでも宣言はどの構成にもある(string header)
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            version: 1
            environment: hlsl
            symbols:
              - name: FOO
            """);
        File.WriteAllText(Path.Combine(_workDirectory, "h.hlsli"), header);
        File.WriteAllText(Path.Combine(_workDirectory, "a.hlsl"), """
            #if FOO
            #include "h.hlsli"
            #endif
            #include "h.hlsli"
            float f(S s) { return s.x; }
            [numthreads(1, 1, 1)] void main() {}
            """);

        (_, string output, string error) = await RunAsync(Path.Combine(_workDirectory, "a.hlsl"));

        Assert.True(error.Length == 0, error);
        Assert.DoesNotContain("HL0313", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 同じ文脈の単位が同じに展開したヘッダの宣言は 1 度だけ検査する。単位によって展開が違う宣言は、それぞれの単位で検査する。
    /// </summary>
    [Fact]
    public async Task 単位が同じに展開したヘッダは一度だけ検査する()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            version: 1
            environment: hlsl
            units:
              - files: a.hlsl
                entry: MainA
                target: cs_6_6
                defines: [STAGE_A=1]
              - files: a.hlsl
                entry: MainB
                target: cs_6_6
            """);
        File.WriteAllText(Path.Combine(_workDirectory, "Common.hlsli"), """
            float Twice(float x) { return x * 2; }
            float Same() { return Twice(1, 2); }
            #if STAGE_A
            float OnlyA() { return Twice(); }
            #endif
            """);
        File.WriteAllText(Path.Combine(_workDirectory, "a.hlsl"), """
            #include "Common.hlsli"
            RWBuffer<float> Output;
            [numthreads(1, 1, 1)] void MainA() { Output[0] = Same(); }
            [numthreads(1, 1, 1)] void MainB() { Output[0] = Same(); }
            """);

        (_, string output, string error) = await RunAsync(_workDirectory);

        Assert.True(error.Length == 0, error);
        Assert.Single(output.Split('\n'), line => line.Contains("Common.hlsli(2:", StringComparison.Ordinal) && line.Contains("HL0341", StringComparison.Ordinal));
        Assert.Contains("Common.hlsli(4:", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 同じステージの単位でもエントリポイントは違う。エントリ関数の uniform 引数の検査は、後の単位でも行う。
    /// </summary>
    [Fact]
    public async Task エントリポイントで変わる検査は後の単位でも行う()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            version: 1
            environment: hlsl
            units:
              - files: a.hlsl
                entry: MainA
                target: cs_6_6
              - files: a.hlsl
                entry: MainB
                target: cs_6_6
            """);
        File.WriteAllText(Path.Combine(_workDirectory, "a.hlsl"), """
            RWBuffer<float> Output;
            [numthreads(1, 1, 1)] void MainA() { Output[0] = 1; }
            [numthreads(1, 1, 1)] void MainB(uniform float scale) { Output[0] = scale; }
            """);

        (_, string output, string error) = await RunAsync(_workDirectory);

        Assert.True(error.Length == 0, error);
        Assert.Contains("エントリ関数 MainB の uniform 引数 scale", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 同じに展開したヘッダの宣言でも、単位ごとの文脈 (ほかの宣言) で検査する。
    /// ヘッダが呼ぶ関数の定義が単位で違えば、後の単位でだけ成り立つ指摘も出す。
    /// </summary>
    [Fact]
    public async Task 同じに展開したヘッダの宣言も単位ごとの文脈で検査する()
    {
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            version: 1
            environment: hlsl
            units:
              - files: b.hlsl
                entry: MainA
                target: cs_6_6
                defines: [STAGE_A=1]
              - files: b.hlsl
                entry: MainB
                target: cs_6_6
            """);
        File.WriteAllText(Path.Combine(_workDirectory, "Use.hlsli"), "float Use() { return Pick(1.0); }\n");
        File.WriteAllText(Path.Combine(_workDirectory, "b.hlsl"), """
            #if STAGE_A
            float Pick(float x) { return x; }
            #else
            float Pick(float x, float y) { return x + y; }
            #endif
            #include "Use.hlsli"
            RWBuffer<float> Output;
            [numthreads(1, 1, 1)] void MainA() { Output[0] = Use(); }
            [numthreads(1, 1, 1)] void MainB() { Output[0] = Use(); }
            """);

        (_, string output, string error) = await RunAsync(_workDirectory);

        Assert.True(error.Length == 0, error);
        Assert.Contains("関数 'Pick' に引数を 1 個渡していますが、必要な引数は 2 個です", output, StringComparison.Ordinal);
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
        Assert.DoesNotContain("Packages/<名前>", output, StringComparison.Ordinal);
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
