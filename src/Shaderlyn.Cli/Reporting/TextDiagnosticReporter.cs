using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Cli.Reporting;

/// <summary>
/// 診断を人間が読むための整形テキストとして出力する。
/// </summary>
/// <remarks>
/// <para>
/// 1 件目の行は <c>パス(行,桁): 重要度 ID: メッセージ</c> という、
/// コンパイラの慣習的な書式に揃えている。多くのエディタとターミナルが
/// この書式をファイル位置として認識し、クリックで飛べるようにしてくれるためである。
/// </para>
/// <para>
/// 続けて該当行のソースと下線を表示する。指摘だけを見せられても
/// 「どのコードのことか」を探す手間が残り、修正されずに放置されやすいためである。
/// </para>
/// <para>
/// メッセージは 1 文ずつ改行する。「何が起きたか」「なぜか」「どうすればよいか」を
/// 1 行に詰め込むと、端末の折り返しに任せることになり、
/// どこが文の切れ目かも分からないまま読み飛ばされる。
/// </para>
/// </remarks>
internal sealed class TextDiagnosticReporter
{
    /// <summary>2 文目以降の行頭に付ける字下げ。</summary>
    /// <remarks>
    /// 見出し行の続きであることが分かればよい。
    /// メッセージの開始位置に揃えるとパスの長さの分だけ右に寄り、かえって読みにくい。
    /// </remarks>
    private const string MessageIndent = "    ";

    private readonly bool _useColor;

    /// <summary>
    /// テキストレポータを生成する。
    /// </summary>
    /// <param name="useColor">ANSI エスケープシーケンスによる色付けを行うかどうか。</param>
    public TextDiagnosticReporter(bool useColor) => _useColor = useColor;

    /// <summary>
    /// 診断の一覧を整形して返す。
    /// </summary>
    /// <param name="diagnostics">出力する診断。ソースコード上の出現順に整列済みであることを前提とする。</param>
    /// <param name="analyzedFileCount">解析したファイル数。要約行に表示する。</param>
    /// <returns>整形済みのテキスト。</returns>
    public string Format(ImmutableArray<Diagnostic> diagnostics, int analyzedFileCount)
    {
        StringBuilder builder = new();

        foreach (Diagnostic diagnostic in diagnostics)
        {
            AppendDiagnostic(builder, diagnostic);
            builder.AppendLine();
        }

        AppendSummary(builder, diagnostics, analyzedFileCount);
        return builder.ToString();
    }

    /// <summary>
    /// 診断 1 件を、位置の見出し行・該当行のソース・下線の 3 行構成で追記する。
    /// </summary>
    /// <param name="builder">追記先。</param>
    /// <param name="diagnostic">出力する診断。</param>
    private void AppendDiagnostic(StringBuilder builder, Diagnostic diagnostic)
    {
        LinePositionSpan lineSpan = diagnostic.Location.LineSpan;
        string severityText = diagnostic.Severity.ToString().ToLowerInvariant();

        builder.Append(CultureInfo.InvariantCulture, $"{diagnostic.Location.FilePath}({lineSpan.Start}): ");
        builder.Append(Colorize(severityText, SeverityColor(diagnostic.Severity)));
        builder.Append(CultureInfo.InvariantCulture, $" {diagnostic.Id}: ");
        AppendMessage(builder, diagnostic.GetMessage());

        AppendSourceSnippet(builder, diagnostic, lineSpan);
    }

    /// <summary>
    /// メッセージを 1 文ずつ改行して追記する。
    /// </summary>
    /// <param name="builder">追記先。</param>
    /// <param name="message">出力するメッセージ。</param>
    /// <remarks>
    /// 1 文目は見出し行の続きとしてそのまま置く。
    /// 位置とメッセージが同じ行に並ぶ書式はエディタがファイル位置として認識するため、
    /// ここを崩すとクリックで飛べなくなる。
    /// </remarks>
    private static void AppendMessage(StringBuilder builder, string message)
    {
        bool isFirst = true;

        foreach (string sentence in SplitIntoSentences(message))
        {
            if (!isFirst)
            {
                builder.Append(MessageIndent);
            }

            builder.AppendLine(sentence);
            isFirst = false;
        }

        if (isFirst)
        {
            builder.AppendLine();
        }
    }

