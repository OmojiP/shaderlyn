using System.Text;
using System.Text.Json;
using Shaderlyn.Cli;
using Xunit.Abstractions;

namespace Shaderlyn.Tests;

/// <summary>
/// 外部の公開シェーダー (DirectX-Graphics-Samples など) を汎用の HLSL 環境で解析し、誤検出が無いことを確かめる。
/// </summary>
/// <remarks>
/// <para>
/// <b>コーパスはリポジトリに入れない。</b> <c>node tools/corpus/fetch.mjs</c> で <c>.work/corpus/</c> (git の対象外) に取ってきて、
/// 環境変数 <c>SHADERLYN_HLSL_CORPUS</c> にその場所を渡したときだけ動く。渡さなければ何もせずに通る。
/// </para>
/// <para>
/// <b>コンパイル単位は分からない。</b> エントリポイント・ターゲット・定義を知らないので、ファイル全体を 1 つの単位として解析する。
/// 実在するサンプルはどれもコンパイルが通るので、エラーと警告は誤検出の候補である。
/// 確かめて正しい指摘だと分かったものは、理由を添えて <see cref="KnownFindings"/> に並べる。
/// </para>
/// <para>
/// 指摘の一覧は、コーパスの隣に <c>report-&lt;名前&gt;.txt</c> として書き出す。調べるときに使う。
/// </para>
/// </remarks>
public sealed class ExternalCorpusTests(ITestOutputHelper output)
{
    /// <summary>コーパスの置き場所を渡す環境変数。</summary>
    private const string CorpusVariable = "SHADERLYN_HLSL_CORPUS";

    /// <summary>
    /// 確かめて正しいと分かった指摘。鍵は「コーパスの中の相対パス:行:ID」、値は理由。
    /// </summary>
    private static readonly Dictionary<string, string> KnownFindings = new(StringComparer.Ordinal);

    /// <summary>
    /// 解析の設定が足りないことを表す指摘。コーパスのコンパイル単位 (探索パス) を知らないので出る。誤検出の候補には数えない。
    /// </summary>
    private static readonly HashSet<string> SetupDiagnostics = new(StringComparer.Ordinal) { "HL0321", "TOOL0004" };

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task 外部のコーパスに誤検出が無い()
    {
        string? root = Environment.GetEnvironmentVariable(CorpusVariable);

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _output.WriteLine($"{CorpusVariable} が無いため飛ばします。");
            return;
        }

        List<string> unexpected = [];

        foreach (string corpus in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            unexpected.AddRange(await AnalyzeCorpusAsync(Path.GetFullPath(root), Path.GetFullPath(corpus)));
        }

        Assert.True(
            unexpected.Count == 0,
            $"誤検出の候補が {unexpected.Count} 件あります:\n" + string.Join("\n", unexpected.Take(60)));
    }

    /// <summary>1 つのコーパスを解析し、エラーと警告のうち確かめていないものを返す。</summary>
    /// <param name="root">コーパスの置き場所。</param>
    /// <param name="corpus">コーパスのフォルダー。</param>
    /// <returns>確かめていない指摘。</returns>
    /// <remarks>
    /// <para>
    /// <b>ファイルごとに解析する。</b> 取り込みを解決できなかったファイル (プロジェクトの探索パスを知らないため) では、
    /// ヘッダが定義するマクロが見えず、<c>#if</c> の外れるはずの分岐を読むなど、指摘が当てにならない。
    /// そうしたファイルの指摘は誤検出の候補に数えず、ファイルの数だけ記録する。
    /// </para>
    /// <para>
    /// 取り込んだヘッダの指摘は、取り込んだファイルごとに出る。同じ位置の同じ指摘は 1 件にまとめる。
    /// </para>
    /// </remarks>
    private async Task<List<string>> AnalyzeCorpusAsync(string root, string corpus)
    {
        string name = Path.GetFileName(corpus);
        string[] files = [.. Directory.EnumerateFiles(corpus, "*.hlsl", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

        StringBuilder report = new();
        SortedDictionary<string, string> candidates = new(StringComparer.Ordinal);
        Dictionary<string, int> byRule = new(StringComparer.Ordinal);
        List<string> setupAffected = [];

        foreach (string file in files)
        {
            StringWriter json = new();
            StringWriter error = new();

            ExitCode code = await Program.RunAsync(
                [file, "--no-config", "--environment", "hlsl", "--format", "json", "--base-path", corpus],
                BuiltInAnalyzers.All,
                json,
                error);

            Assert.True(code != ExitCode.ToolError, $"{file}: {error}");

            using JsonDocument document = JsonDocument.Parse(json.ToString());
            JsonElement[] diagnostics = [.. document.RootElement.GetProperty("diagnostics").EnumerateArray()];
            bool affected = diagnostics.Any(d => SetupDiagnostics.Contains(d.GetProperty("id").GetString()!));
            string relative = Path.GetRelativePath(corpus, file).Replace('\\', '/');

            if (affected)
            {
                setupAffected.Add(relative);
            }

            foreach (JsonElement diagnostic in diagnostics)
            {
                string id = diagnostic.GetProperty("id").GetString()!;
                string severity = diagnostic.GetProperty("severity").GetString()!;
                string where = diagnostic.GetProperty("file").GetString()!.Replace('\\', '/');
                int line = diagnostic.GetProperty("line").GetInt32();
                string message = diagnostic.GetProperty("message").GetString()!.ReplaceLineEndings(" ");
                string key = $"{where}:{line}:{id}";

                report.AppendLine($"{severity} {key} {message}{(affected ? " [取り込みを解決できないファイル " + relative + "]" : string.Empty)}");
                byRule[$"{severity} {id}"] = byRule.GetValueOrDefault($"{severity} {id}") + 1;

                if (!affected && severity is "error" or "warning" && !KnownFindings.ContainsKey(key))
                {
                    candidates.TryAdd(key, $"{name}/{key} {message}");
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(root, $"report-{name}.txt"), report.ToString());

        _output.WriteLine(
            $"{name}: {files.Length} ファイル、取り込みを解決できないファイル {setupAffected.Count}、誤検出の候補 {candidates.Count}");

        foreach ((string rule, int count) in byRule.OrderByDescending(p => p.Value))
        {
            _output.WriteLine($"  {rule}: {count}");
        }

        return [.. candidates.Values];
    }
}
