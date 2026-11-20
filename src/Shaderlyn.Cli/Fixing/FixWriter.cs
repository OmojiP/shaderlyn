using System.Text;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Cli.Fixing;

/// <summary>
/// 修正をファイルに書き込む。書き込まずに差分を作る。
/// </summary>
/// <remarks>
/// <para>
/// <b>ファイルの BOM・エンコーディング・改行の書き方は変えない。</b>
/// 読み込みは解析と同じ (<see cref="SourceText.FromBytes"/>) で、書き込みは読み込んだエンコーディングで行い、元の BOM を付け直す。
/// 改行は修正の文字列が持つものだけが入る (修正を作るルールが、ファイルの改行に合わせる)。
/// </para>
/// <para>
/// <b>解析の後にファイルが変わっていれば書き込まない。</b>
/// 修正の位置は解析したときのテキストの位置なので、長さが違えばずれた位置を書き換える。
/// UTF-8 として読めないバイトがあるファイルも書き込まない。読み込んだ文字列から書き戻すと、そのバイトが U+FFFD に変わる。
/// </para>
/// </remarks>
internal static class FixWriter
{
    /// <summary>差分の前後に表示する、変わらない行の数。</summary>
    private const int ContextLines = 3;

    /// <summary>
    /// ファイルを読み込み、修正を当てられるかを確認する。
    /// </summary>
    /// <param name="file">ファイルに当てる修正。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>読み込んだテキスト。当てられなければその理由。</returns>
    public static async Task<(SourceText? Source, string? Error)> LoadAsync(
        FileFixes file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        SourceText source = await SourceText.LoadAsync(file.FilePath, cancellationToken).ConfigureAwait(false);

        if (source.Length != file.SourceLength)
        {
            return (null, "解析の後にファイルが変わりました");
        }

        if (!source.InvalidUtf8Offsets.IsEmpty)
        {
            return (null, "UTF-8 として読めないバイトがあります");
        }

        return (source, null);
    }

    /// <summary>修正を当てた文字列を、元のエンコーディングと BOM で書き込む。</summary>
    /// <param name="source">読み込んだテキスト。</param>
    /// <param name="file">ファイルに当てる修正。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>書き込みの完了。</returns>
    public static async Task WriteAsync(SourceText source, FileFixes file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);

