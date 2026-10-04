using Shaderlyn.Cli;
using Shaderlyn.Core.Text;
using Shaderlyn.Unreal;

namespace Shaderlyn.Tests;

/// <summary>
/// Unreal Engine の環境の検証。エンジン本体は使わず、同じ形のフォルダーを作って確かめる。
/// </summary>
public sealed class UnrealEnvironmentTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), "shaderlyn-unreal-" + Guid.NewGuid().ToString("N"));

    public UnrealEnvironmentTests() => Directory.CreateDirectory(_workDirectory);

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
    public void 仮想パスを割り当てたフォルダーから引く()
    {
        Write("Engine/Shaders/Private/Common.ush", "// engine");
        Write("Plugins/A/Shaders/Private/A.ush", "// plugin A");
        Write("Plugins/A/Sub/Shaders/B.ush", "// plugin A sub");

        UnrealVirtualPathResolver resolver = new(new Dictionary<string, string>
        {
            ["/Engine"] = Path.Combine(_workDirectory, "Engine", "Shaders"),
            ["/Plugin/A"] = Path.Combine(_workDirectory, "Plugins", "A", "Shaders"),
            ["/Plugin/A/Sub/"] = Path.Combine(_workDirectory, "Plugins", "A", "Sub", "Shaders"),
        });

        Assert.True(resolver.TryResolve("/Engine/Private/Common.ush", "x.usf", out SourceText? common));
        Assert.Equal("// engine", common.ToString());

        Assert.True(resolver.TryResolve("/Plugin/A/Private/A.ush", "x.usf", out SourceText? a));
        Assert.Equal("// plugin A", a.ToString());

        // 長い割り当てが勝つ。
        Assert.True(resolver.TryResolve("/Plugin/A/Sub/B.ush", "x.usf", out SourceText? b));
        Assert.Equal("// plugin A sub", b.ToString());

        // 仮想パスでないものと、割り当ての無いものは引かない。
        Assert.False(resolver.TryResolve("Private/Common.ush", "x.usf", out _));
        Assert.False(resolver.TryResolve("/Engine/Generated/GeneratedUniformBuffers.ush", "x.usf", out _));
        Assert.False(resolver.TryResolve("/Plugin/Other/X.ush", "x.usf", out _));
    }

    [Fact]
    public async Task パーミュテーションごとにUnrealのシェーダーを解析する()
    {
        Write(".shaderlyn.yaml", """
            version: 1
            environment: unreal
            unreal:
              engine: Engine
              project: .
            symbols:
              - name: PERMUTATION_HIGH_QUALITY   # SHADER_PERMUTATION_BOOL は 0 か 1 で定義される
                values: [0, 1]
            units:
              - files: Shaders/**/*.usf
                entry: MainPS
                target: ps_6_6
            """);

        // エンジンの共通ヘッダは、ビルドのときに生成される uniform buffer の宣言を取り込む。
        Write("Engine/Shaders/Private/Common.ush", """
            #pragma once
            #include "/Engine/Generated/GeneratedUniformBuffers.ush"
            #if COMPILER_HLSL && SM6_PROFILE
            float Square(float x) { return x * x; }
            #endif
            """);
        Write("Shaders/Private/MyCommon.ush", """
            #pragma once
            float3 Tint(float3 c) { return c * 0.5; }
            """);
        Write("Shaders/Private/MyPass.usf", """
            #include "/Engine/Private/Common.ush"
            #include "/Project/Private/MyCommon.ush"

            float4 MainPS(float4 SvPosition : SV_Position) : SV_Target0
            {
            #if PERMUTATION_HIGH_QUALITY
                float3 color = Tint(Square(SvPosition.x));
            #else
                float4 color = Square(SvPosition.x);
            #endif
                return color.a;
            }
            """);

        (ExitCode code, string output, string error) = await RunAsync(_workDirectory);

        Assert.True(error.Length == 0, error);
        Assert.Equal(ExitCode.DiagnosticsFound, code);

        // パーミュテーションの値ごとに読み分け、値を添えて報告する。
        Assert.Contains("HL0312", output, StringComparison.Ordinal);
        Assert.Contains("PERMUTATION_HIGH_QUALITY == 1", output, StringComparison.Ordinal);

        // 生成される取り込みは誤りとして報告しない。中身を知らないことは SL0002 で伝える。
        Assert.DoesNotContain("HL0321", output, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOL0004", output, StringComparison.Ordinal);
        Assert.Contains("エンジンが生成するファイル", output, StringComparison.Ordinal);
        Assert.Contains("1 ファイルを解析", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task エンジンの場所が無ければUnrealの直し方を案内する()
    {
        Write(".shaderlyn.yaml", "environment: unreal\n");
        Write("Shaders/MyPass.usf", """
            #include "/Engine/Private/Common.ush"
            float4 MainPS() : SV_Target0 { return 1; }
            """);

        (_, string output, _) = await RunAsync(_workDirectory);

        Assert.Contains("TOOL0004", output, StringComparison.Ordinal);
        Assert.Contains("Unreal の仮想パス", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 環境の表示に仮想パスを出す()
    {
        Write(".shaderlyn.yaml", """
            environment: unreal
            unreal:
              engine: Engine
              virtual-paths:
                /Plugin/MyPlugin: Plugins/MyPlugin/Shaders
            """);
        Directory.CreateDirectory(Path.Combine(_workDirectory, "Engine", "Shaders"));

        (ExitCode code, string output, _) = await RunAsync(_workDirectory, "--env");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("環境: Unreal Engine", output, StringComparison.Ordinal);
        Assert.Contains("/Engine → ", output, StringComparison.Ordinal);
        Assert.Contains("/Plugin/MyPlugin → ", output, StringComparison.Ordinal);
        Assert.Contains("(ありません)", output, StringComparison.Ordinal);
        Assert.Contains("言語のバージョン: 2018 (既定)", output, StringComparison.Ordinal);
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_workDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static async Task<(ExitCode Code, string Output, string Error)> RunAsync(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync(args, BuiltInAnalyzers.All, output, error);
        return (code, output.ToString(), error.ToString());
    }
}
