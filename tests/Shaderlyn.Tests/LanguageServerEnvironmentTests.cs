using System.Text.Json;

namespace Shaderlyn.Tests;

/// <summary>
/// 言語サーバーを Unity 以外の環境 (設定ファイルの <c>environment: hlsl</c>) で使ったときの検証。
/// </summary>
public sealed class LanguageServerEnvironmentTests : IDisposable
{
    private const string Shader = """
        float4 main() : SV_Target
        {
        #if QUALITY == 2
            return 1; // High
        #else
            return 0; // Low
        #endif
        }
        """;

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), "shaderlyn-lsp-env-" + Guid.NewGuid().ToString("N"));

    public LanguageServerEnvironmentTests()
    {
        Directory.CreateDirectory(_workDirectory);
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), """
            environment: hlsl
            symbols:
              - name: QUALITY
                values: [0, 1, 2]
            """);
    }

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
    public async Task 値を持つシンボルを値ごとの候補として返す()
    {
        await using LanguageServerHarness harness = new(DocumentUri());
        await harness.InitializeAsync(null, _workDirectory);
        await harness.OpenAsync(Shader);

        JsonElement[] symbols = await harness.RequestConditionSymbolsRawAsync();

        Assert.Equal(["QUALITY==0", "QUALITY==1", "QUALITY==2"], symbols.Select(s => s.GetProperty("name").GetString()));
        Assert.All(symbols, s =>
        {
            Assert.True(s.GetProperty("declared").GetBoolean());
            Assert.True(s.GetProperty("requiresOne").GetBoolean());
            Assert.Equal("QUALITY", s.GetProperty("variable").GetString());
        });
        Assert.Equal([0, 1, 2], symbols.Select(s => s.GetProperty("value").GetInt32()));
        Assert.Single(symbols.Select(s => s.GetProperty("group").GetInt32()).Distinct());
    }

    [Fact]
    public async Task 値を選んだ構成で効いていない範囲を表示する()
    {
        await using LanguageServerHarness harness = new(DocumentUri());
        await harness.InitializeAsync(new { inactiveRegions = true }, _workDirectory);
        await harness.OpenAsync(Shader);
        await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");

        await harness.SetSelectedConfigurationAsync("QUALITY==2");

        JsonElement high = await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");
        Assert.False(Covers(high, LineOf("High")));
        Assert.True(Covers(high, LineOf("Low")));

        await harness.SetSelectedConfigurationAsync("QUALITY==1");

        JsonElement mid = await harness.ReceiveNotificationAsync("shaderlyn/inactiveRegions");
        Assert.True(Covers(mid, LineOf("High")));
        Assert.False(Covers(mid, LineOf("Low")));
    }

    /// <summary>ワークスペースの中のファイルの URI。</summary>
    private string DocumentUri()
        => LanguageServer.DocumentUri.FromFilePath(Path.Combine(_workDirectory, "Shader.hlsl"));

    /// <summary>その文字列が書かれた行の番号 (0 始まり)。</summary>
    private static int LineOf(string marker)
        => Shader[..Shader.IndexOf(marker, StringComparison.Ordinal)].Count(c => c == '\n');

    /// <summary>通知された範囲のどれかが、その行を含むかを判定する。</summary>
    private static bool Covers(JsonElement notification, int line)
        => notification.GetProperty("regions").EnumerateArray().Any(
            r => r.GetProperty("start").GetProperty("line").GetInt32() <= line
                 && line < r.GetProperty("end").GetProperty("line").GetInt32());
}
