using Shaderlyn.Cli;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Tests;

/// <summary>
/// すべてのルールに説明ドキュメントが存在することを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 「なぜ問題なのか」が読めない指摘は、理解されないまま抑制されるか、
/// 意味が分からないまま機械的に潰されて別の不具合を生む。
/// ドキュメントの有無を人間のレビューに任せると必ず抜けるため、テストで強制する。
/// </para>
/// <para>
/// ルールを追加したら <c>docs/rules/&lt;ID&gt;.md</c> も追加すること。
/// </para>
/// </remarks>
public sealed class RuleDocumentationTests
{
    private static string DocumentationDirectory
        => Path.Combine(AppContext.BaseDirectory, "docs", "rules");

    /// <summary>ルールの一覧を載せているドキュメント。</summary>
    /// <remarks>
    /// ここに挙げたドキュメントは、実装されているルールと過不足なく一致していなければならない。
    /// 一覧から漏れたルールは、利用者から見れば存在しないのと同じである。
    /// </remarks>
    public static TheoryData<string> RuleListDocuments =>
    [
        Path.Combine(AppContext.BaseDirectory, "docs", "rules", "README.md"),
    ];

    /// <summary>重要度をドキュメントの表記へ直す。</summary>
    /// <param name="severity">対象の重要度。</param>
    /// <returns>ドキュメントに書かれている表記。</returns>
    private static string DescribeSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "エラー",
        DiagnosticSeverity.Warning => "警告",
        DiagnosticSeverity.Info => "情報",
        _ => severity.ToString(),
    };

    /// <summary>
    /// 実装されているすべてのルール定義を集める。
    /// </summary>
    /// <remarks>
    /// <b>製品コードと同じ一覧を使う。</b>
    /// テスト側で一覧を作り直すと、SARIF が出力するルールと
    /// ドキュメントを検証するルールがずれる。
    /// ずれた側にドキュメントが無くても、テストは通ってしまう。
    /// </remarks>
    private static IEnumerable<DiagnosticDescriptor> AllDescriptors()
        => RuleCatalog.GetShaderRules(BuiltInAnalyzers.All);

    [Fact]
    public void すべてのルールに説明ドキュメントがある()
    {
        List<string> missing = [.. AllDescriptors()
            .Where(d => !File.Exists(Path.Combine(DocumentationDirectory, $"{d.Id}.md")))
            .Select(d => d.Id)
            .Order(StringComparer.Ordinal)];

        Assert.True(
            missing.Count == 0,
            $"次のルールに docs/rules/<ID>.md がありません: {string.Join(", ", missing)}");
    }

    [Fact]
    public void すべてのルールにヘルプリンクが設定されている()
    {
        // SARIF の helpUri として出力され、GitHub のアラート画面から辿れるようになる。
        List<string> missing = [.. AllDescriptors()
            .Where(d => string.IsNullOrWhiteSpace(d.HelpLinkUri))
            .Select(d => d.Id)
            .Order(StringComparer.Ordinal)];

        Assert.True(
            missing.Count == 0,
            $"次のルールに HelpLinkUri がありません: {string.Join(", ", missing)}");
    }

    [Fact]
    public void ヘルプリンクの指す先とドキュメントのファイル名が一致する()
    {
        foreach (DiagnosticDescriptor descriptor in AllDescriptors())
        {
            Assert.EndsWith($"/{descriptor.Id}.md", descriptor.HelpLinkUri!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ドキュメントの見出しがルールのタイトルと一致する()
    {
        // 見出しと実装がずれていると、指摘の文言で検索した利用者が
        // 目的のドキュメントに辿り着けない。
        List<string> mismatches = [];

        foreach (DiagnosticDescriptor descriptor in AllDescriptors())
        {
            string path = Path.Combine(DocumentationDirectory, $"{descriptor.Id}.md");
            string heading = File.ReadLines(path).First();
            string expected = $"# {descriptor.Id}: {descriptor.Title}";

            if (!string.Equals(heading, expected, StringComparison.Ordinal))
            {
                mismatches.Add($"{descriptor.Id}: 実装 '{expected}' / ドキュメント '{heading}'");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public void ドキュメントの既定の重要度が実装と一致する()
    {
        // 「警告だと思って無視していたらエラーで CI が落ちた」という食い違いを防ぐ。
        List<string> mismatches = [];

        foreach (DiagnosticDescriptor descriptor in AllDescriptors())
        {
            string path = Path.Combine(DocumentationDirectory, $"{descriptor.Id}.md");
            string expected = DescribeSeverity(descriptor.DefaultSeverity);

            if (!File.ReadLines(path).Any(line =>
                    line.Contains("既定の重要度", StringComparison.Ordinal)
                    && line.Contains(expected, StringComparison.Ordinal)))
            {
                mismatches.Add($"{descriptor.Id}: 実装は {expected}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Theory]
    [MemberData(nameof(RuleListDocuments))]
    public void ルールの一覧が実装と過不足なく一致する(string documentPath)
    {
        // 一覧から漏れたルールは、利用者から見れば存在しないのと同じである。
        // 逆に、消したルールが一覧に残っていると、存在しないものの説明を読ませることになる。
        HashSet<string> implemented = [.. AllDescriptors().Select(d => d.Id)];

        HashSet<string> listed =
        [
            .. File.ReadLines(documentPath)
                .Where(line => line.StartsWith("| ", StringComparison.Ordinal))
                .Select(ExtractRuleId)
                .OfType<string>()
        ];

        List<string> missing = [.. implemented.Except(listed).Order(StringComparer.Ordinal)];
        List<string> extra = [.. listed.Except(implemented).Order(StringComparer.Ordinal)];

        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            $"{Path.GetFileName(documentPath)}: 一覧に無いルール: {string.Join(", ", missing)}"
            + $" / 実装に無い ID: {string.Join(", ", extra)}");
    }

    /// <summary>
    /// 表の行からルール ID を取り出す。
    /// </summary>
    /// <param name="line">表の 1 行。</param>
    /// <returns>ルール ID。ルールの行でない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <c>| SL1001 | …</c> と <c>| [SL1001](docs/rules/SL1001.md) | …</c> の
    /// 両方の書き方を受け付ける。ドキュメントによって書き方が違うためである。
    /// </remarks>
    private static string? ExtractRuleId(string line)
    {
        string cell = line.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

        if (cell.StartsWith('[') && cell.IndexOf(']', StringComparison.Ordinal) is int end and > 0)
        {
            cell = cell[1..end];
        }

        return cell.Length > 2
               && char.IsAsciiLetterUpper(cell[0])
               && cell.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c))
               && cell.Any(char.IsAsciiDigit)
            ? cell
            : null;
    }

    [Fact]
    public void 実装されていないルールのドキュメントが残っていない()
    {
        // ルールを削除したのにドキュメントだけが残ると、
        // 存在しないルールの説明を読ませることになる。
        //
        // ツール自身のルール (TOOL) も数える。
        // すべてに説明が要るわけではないが、用意したものが取り残されるのは同じく困る。
        HashSet<string> implemented = [.. RuleCatalog.GetAllRules(BuiltInAnalyzers.All).Select(d => d.Id)];

        // README.md はルールの一覧であり、ルール 1 つの説明ではない。
        List<string> orphaned = [.. Directory
            .EnumerateFiles(DocumentationDirectory, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => id is not null && id != "README" && !implemented.Contains(id))
            .Select(id => id!)
            .Order(StringComparer.Ordinal)];

        Assert.True(
            orphaned.Count == 0,
            $"次のドキュメントに対応するルールが実装されていません: {string.Join(", ", orphaned)}");
    }
}
