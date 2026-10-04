using System.Collections.Immutable;
using Shaderlyn.Cli;
using Shaderlyn.Cookbook.Environment;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Environments;
using Xunit.Abstractions;

namespace Shaderlyn.Tests;

/// <summary>
/// 独自エンジンの環境 (docs/custom-rules/cookbook.md の <see cref="MyEngineEnvironment"/>) と、
/// 自前のホストが環境を足す口 (<see cref="HostEnvironment"/>) の検証。
/// </summary>
public sealed class HostEnvironmentTests(ITestOutputHelper output) : IDisposable
{
    private const string Shader = """
        #pragma variant QUALITY 0 1 2
        #pragma variant USE_FOG
        #pragma entry MainPS ps_6_6

        float4 MainPs() : SV_Target
        {
        #if QUALITY == 2
            float3 c = 1;
        #else
            float4 c = 1;
        #endif
        #ifdef USE_FOG
            c *= 0.5;
        #endif
        #ifdef USE_SHADOWS
            c = 0;
        #endif
        #ifdef _DEBUG
            c = 1;
        #endif
            return c.w;
        }
        """;

    private readonly ITestOutputHelper _output = output;

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), "shaderlyn-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDirectory))
            {
                Directory.Delete(_workDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }
    }

    [Fact]
    public void 独自のpragmaで宣言した構成とエントリポイントで解析する()
    {
        SourceText text = SourceText.From(Shader, "Lit.fx");

        ShaderCompilation compilation = ShaderCompilation.Create(
            text, [HlslEnvironment.WholeFile(text)], text, new MyEngineEnvironment(), ShaderSourceInfo.None);

        ImmutableArray<Diagnostic> diagnostics =
            new AnalyzerDriver(BuiltInAnalyzers.All).Analyze(compilation.CreateAnalysisTarget());

        foreach (Diagnostic diagnostic in diagnostics)
        {
            _output.WriteLine($"{diagnostic.Location.LineSpan.Start} {diagnostic.Id}: {diagnostic.GetMessage()}");
        }

        Assert.Contains(diagnostics, d => d.Id == "HL0301" && d.GetMessage().Contains("#pragma entry が指定する関数 'MainPS'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Id == "HL0312" && d.GetMessage().Contains("QUALITY == 2", StringComparison.Ordinal));

        // エンジンの慣習 (USE_) に合う名前だけを、宣言し忘れとして報告する。
        // 値を持つシンボルは名前 (QUALITY) で使われている。原子 (QUALITY==0) を使っていないとは言わない。
        Assert.DoesNotContain(diagnostics, d => d.Id == "HL0331");

        Diagnostic undeclared = Assert.Single(diagnostics, d => d.Id == "HL0330");
        Assert.Contains("USE_SHADOWS", undeclared.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 自前のホストが足した環境を設定ファイルで選ぶ()
    {
        Directory.CreateDirectory(_workDirectory);
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "environment: myengine\n");
        File.WriteAllText(Path.Combine(_workDirectory, "Lit.fx"), Shader);

        HostEnvironment myEngine = new("myengine", "MyEngine", [".fx"], () => new MyEngineEnvironment());

        (ExitCode code, string text, string error) = await RunAsync([myEngine], _workDirectory);

        Assert.True(error.Length == 0, error);
        Assert.Equal(ExitCode.DiagnosticsFound, code);
        Assert.Contains("HL0301", text, StringComparison.Ordinal);
        Assert.Contains("1 ファイルを解析", text, StringComparison.Ordinal);

        (_, string environment, _) = await RunAsync([myEngine], _workDirectory, "--env");
        Assert.Contains("環境: MyEngine", environment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 足していない環境の名前は指定できる環境を添えてエラーにする()
    {
        Directory.CreateDirectory(_workDirectory);
        File.WriteAllText(Path.Combine(_workDirectory, "Lit.fx"), Shader);

        (ExitCode code, _, string error) = await RunAsync([], _workDirectory, "--environment", "myengine");

        Assert.Equal(ExitCode.ToolError, code);
        Assert.Contains("unity, hlsl, unreal", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unity")]
    [InlineData("")]
    public async Task 組み込みの環境と重なる名前は使えない(string name)
    {
        HostEnvironment clash = new(name, "X", [".fx"], () => new MyEngineEnvironment());

        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync([clash], "."));
    }

    private static async Task<(ExitCode Code, string Output, string Error)> RunAsync(
        ImmutableArray<HostEnvironment> environments,
        params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync(args, BuiltInAnalyzers.All, environments, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
