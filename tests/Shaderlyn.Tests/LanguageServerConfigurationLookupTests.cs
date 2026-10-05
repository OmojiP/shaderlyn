using System.Text.Json;

namespace Shaderlyn.Tests;

/// <summary>
/// 言語サーバーが、開いたファイルの場所から設定ファイルを探すことの検証。
/// </summary>
/// <remarks>
/// リポジトリのルートを開き、サブフォルダー (<c>samples/environments/unreal</c> など) に置いた設定のファイルを編集する形を再現する。
/// ワークスペースのルートからだけ探すと、サブフォルダーの設定が読まれず、Unity の環境で ShaderLab として読んでいた。
/// </remarks>
public sealed class LanguageServerConfigurationLookupTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "shaderlyn-lsp-lookup-" + Guid.NewGuid().ToString("N"));

    public LanguageServerConfigurationLookupTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }
    }

    [Fact]
    public async Task サブフォルダーの設定ファイルの環境で解析する()
    {
        string project = Path.Combine(_workspace, "samples", "hlsl");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, ".shaderlyn.yaml"), """
            environment: hlsl
            symbols:
              - name: QUALITY
                values: [0, 1, 2]
            """);

        string path = Path.Combine(project, "Common.hlsli");

        await using LanguageServerHarness harness = new(LanguageServer.DocumentUri.FromFilePath(path));
        await harness.InitializeAsync(null, _workspace);
        await harness.OpenAsync("""
            float4 Shade(float4 c)
            {
            #if QUALITY == 2
                return c;
            #else
                return c * 0.5;
            #endif
            }
            """);

        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "SL0001");

        // 汎用の HLSL 環境の、設定ファイルの値を持つシンボルが候補に並ぶ。
        JsonElement[] symbols = await harness.RequestConditionSymbolsRawAsync();
        Assert.Contains(symbols, s => s.GetProperty("name").GetString() == "QUALITY==2");
    }

    [Fact]
    public async Task 設定ファイルの無いUnityの環境でもHLSLの断片をShaderLabとして読まない()
    {
        // 設定ファイルが無いので Unity の環境。.usf / .ush / .hlsli も HLSL として読む。
        string path = Path.Combine(_workspace, "Pass.usf");

        await using LanguageServerHarness harness = new(LanguageServer.DocumentUri.FromFilePath(path));
        await harness.InitializeAsync(null, _workspace);
        await harness.OpenAsync("float4 MainPS() : SV_Target0 { return 1; }");

        JsonElement diagnostics = await harness.ReceiveDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics.EnumerateArray(), d => d.GetProperty("code").GetString() == "SL0001");
    }

    [Fact]
    public async Task 重なった組をひとつの組にまとめない()
    {
        // B は 2 つの組に入る。A と C はどちらの組でも一緒にならないので、同時に選べなければならない。
        File.WriteAllText(Path.Combine(_workspace, ".shaderlyn.yaml"), """
            environment: hlsl
            symbols:
              - at-most-one: [A, B]
              - at-most-one: [B, C]
              - one-of: [D, E]
            """);

        string path = Path.Combine(_workspace, "Lit.hlsl");

        await using LanguageServerHarness harness = new(LanguageServer.DocumentUri.FromFilePath(path));
        await harness.InitializeAsync(null, _workspace);
        await harness.OpenAsync("""
            float4 main() : SV_Target
            {
            #if defined(A) || defined(B) || defined(C) || defined(D) || defined(E)
                return 1;
            #endif
                return 0;
            }
            """);
        await harness.ReceiveDiagnosticsAsync();

        JsonElement result = await harness.RequestConditionSymbolsResultAsync();
        Dictionary<string, int[]> groupsOf = result.GetProperty("symbols").EnumerateArray().ToDictionary(
            s => s.GetProperty("name").GetString()!,
            s => s.TryGetProperty("groups", out JsonElement groups)
                ? groups.EnumerateArray().Select(g => g.GetInt32()).ToArray()
                : Array.Empty<int>());

        Assert.Single(groupsOf["A"]);
        Assert.Single(groupsOf["C"]);
        Assert.Equal(2, groupsOf["B"].Length);
        Assert.NotEqual(groupsOf["A"][0], groupsOf["C"][0]);
        Assert.Contains(groupsOf["A"][0], groupsOf["B"]);
        Assert.Contains(groupsOf["C"][0], groupsOf["B"]);

        // 組ごとの「どれか 1 つが必ず有効か」。one-of の組だけが立つ。
        Dictionary<int, bool> requiresOne = result.GetProperty("groups").EnumerateArray().ToDictionary(
            g => g.GetProperty("id").GetInt32(),
            g => g.GetProperty("requiresOne").GetBoolean());

        Assert.False(requiresOne[groupsOf["A"][0]]);
        Assert.False(requiresOne[groupsOf["C"][0]]);
        Assert.True(requiresOne[groupsOf["D"][0]]);
    }

    [Fact]
    public async Task 設定ファイルを書き換えたら読み直す()
    {
        string configuration = Path.Combine(_workspace, ".shaderlyn.yaml");
        File.WriteAllText(configuration, "environment: hlsl\n");

        string path = Path.Combine(_workspace, "Lit.hlsl");

        await using LanguageServerHarness harness = new(LanguageServer.DocumentUri.FromFilePath(path));
        await harness.InitializeAsync(null, _workspace);
        await harness.OpenAsync("""
            float4 main() : SV_Target
            {
            #if QUALITY == 1
                return 1;
            #endif
                return 0;
            }
            """);
        await harness.ReceiveDiagnosticsAsync();

        // まだ QUALITY は値を持つシンボルとして宣言されていない。
        JsonElement[] before = await harness.RequestConditionSymbolsRawAsync();
        Assert.DoesNotContain(before, s => s.TryGetProperty("variable", out _));

        File.WriteAllText(configuration, "environment: hlsl\nsymbols:\n  - name: QUALITY\n    values: [0, 1]\n");
        File.SetLastWriteTimeUtc(configuration, DateTime.UtcNow.AddMinutes(1));

        JsonElement[] symbols = await harness.RequestConditionSymbolsRawAsync();
        Assert.Contains(symbols, s => s.GetProperty("name").GetString() == "QUALITY==1");
    }
}
