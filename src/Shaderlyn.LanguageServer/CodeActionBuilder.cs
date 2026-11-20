using System.Collections.Immutable;
using Shaderlyn.Cli.Fixing;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// エディタが 1 回の操作で適用できる修正 1 つ分。
/// </summary>
/// <param name="Title">操作の名前。</param>
/// <param name="Edits">置き換えの並び。重ならない。</param>
/// <param name="IsPreferred">既定の候補として扱ってよいか。</param>
internal readonly record struct CodeActionEdit(string Title, ImmutableArray<TextReplacement> Edits, bool IsPreferred)
{
    /// <summary>同じ修正を 1 つにまとめるための鍵。</summary>
    public string Key => Title + "\n" + string.Join("\n", Edits.Select(e => $"{e.Span.Start}:{e.Span.Length}:{e.NewText}"));
}

/// <summary>
/// 指摘に対して提示できる修正を組み立てる。
/// </summary>
/// <remarks>
/// <para>
/// <b>修正はルールが決める (<see cref="Diagnostic.SuggestedReplacement"/> と <see cref="Diagnostic.Fixes"/>)。</b>
/// ここでは並べるだけで、メッセージの文面から修正の方法を読み取らない。
/// </para>
/// <para>
/// 抑制コメントはどの指摘にも提示する。
/// 直せない事情があるときに、理由を書いて抑制する手段は常に必要である。
/// </para>
/// </remarks>
internal static class CodeActionBuilder
{
    /// <summary>
    /// 指摘に対する修正を列挙する。
    /// </summary>
    /// <param name="text">対象のソーステキスト。</param>
    /// <param name="diagnostic">対象の指摘。</param>
    /// <returns>提示する修正。</returns>
    public static ImmutableArray<CodeActionEdit> Build(SourceText text, Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(diagnostic);

        ImmutableArray<CodeActionEdit>.Builder actions = ImmutableArray.CreateBuilder<CodeActionEdit>();

        // CLI の --fix と JSON・SARIF の出力と、同じ並びを使う。
        foreach (DiagnosticFix fix in DiagnosticFixes.Collect(diagnostic, text.Length))
        {
            actions.Add(new CodeActionEdit(fix.Title, fix.Replacements, fix.IsPreferred));
        }

        actions.Add(CreateSuppression(text, diagnostic));
        return actions.ToImmutable();
    }

    /// <summary>
    /// 抑制コメントを差し込む修正を作る。
    /// </summary>
    /// <param name="text">対象のソーステキスト。</param>
    /// <param name="diagnostic">対象の指摘。</param>
    /// <returns>作った修正。</returns>
    /// <remarks>
    /// <para>
    /// 指摘の行と同じ字下げでその上の行へ入れる。
    /// 字下げが崩れたコメントは、後から読む人に「機械が書いたもの」として軽く扱われる。
    /// </para>
    /// <para>
    /// <b>ルール ID を必ず書く。</b>
    /// ID を省くとその行のすべての指摘が抑制される。
    /// 1 つを抑制するつもりで、まだ見ぬ指摘まで巻き添えにしてはならない。
    /// </para>
    /// </remarks>
    private static CodeActionEdit CreateSuppression(SourceText text, Diagnostic diagnostic)
    {
        int line = diagnostic.Location.LineSpan.Start.Line;
        TextSpan lineSpan = text.GetLineSpan(line);
        string lineText = text.ToString(lineSpan);
        string indent = lineText[..(lineText.Length - lineText.TrimStart().Length)];

        TextSpan insertAt = new(lineSpan.Start, 0);
        string comment = $"{indent}// shaderlyn-disable-next-line {diagnostic.Id}{Environment.NewLine}";

        return new CodeActionEdit(
            $"この行で {diagnostic.Id} を抑制する", [new TextReplacement(insertAt, comment)], IsPreferred: false);
    }
}