        byte[] preamble = source.Encoding.GetPreamble();
        byte[] body = source.Encoding.GetBytes(file.Apply(source.Content));
        byte[] bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);

        await File.WriteAllBytesAsync(source.FilePath, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 修正を当てる前と後の差分を、unified diff の形で作る。
    /// </summary>
    /// <param name="displayPath">差分に書くパス (<c>--base-path</c> からの相対)。</param>
    /// <param name="source">当てる前のテキスト。</param>
    /// <param name="file">ファイルに当てる修正。</param>
    /// <returns>差分。</returns>
    /// <remarks>
    /// 行どうしを照合して差分を求めることはしない。置き換えの位置が分かっているので、それを含む行を「変わった行」にする。
    /// 同じ行を書き換える置き換えは 1 つにまとめ、変わらない行が <see cref="ContextLines"/> の 2 倍以下しか挟まらない所は 1 つの見出しにまとめる。
    /// </remarks>
    public static string Diff(string displayPath, SourceText source, FileFixes file)
    {
        ArgumentNullException.ThrowIfNull(displayPath);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);

        List<Block> blocks = BuildBlocks(source, file);

        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        int lastLine = LastLine(source);
        bool endsWithNewLine = source.Length == 0 || source[source.Length - 1] == '\n';

        StringBuilder builder = new();
        builder.Append("--- a/").Append(displayPath).Append('\n');
        builder.Append("+++ b/").Append(displayPath).Append('\n');

        int delta = 0;
        int index = 0;

        while (index < blocks.Count)
        {
            int last = index;

            while (last + 1 < blocks.Count && blocks[last + 1].FirstLine - blocks[last].LastLine - 1 <= ContextLines * 2)
            {
                last++;
            }

            int start = Math.Max(0, blocks[index].FirstLine - ContextLines);
            int end = Math.Min(lastLine, blocks[last].LastLine + ContextLines);
            int oldCount = end - start + 1;
            int newCount = oldCount + blocks.Skip(index).Take(last - index + 1).Sum(b => b.NewLines.Count - b.OldLines.Count);

            builder.Append("@@ -").Append(Range(start, oldCount))
                .Append(" +").Append(Range(start + delta, newCount)).Append(" @@\n");

            int line = start;

            for (int b = index; b <= last; b++)
            {
                Block block = blocks[b];

                for (; line < block.FirstLine; line++)
                {
                    AppendContext(builder, source, line, lastLine, endsWithNewLine);
                }

                AppendLines(builder, '-', block.OldLines, block.OldEndsWithoutNewLine);
                AppendLines(builder, '+', block.NewLines, block.NewEndsWithoutNewLine);
                line = block.LastLine + 1;
                delta += block.NewLines.Count - block.OldLines.Count;
            }

            for (; line <= end; line++)
            {
                AppendContext(builder, source, line, lastLine, endsWithNewLine);
            }

            index = last + 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 変わった行のまとまり。
    /// </summary>
    /// <param name="FirstLine">最初の行 (0 始まり)。</param>
    /// <param name="LastLine">最後の行 (0 始まり。この行を含む)。</param>
    /// <param name="OldLines">当てる前の行。</param>
    /// <param name="NewLines">当てた後の行。</param>
    /// <param name="OldEndsWithoutNewLine">当てる前の最後の行が、改行の無いファイルの終わりか。</param>
    /// <param name="NewEndsWithoutNewLine">当てた後の最後の行が、改行の無いファイルの終わりか。</param>
    private sealed record Block(
        int FirstLine, int LastLine, List<string> OldLines, List<string> NewLines, bool OldEndsWithoutNewLine, bool NewEndsWithoutNewLine);

    /// <summary>置き換えを、それが書き換える行ごとにまとめる。</summary>
    private static List<Block> BuildBlocks(SourceText source, FileFixes file)
    {
        List<(int First, int Last, List<TextReplacement> Replacements)> groups = [];

        foreach (TextReplacement replacement in file.Replacements)
        {
            int first = source.GetLinePosition(replacement.Span.Start).Line;
            int last = source.GetLinePosition(replacement.Span.End).Line;

            if (groups.Count > 0 && first <= groups[^1].Last)
            {
                (int f, int l, List<TextReplacement> list) = groups[^1];
                list.Add(replacement);
                groups[^1] = (f, Math.Max(l, last), list);
            }
            else
            {
                groups.Add((first, last, [replacement]));
            }
        }

        List<Block> blocks = [];

        foreach ((int first, int last, List<TextReplacement> replacements) in groups)
        {
            int start = source.GetLineSpan(first).Start;
            int end = last + 1 < source.LineCount ? source.GetLineSpan(last + 1).Start : source.Length;
            string before = source.Content[start..end];

            StringBuilder after = new();
            int position = start;

            foreach (TextReplacement replacement in replacements)
            {
                after.Append(source.Content, position, replacement.Span.Start - position).Append(replacement.NewText);
                position = replacement.Span.End;
            }

            after.Append(source.Content, position, end - position);

            List<string> oldLines = SplitLines(before, out bool oldOpen);
            List<string> newLines = SplitLines(after.ToString(), out bool newOpen);

            if (!oldLines.SequenceEqual(newLines, StringComparer.Ordinal) || oldOpen != newOpen)
            {
                blocks.Add(new Block(first, last, oldLines, newLines, oldOpen, newOpen));
            }
        }

        return blocks;
    }

    /// <summary>テキストの最後の行の番号 (0 始まり)。末尾が改行なら、その前の行。</summary>
    private static int LastLine(SourceText source)
        => source.Length > 0 && source[source.Length - 1] == '\n' ? Math.Max(0, source.LineCount - 2) : source.LineCount - 1;

    /// <summary>変わらない行を 1 つ書く。</summary>
    private static void AppendContext(StringBuilder builder, SourceText source, int line, int lastLine, bool endsWithNewLine)
    {
        builder.Append(' ').Append(source.GetLineText(line).TrimEnd('\r')).Append('\n');

        if (line == lastLine && !endsWithNewLine)
        {
            builder.Append("\\ No newline at end of file\n");
        }
    }

    /// <summary>変わった行を書く。改行の無いファイルの終わりには、その旨の行を続ける。</summary>
    private static void AppendLines(StringBuilder builder, char marker, List<string> lines, bool endsWithoutNewLine)
    {
        foreach (string line in lines)
        {
            builder.Append(marker).Append(line).Append('\n');
        }

        if (endsWithoutNewLine && lines.Count > 0)
        {
            builder.Append("\\ No newline at end of file\n");
        }
    }

    /// <summary>見出しの範囲 (1 始まり。行が無いときは前の行の番号)。</summary>
    private static string Range(int start, int count) => count == 0 ? $"{start},0" : $"{start + 1},{count}";

    /// <summary>行に分ける。行の終わりの <c>\r</c> は表示しないので除く。</summary>
    /// <param name="text">行の始まりから行の終わり (改行を含む) までの文字列。</param>
    /// <param name="endsWithoutNewLine">最後の行に改行が無いか (ファイルの終わり)。</param>
    private static List<string> SplitLines(string text, out bool endsWithoutNewLine)
    {
        List<string> lines = [.. text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l)];
        endsWithoutNewLine = text.Length > 0 && !text.EndsWith('\n');

        if (!endsWithoutNewLine)
        {
            // 末尾の改行の後ろの空の要素。
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
