using System.Text.RegularExpressions;

namespace Shaderlyn.Tests;

/// <summary>
/// リリースの概要 (docs/releases) の検証。
/// </summary>
/// <remarks>
/// <para>
/// <b>概要はリリースの本文の先頭に置かれ、GitHub の外 (Marketplace などから来た人) にも読まれる。</b>
/// 書き忘れはリリースのワークフローが止めるが、それはタグを push した後である。PR の時点で見つける。
/// </para>
/// <para>
/// 概要のリンクは、リリースのページから開けるように <c>https://github.com/OmojiP/shaderlyn/blob/main/...</c> の形で書く。
/// 外部の URL として扱うとリンクの検証から漏れるので、リポジトリの中のファイルと見出しに置き換えて照合する。
/// </para>
/// </remarks>
public sealed partial class DocumentationLinkTests
{
    private const string RepositoryBlobUrl = "https://github.com/OmojiP/shaderlyn/blob/main/";

    [Fact]
    public void CHANGELOG_のリリース済みのバージョンにリリースの概要がある()
    {
        string root = RepositoryRoot();
        List<string> problems = [];

        foreach (Match match in ReleasedVersionHeading().Matches(File.ReadAllText(Path.Combine(root, "CHANGELOG.md"))))
        {
            string path = Path.Combine(root, "docs", "releases", $"v{match.Groups[1].Value}.md");
            if (!File.Exists(path))
            {
                problems.Add($"{Relative(root, path)} がありません");
            }
            else if (!SeparatorLine().IsMatch(File.ReadAllText(path)))
            {
                problems.Add($"{Relative(root, path)} に英語と日本語を分ける --- の行がありません");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void リリースの概要のリポジトリへのリンクが切れていない()
    {
        string root = RepositoryRoot();
        List<string> broken = [];
        Dictionary<string, HashSet<string>> anchorCache = new(StringComparer.OrdinalIgnoreCase);

        foreach (string summary in Directory.EnumerateFiles(Path.Combine(root, "docs", "releases"), "v*.md"))
        {
            foreach (string target in Links(StripCode(File.ReadAllText(summary))))
            {
                if (!target.StartsWith(RepositoryBlobUrl, StringComparison.Ordinal))
                {
                    continue;
                }

                string relative = target[RepositoryBlobUrl.Length..];
                int hash = relative.IndexOf('#', StringComparison.Ordinal);
                string pathPart = hash < 0 ? relative : relative[..hash];
                string? anchor = hash < 0 ? null : Uri.UnescapeDataString(relative[(hash + 1)..]).ToLowerInvariant();
                string resolved = Path.Combine(root, Uri.UnescapeDataString(pathPart).Replace('/', Path.DirectorySeparatorChar));
                string where = $"{Relative(root, summary)} -> {target}";

                if (!File.Exists(resolved))
                {
                    broken.Add(where);
                    continue;
                }

                if (anchor is not null)
                {
                    if (!anchorCache.TryGetValue(resolved, out HashSet<string>? anchors))
                    {
                        anchors = Anchors(File.ReadAllText(resolved));
                        anchorCache[resolved] = anchors;
                    }

                    if (!anchors.Contains(anchor))
                    {
                        broken.Add(where + " (見出しがない)");
                    }
                }
            }
        }

        Assert.True(broken.Count == 0, "切れているリンクがあります:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>CHANGELOG のリリース済みのバージョンの見出し (<c>## [0.3.5] - 2026-11-22</c>)。</summary>
    [GeneratedRegex(@"(?m)^## \[(\d+\.\d+\.\d+)\] - \d{4}-\d{2}-\d{2}\s*$")]
    private static partial Regex ReleasedVersionHeading();

    [GeneratedRegex(@"(?m)^---\s*$")]
    private static partial Regex SeparatorLine();
}
