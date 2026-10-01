using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// エディタが 1 回の操作で適用できる直し 1 つ分。
/// </summary>
/// <param name="Title">操作の名前。</param>
/// <param name="Span">置き換える範囲。</param>
/// <param name="NewText">置き換える文字列。</param>
/// <param name="IsPreferred">既定の候補として扱ってよいか。</param>
internal readonly record struct CodeActionEdit(string Title, TextSpan Span, string NewText, bool IsPreferred);

/// <summary>
/// 指摘に対して提示できる直しを組み立てる。
/// </summary>
/// <remarks>
/// <para>
/// <b>直し方が一意に決まるものだけを提示する。</b>
/// エディタの直しは 1 回の操作で適用され、多くの場合は中身を確かめられない。
/// 「たぶんこうだろう」を提示すると、誤りが 1 打鍵で広がる。
/// </para>
/// <para>
/// 抑制コメントはどの指摘にも提示する。
/// 直せない事情があるときに、理由を書いて抑制する手段は常に要る。
/// </para>
/// </remarks>
internal static class CodeActionBuilder
{
    /// <summary>
    /// 指摘に対する直しを列挙する。
    /// </summary>
    /// <param name="text">対象のソーステキスト。</param>
    /// <param name="diagnostic">対象の指摘。</param>
    /// <returns>提示する直し。</returns>
    public static ImmutableArray<CodeActionEdit> Build(SourceText text, Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(diagnostic);

        ImmutableArray<CodeActionEdit>.Builder actions = ImmutableArray.CreateBuilder<CodeActionEdit>();

        if (diagnostic.SuggestedReplacement is { } replacement)
        {
            // 置き換える文字列が既に引用符を持つ場合、さらに囲むと読みにくくなる。
            string shown = replacement.StartsWith('"') ? replacement : $"'{replacement}'";

            actions.Add(new CodeActionEdit(
                $"{shown} に直す", diagnostic.Location.Span, replacement, IsPreferred: true));
        }

        actions.Add(CreateSuppression(text, diagnostic));
        return actions.ToImmutable();
    }

    /// <summary>
    /// 抑制コメントを差し込む直しを作る。
    /// </summary>
    /// <param name="text">対象のソーステキスト。</param>
    /// <param name="diagnostic">対象の指摘。</param>
    /// <returns>作った直し。</returns>
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
            $"この行で {diagnostic.Id} を抑制する", insertAt, comment, IsPreferred: false);
    }
}
