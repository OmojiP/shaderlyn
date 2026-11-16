using System.Text.RegularExpressions;
using Shaderlyn.Cli;

namespace Shaderlyn.Tests;

/// <summary>
/// 構文の誤りのあるシェーダー (<c>fixtures/broken</c>) で、宣言の検査をどこまで続けるかを確かめる。
/// </summary>
/// <remarks>
/// <para>
/// 構文の誤りがどれも関数の本体の中にあり、本体の { と } が対応していれば、誤りを含む文からその関数の終わりの外で、
/// 宣言が無いことを根拠にする検査 (CF0310 など) を続ける (ShaderCompilation.BrokenFunctionSpans)。
/// </para>
/// <para>
/// 各ファイルの先頭の <c>// 期待: 行 ID</c> が、そのファイルに出るエラーと警告のすべてである (同じ行に 2 つあれば 2 行書く。無ければ <c>// 期待: なし</c>)。
/// </para>
/// </remarks>
public sealed partial class BrokenCodeTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures", "broken");

    [GeneratedRegex(@"^// 期待: (?<line>\d+) (?<id>[A-Z]+\d+)", RegexOptions.Multiline)]
    private static partial Regex Expectation();

    [GeneratedRegex(@"[\\/](?<file>[^\\/(]+)\((?<line>\d+):\d+\): (?:error|warning) (?<id>[A-Z]+\d+):")]
    private static partial Regex Reported();

    /// <summary>試すファイルの名前。</summary>
    public static TheoryData<string> Files()
    {
        TheoryData<string> data = [];

        foreach (string path in Directory.EnumerateFiles(FixtureDirectory).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task 期待した指摘だけを出す(string name)
    {
        string path = Path.Combine(FixtureDirectory, name);
        string text = await File.ReadAllTextAsync(path);
        Assert.True(text.Contains("// 期待:", StringComparison.Ordinal), $"{name} に「// 期待:」がありません。");

        string[] expected = [.. Expectation().Matches(text).Select(m => $"{m.Groups["line"].Value} {m.Groups["id"].Value}").Order(StringComparer.Ordinal)];

        // .shader は Unity、.hlsl は汎用の HLSL、.slang は Slang として読む。
        string[] environment = Path.GetExtension(name) switch
        {
            ".slang" => ["--environment", "slang"],
            ".hlsl" => ["--environment", "hlsl"],
            _ => [],
        };

        StringWriter output = new();
        await Program.RunAsync(["--no-config", .. environment, path], BuiltInAnalyzers.All, output, new StringWriter());

        string[] actual = [.. Reported().Matches(output.ToString())
            .Where(m => string.Equals(m.Groups["file"].Value, name, StringComparison.OrdinalIgnoreCase))
            .Select(m => $"{m.Groups["line"].Value} {m.Groups["id"].Value}")
            .Order(StringComparer.Ordinal)];

        Assert.Equal(expected, actual);
    }
}
