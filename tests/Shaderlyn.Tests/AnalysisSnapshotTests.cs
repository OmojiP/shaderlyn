using System.Collections.Immutable;
using System.Text;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;
using Xunit.Abstractions;

namespace Shaderlyn.Tests;

/// <summary>
/// 解析の結果 (すべての診断) を記録し、作り替えの前後で変わっていないことを確かめる。
/// </summary>
/// <remarks>
/// <para>
/// <b>形を変える作業 (層の分け直し・型の移動) のためのものである。</b>
/// 規則ごとの件数が同じでも、位置やメッセージが変わっていれば利用者には別の結果である。
/// ID・重要度・位置・メッセージをすべて並べて比べる。
/// </para>
/// <para>
/// <b>環境変数 <c>SHADERLYN_SNAPSHOT</c> に記録の置き場所を与えたときだけ動く。</b>
/// 記録は手元の Unity のバージョンに依るので、リポジトリには入れない。
/// 置き場所にファイルが無ければ記録し、あれば比べる。違えば、今の結果を <c>.received</c> を付けて隣に書き出す。
/// </para>
/// <para>
/// 対象は Unity 同梱の <c>.shader</c> と <c>.compute</c>、およびこのテストの fixtures のすべてである。
/// Unity が見つからなければ飛ばす。
/// </para>
/// </remarks>
public sealed class AnalysisSnapshotTests(ITestOutputHelper output)
{
    /// <summary>記録の置き場所を与える環境変数。</summary>
    private const string SnapshotVariable = "SHADERLYN_SNAPSHOT";

    /// <summary>食い違いを出力に並べる上限。</summary>
    private const int MaxReportedDifferences = 40;

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void 解析の結果が記録と一致する()
    {
        string? snapshotPath = Environment.GetEnvironmentVariable(SnapshotVariable);

        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            _output.WriteLine($"{SnapshotVariable} が無いため飛ばします。");
            return;
        }

        string? editorData = UnityCorpusTests.FindUnityEditorData();

        if (editorData is null)
        {
            _output.WriteLine("Unity が見つからないため飛ばします。");
            return;
        }

        List<string> lines = [.. CollectResults(editorData)];

        if (!File.Exists(snapshotPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(snapshotPath))!);
            File.WriteAllLines(snapshotPath, lines, new UTF8Encoding(false));
            _output.WriteLine($"{lines.Count} 行を記録しました: {snapshotPath}");
            return;
        }

        string[] expected = File.ReadAllLines(snapshotPath, Encoding.UTF8);
        string receivedPath = snapshotPath + ".received";

        if (expected.SequenceEqual(lines, StringComparer.Ordinal))
        {
            File.Delete(receivedPath);
            _output.WriteLine($"{lines.Count} 行が記録と一致しました。");
            return;
        }

        File.WriteAllLines(receivedPath, lines, new UTF8Encoding(false));

        HashSet<string> before = [.. expected];
        HashSet<string> after = [.. lines];
        List<string> differences =
        [
            .. expected.Where(l => !after.Contains(l)).Select(l => "- " + l),
            .. lines.Where(l => !before.Contains(l)).Select(l => "+ " + l),
        ];

        Assert.Fail(
            $"解析の結果が記録と違います (消えた {expected.Count(l => !after.Contains(l))} 行、増えた {lines.Count(l => !before.Contains(l))} 行)。"
            + $"今の結果: {receivedPath}\n"
            + string.Join("\n", differences.Take(MaxReportedDifferences)));
    }

    /// <summary>すべての対象を解析し、診断を 1 行ずつ並べる。</summary>
    /// <param name="editorData">Unity Editor のデータフォルダー。</param>
    /// <returns>ファイルの順、ファイルの中では位置の順に並べた行。</returns>
    private static IEnumerable<string> CollectResults(string editorData)
    {
        PreprocessorOptions preprocessor = UnityCorpusTests.CreateOptions(editorData);
        SemanticsOptions options = new()
        {
            IncludeResolver = preprocessor.IncludeResolver,
            PredefinedMacros = preprocessor.PredefinedMacros,
            TokenCache = new HlslTokenCache(),
        };

        AnalyzerDriver driver = new(Cli.BuiltInAnalyzers.All);
        EnumerationOptions enumeration = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
        string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");

        IEnumerable<(string Root, string Path)> files =
        [
            .. Directory.EnumerateFiles(editorData, "*.shader", enumeration)
                .Concat(Directory.EnumerateFiles(editorData, "*.compute", enumeration))
                .Select(p => (editorData, p)),
            .. Directory.EnumerateFiles(fixtures, "*.*", enumeration)
                .Where(p => p.EndsWith(".shader", StringComparison.Ordinal)
                    || p.EndsWith(".compute", StringComparison.Ordinal)
                    || p.EndsWith(".hlsl", StringComparison.Ordinal))
                .Select(p => (fixtures, p)),
        ];

        foreach ((string root, string path) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            string name = Path.GetRelativePath(root, path).Replace('\\', '/');
            SourceText text = SourceText.From(File.ReadAllText(path), path);

            ShaderCompilation compilation = path.EndsWith(".shader", StringComparison.Ordinal)
                ? ShaderCompilation.Create(text, ShaderLabSyntaxTree.Parse(text), options)
                : ShaderCompilation.CreateForHlsl(text, options);

            IEnumerable<string> diagnostics = driver.Analyze(compilation.CreateAnalysisTarget())
                .OrderBy(d => d.Location.Span.Start)
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .ThenBy(d => d.GetMessage(), StringComparer.Ordinal)
                .Select(d => $"{name}{Where(d, path)}({d.Location.LineSpan.Start}-{d.Location.LineSpan.End}) {d.Id} {d.Severity}: {Flatten(d.GetMessage())}");

            foreach (string line in diagnostics)
            {
                yield return line;
            }
        }
    }

    /// <summary>診断の位置が解析したファイルの外 (取り込んだヘッダ) なら、そのファイル名を示す。</summary>
    /// <param name="diagnostic">診断。</param>
    /// <param name="path">解析したファイル。</param>
    /// <returns><c>@ファイル名</c>。解析したファイルの中なら空。</returns>
    private static string Where(Diagnostic diagnostic, string path)
        => string.Equals(diagnostic.Location.FilePath, path, StringComparison.Ordinal)
            ? string.Empty
            : "@" + Path.GetFileName(diagnostic.Location.FilePath);

    /// <summary>メッセージを 1 行にする。</summary>
    /// <param name="message">元のメッセージ。</param>
    /// <returns>改行を空白にしたもの。</returns>
    private static string Flatten(string message) => message.Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' ');
}
