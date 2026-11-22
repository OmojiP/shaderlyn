using System.Text;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// ホバーと補完の説明の Markdown を、プレーンテキストに書き換える。
/// </summary>
/// <remarks>
/// <para>
/// Markdown を表示できないエディタ (Visual Studio 2022 など) は、初期化の要求で
/// ホバーの <c>contentFormat</c> や補完の <c>documentationFormat</c> に <c>plaintext</c> だけを申告する。
/// Markdown のまま送ると、<c>`float4 frag()`</c> のように記号がそのまま表示される。
/// </para>
/// <para>
/// 説明で使う書き方は、コードブロック・行内のコード・太字・箇条書きだけである。
/// コードブロックは囲みの行を外して中身を残し、行内のコードと太字は記号を外す。
/// 箇条書きの <c>- </c> はプレーンテキストでも読めるので残す。コードブロックの中は書き換えない。
/// </para>
/// </remarks>
internal static class MarkdownPlainText
{
    /// <summary>
    /// Markdown をプレーンテキストに書き換える。
    /// </summary>
    /// <param name="markdown">ホバーや補完の説明の Markdown。</param>
    /// <returns>プレーンテキスト。</returns>
    public static string Convert(string markdown)
    {
        string[] lines = markdown.Split('\n');
        StringBuilder result = new(markdown.Length);
        bool inCode = false;
        bool first = true;

        foreach (string line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (!first)
            {
                result.Append('\n');
            }

            first = false;

            if (inCode)
            {
                result.Append(line);
                continue;
            }

            // 強制改行の行末の空白 2 つも外す。
            result.Append(line.Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal)
                .TrimEnd(' '));
        }

        return result.ToString();
    }
}
