using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Shaderlyn.Tests;

/// <summary>
/// ドキュメントのリンクと、コードからドキュメントを指すパスが切れていないことの検証。
/// </summary>
/// <remarks>
/// <para>
/// <b>ドキュメントを動かしたり見出しを変えたりすると、どこかのリンクが黙って切れる。</b>
/// GitHub は切れたリンクを 404 にするだけで、書いた側には何も知らせない。
/// 読む人がたどれない案内は、無いのと同じである。
/// </para>
/// <para>
/// 見るのはリポジトリの中を指すリンクだけである。外部の URL は、ネットワークに依存させないために見ない。
/// 見出しへのリンク (<c>#...</c>) は、GitHub が見出しから作るアンカーの規則で照合する。
/// </para>
/// </remarks>
public sealed partial class DocumentationLinkTests
{
    /// <summary>走査しないフォルダー。</summary>
    private static readonly string[] ExcludedDirectories = ["node_modules", "bin", "obj", "out", "artifacts", ".git", ".vs"];

    [Fact]
    public void ドキュメントの中のリンクが切れていない()
    {
        string root = RepositoryRoot();
        List<string> broken = [];
        Dictionary<string, HashSet<string>> anchorCache = new(StringComparer.OrdinalIgnoreCase);

        foreach (string document in EnumerateFiles(root, "*.md"))
        {
            string text = StripCode(File.ReadAllText(document));
            string directory = Path.GetDirectoryName(document)!;

            foreach (string target in Links(text))
            {
                if (IsExternal(target))
                {
                    continue;
                }

                int hash = target.IndexOf('#', StringComparison.Ordinal);
                string pathPart = hash < 0 ? target : target[..hash];
                string? anchor = hash < 0 ? null : Uri.UnescapeDataString(target[(hash + 1)..]).ToLowerInvariant();

                string resolved = pathPart.Length == 0
                    ? document
                    : Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(pathPart)));

                string where = $"{Relative(root, document)} -> {target}";

                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    broken.Add(where);
                    continue;
                }

                if (anchor is not null && resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    if (!anchorCache.TryGetValue(resolved, out HashSet<string>? anchors))
                    {
                        anchors = Anchors(File.ReadAllText(resolved));
                        anchorCache[resolved] = anchors;
                    }

                    if (!anchors.Contains(anchor))
                    {
                        broken.Add(where + " (見出しが無い)");
                    }
                }
            }
        }

        Assert.True(broken.Count == 0, "切れているリンクがあります:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    [Fact]
    public void コードから指しているドキュメントが存在する()
    {
        // コメントの「(docs/guide/cli-usage.md)」のような参照は、ドキュメントを動かしても誰も気づかない。
        string root = RepositoryRoot();
        List<string> broken = [];

        foreach (string folder in (string[])["src", "tests", "samples", ".github", "editors"])
        {
            string path = Path.Combine(root, folder);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (string file in EnumerateFiles(path, "*.*")
                .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".yml", StringComparison.Ordinal)
                    || f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal) || f.EndsWith(".ts", StringComparison.Ordinal)))
            {
                foreach (Match match in DocumentPathInCode().Matches(File.ReadAllText(file)))
                {
                    string reference = match.Value.TrimEnd('.', '/');
                    if (!DocumentExists(root, reference))
                    {
                        broken.Add($"{Relative(root, file)} -> {reference}");
                    }
                }
            }
        }

        Assert.True(broken.Count == 0, "存在しないドキュメントを指しています:" + Environment.NewLine + string.Join(Environment.NewLine, broken.Distinct()));
    }

    /// <summary>コードに書かれたドキュメントのパスが指すものがあるかを調べる。</summary>
    /// <param name="root">リポジトリのルート。</param>
    /// <param name="reference"><c>docs/guide/cli-usage.md</c> のような参照。</param>
    /// <returns>存在すれば <see langword="true"/>。</returns>
    private static bool DocumentExists(string root, string reference)
    {
        string full = Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) || Directory.Exists(full);
    }

    /// <summary>GitHub が見出しから作るアンカーを集める。</summary>
    /// <param name="markdown">ドキュメント。</param>
    /// <returns>小文字にしたアンカー。</returns>
    private static HashSet<string> Anchors(string markdown)
    {
        HashSet<string> anchors = new(StringComparer.Ordinal);
        Dictionary<string, int> seen = new(StringComparer.Ordinal);

        // 見出しの中のインラインコードは、記号を落とした文字としてアンカーに残る。消してはならない。
        foreach (Match heading in Heading().Matches(FencedCode().Replace(markdown, "")))
        {
            string text = MarkdownLink().Replace(heading.Groups[1].Value, "$1");
            string slug = NonSlugCharacter().Replace(text.Trim().ToLowerInvariant(), "").Replace(' ', '-');

            if (seen.TryGetValue(slug, out int count))
            {
                seen[slug] = count + 1;
                anchors.Add($"{slug}-{count}");
            }
            else
            {
                seen[slug] = 1;
                anchors.Add(slug);
            }
        }

        foreach (Match html in HtmlAnchor().Matches(markdown))
        {
            anchors.Add(html.Groups[1].Value.ToLowerInvariant());
        }

        return anchors;
    }

    private static IEnumerable<string> Links(string markdown)
    {
        foreach (Match match in InlineLink().Matches(markdown))
        {
            yield return match.Groups[1].Value;
        }

        foreach (Match match in ReferenceLink().Matches(markdown))
        {
            yield return match.Groups[1].Value;
        }

        foreach (Match match in HtmlSource().Matches(markdown))
        {
            yield return match.Groups[1].Value;
        }
    }

    private static bool IsExternal(string target)
        => target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.Ordinal);

    /// <summary>コードブロックとインラインコードを消す。その中の <c>[x](y)</c> はリンクではない。</summary>
    private static string StripCode(string markdown)
        => InlineCode().Replace(FencedCode().Replace(markdown, ""), "");

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
        => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => !f[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => ExcludedDirectories.Contains(part, StringComparer.OrdinalIgnoreCase)));

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string RepositoryRoot([CallerFilePath] string testFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));

    [GeneratedRegex(@"(?ms)^\s*```.*?^\s*```[^\n]*$")]
    private static partial Regex FencedCode();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\]\(<?([^)\s>]+)>?(?:\s+""[^""]*"")?\)")]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"(?m)^\[[^\]]+\]:\s*(\S+)")]
    private static partial Regex ReferenceLink();

    [GeneratedRegex(@"(?m)^#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"[^\p{L}\p{N}\p{M}\- _]")]
    private static partial Regex NonSlugCharacter();

    [GeneratedRegex(@"<(?:img|a)\s[^>]*?(?:src|href)=""([^""]+)""")]
    private static partial Regex HtmlSource();

    [GeneratedRegex(@"<a\s+(?:name|id)=""([^""]+)""")]
    private static partial Regex HtmlAnchor();

    [GeneratedRegex(@"docs/[\w./\-]*[\w\-]")]
    private static partial Regex DocumentPathInCode();
}
