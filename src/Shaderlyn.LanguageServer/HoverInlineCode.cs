using System.Text;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// ホバーのコードブロックを、行内のコードに書き換える。
/// </summary>
/// <remarks>
/// <para>
/// Visual Studio はホバーのコードブロックに「コードのコピー」の見出しを付け、
/// 1 行の宣言でも大きな枠で表示する。
/// 初期化の設定で <c>hoverCodeBlocks: false</c> を受け取ったエディタには、コードブロックを行内のコードにして返す。
/// </para>
/// <para>
/// 複数行のコードブロック (構造体・多重定義の並びなど) は、1 行ずつ行内のコードにして改行でつなぐ。
/// 行頭の字下げは、表示で詰められないよう改行しない空白 (U+00A0) にする。
/// </para>
/// </remarks>
internal static class HoverInlineCode
{
    /// <summary>
    /// Markdown の中のコードブロックを、行内のコードに書き換える。
    /// </summary>
    /// <param name="markdown">ホバーの Markdown。</param>
    /// <returns>書き換えた Markdown。コードブロックが無ければ元のまま。</returns>
    public static string Convert(string markdown)
    {
        if (!markdown.Contains("```", StringComparison.Ordinal))
        {
            return markdown;
        }

        string[] lines = markdown.Split('\n');
        StringBuilder result = new(markdown.Length);
        List<string>? code = null;

        foreach (string line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (code is null)
                {
                    code = [];
                    continue;
                }

                AppendCode(result, code);
                code = null;
                continue;
            }

            if (code is not null)
            {
                code.Add(line);
                continue;
            }

            result.Append(line).Append('\n');
        }

        // 閉じていないコードブロックは、そこまでを行内のコードにする。
        if (code is not null)
        {
            AppendCode(result, code);
        }

        // Split で増えた末尾の改行を落とし、元の末尾に合わせる。
        if (!markdown.EndsWith('\n') && result.Length > 0 && result[^1] == '\n')
        {
            result.Length--;
        }

        return result.ToString();
    }

    /// <summary>コードブロックの行を、行内のコードにして加える。</summary>
    /// <param name="result">書き出し先。</param>
    /// <param name="code">コードブロックの中の行。</param>
    private static void AppendCode(StringBuilder result, List<string> code)
    {
        List<string> parts = [.. code.Where(l => !string.IsNullOrWhiteSpace(l)).Select(ToInline)];

        // 行末の空白 2 つで改行する (Markdown の強制改行)。
        result.Append(string.Join("  \n", parts)).Append('\n');
    }

    /// <summary>1 行を行内のコードにする。</summary>
    /// <param name="line">コードの 1 行。</param>
    /// <returns>行内のコード。</returns>
    private static string ToInline(string line)
    {
        string trimmed = line.TrimStart(' ', '\t');
        int indent = line[..(line.Length - trimmed.Length)].Sum(c => c == '\t' ? 4 : 1);
        string text = new string(' ', indent) + trimmed.TrimEnd();

        // 中にバッククォートがあれば、それより長い並びで囲む。
        int longest = 0;
        int run = 0;

        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        if (longest == 0)
        {
            return $"`{text}`";
        }

        string fence = new('`', longest + 1);
        return $"{fence} {text} {fence}";
    }
}