    /// <summary>
    /// メッセージを文の区切りで分ける。
    /// </summary>
    /// <param name="message">分けるメッセージ。</param>
    /// <returns>句点までを 1 つとした文の並び。空の文は含まない。</returns>
    /// <remarks>
    /// 端末の幅で折り返すのではなく文で分ける。
    /// 幅で折ると、環境によって切れ目が変わり、
    /// 出力を貼り付けて共有したときに読み手ごとに違う形になる。
    /// </remarks>
    private static IEnumerable<string> SplitIntoSentences(string message)
    {
        int start = 0;

        for (int i = 0; i < message.Length; i++)
        {
            if (message[i] != '。')
            {
                continue;
            }

            string sentence = message[start..(i + 1)].Trim();
            start = i + 1;

            if (sentence.Length > 0)
            {
                yield return sentence;
            }
        }

        string rest = message[start..].Trim();

        if (rest.Length > 0)
        {
            yield return rest;
        }
    }

    /// <summary>
    /// 指摘箇所のソース行と、その下に指摘範囲を示す下線を追記する。
    /// </summary>
    /// <param name="builder">追記先。</param>
    /// <param name="diagnostic">出力する診断。</param>
    /// <param name="lineSpan">診断の行・桁範囲。</param>
    /// <remarks>
    /// 複数行にまたがる指摘は開始行だけを表示し、下線はその行の末尾までとする。
    /// 長大なブロック全体を出力しても読み手の役に立たないためである。
    /// タブは桁がずれるので、下線側でもタブをそのまま使って位置を合わせる。
    /// </remarks>
    private void AppendSourceSnippet(StringBuilder builder, Diagnostic diagnostic, LinePositionSpan lineSpan)
    {
        string lineText = diagnostic.Location.GetLineText();
        int lineNumber = lineSpan.Start.Line + 1;
        string gutter = lineNumber.ToString(CultureInfo.InvariantCulture);
        string gutterPad = new(' ', gutter.Length);

        builder.Append(CultureInfo.InvariantCulture, $" {gutter} | {lineText}");
        builder.AppendLine();

        int underlineStart = Math.Min(lineSpan.Start.Character, lineText.Length);
        int underlineLength = lineSpan.IsSingleLine
            ? Math.Max(1, lineSpan.End.Character - lineSpan.Start.Character)
            : Math.Max(1, lineText.Length - underlineStart);
        underlineLength = Math.Min(underlineLength, Math.Max(1, lineText.Length - underlineStart));

        builder.Append(CultureInfo.InvariantCulture, $" {gutterPad} | ");

        // 下線の開始位置を合わせるため、元の行のタブはタブのまま複写する。
        for (int i = 0; i < underlineStart; i++)
        {
            builder.Append(lineText[i] == '\t' ? '\t' : ' ');
        }

        builder.Append(Colorize(new string('^', underlineLength), SeverityColor(diagnostic.Severity)));
        builder.AppendLine();
    }

    /// <summary>
    /// 重要度ごとの件数と解析ファイル数からなる要約行を追記する。
    /// </summary>
    /// <param name="builder">追記先。</param>
    /// <param name="diagnostics">出力した診断。</param>
    /// <param name="analyzedFileCount">解析したファイル数。</param>
    private static void AppendSummary(StringBuilder builder, ImmutableArray<Diagnostic> diagnostics, int analyzedFileCount)
    {
        int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int infos = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info);

        builder.Append(CultureInfo.InvariantCulture,
            $"{analyzedFileCount} ファイルを解析: エラー {errors} 件, 警告 {warnings} 件, 情報 {infos} 件");
        builder.AppendLine();
    }

    /// <summary>重要度に対応する ANSI 色コードを返す。</summary>
    /// <param name="severity">対象の重要度。</param>
    /// <returns>ANSI 色コード。</returns>
    private static string SeverityColor(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "31",   // 赤
        DiagnosticSeverity.Warning => "33", // 黄
        _ => "36",                          // シアン
    };

    /// <summary>
    /// 色付けが有効な場合のみ、文字列を ANSI エスケープシーケンスで囲む。
    /// </summary>
    /// <param name="text">対象の文字列。</param>
    /// <param name="colorCode">ANSI 色コード。</param>
    /// <returns>色付けした文字列、または元の文字列。</returns>
    private string Colorize(string text, string colorCode)
        => _useColor ? $"{Escape}[{colorCode}m{text}{Escape}[0m" : text;

    /// <summary>ANSI エスケープシーケンスの開始文字。</summary>
    /// <remarks>
    /// ソース中に生の ESC バイトを置くとエディタや差分ツールで壊れやすいため、
    /// 定数として明示的に定義する。
    /// </remarks>
    private static readonly string Escape = ((char)0x1B).ToString();
}
