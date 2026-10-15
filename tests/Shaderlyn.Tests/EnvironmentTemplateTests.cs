using Shaderlyn.Cli;
using Shaderlyn.Configuration;

namespace Shaderlyn.Tests;

/// <summary>
/// 環境ごとの設定ファイルのテンプレート (samples/environments) の検証。
/// </summary>
/// <remarks>
/// テンプレートは利用者が写して使う。設定の書式が変わったときに、誰にも気づかれずに読めなくなってはならない。
/// README に書いた「出るはずの指摘」が実際に出ることも確かめる。
/// </remarks>
public sealed class EnvironmentTemplateTests
{
    private static string TemplateDirectory(string name)
        => Path.Combine(AppContext.BaseDirectory, "samples", "environments", name);

    [Theory]
    [InlineData("unity")]
    [InlineData("hlsl")]
    [InlineData("unreal")]
    public void テンプレートの設定ファイルは問題なく読める(string name)
    {
        ShaderlynConfiguration configuration =
            ConfigurationLoader.Load(Path.Combine(TemplateDirectory(name), ShaderlynConfiguration.FileName));

        Assert.True(
            configuration.Problems.IsEmpty,
            string.Join("\n", configuration.Problems.Select(p => $"{p.Line + 1}: {p.Message}")));
        Assert.Equal(name, configuration.Environment);
    }

    [Theory]
    [InlineData("unity", new[] { "SL1003", "HL0330", "HL0312" }, "1 ファイルを解析")]
    [InlineData("hlsl", new[] { "HL0301", "HL0312", "HL0345", "HL0312" }, "4 ファイルを解析")]
    [InlineData("unreal", new[] { "UE0001", "HL0312" }, "1 ファイルを解析")]
    public async Task テンプレートでREADMEに書いた指摘が出る(string name, string[] expected, string summary)
    {
        StringWriter output = new();
        StringWriter error = new();

        ExitCode code = await Program.RunAsync(
            [TemplateDirectory(name), "--format", "json"], BuiltInAnalyzers.All, output, error);

        Assert.True(error.ToString().Length == 0, error.ToString());
        Assert.Equal(ExitCode.DiagnosticsFound, code);

        string[] reported = [.. System.Text.RegularExpressions.Regex
            .Matches(output.ToString(), "\"id\"\\s*:\\s*\"([A-Z]+[0-9]+)\"")
            .Select(m => m.Groups[1].Value)];

        Assert.Equal(expected.Order(StringComparer.Ordinal), reported.Order(StringComparer.Ordinal));

        StringWriter text = new();
        await Program.RunAsync([TemplateDirectory(name)], BuiltInAnalyzers.All, text, new StringWriter());
        Assert.Contains(summary, text.ToString(), StringComparison.Ordinal);
    }
}
