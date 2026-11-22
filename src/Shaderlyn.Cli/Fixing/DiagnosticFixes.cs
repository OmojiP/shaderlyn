using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Cli.Fixing;

/// <summary>
/// 指摘が持つ修正を、1 つの並びにまとめる。
/// </summary>
/// <remarks>
/// <para>
/// ルールが修正を添える方法は 2 つある。指摘の範囲を置き換える <see cref="Diagnostic.SuggestedReplacement"/> と、
/// 範囲の外も書き換えられる <see cref="Diagnostic.Fixes"/> である。
/// エディタのコードアクション、CLI の <c>--fix</c>、JSON・SARIF の出力は、どれもここを通して同じ並びを使う。
/// 出力ごとに組み立てると、片方にだけ現れる修正ができる。
/// </para>
/// <para>
/// <see cref="Diagnostic.SuggestedReplacement"/> は既定の候補として扱う。
/// ルールの手引きで、修正が一意に決まるときだけ添えるものとしている。
/// </para>
/// </remarks>
internal static class DiagnosticFixes
{
    /// <summary>
    /// 指摘の修正を列挙する。
    /// </summary>
    /// <param name="diagnostic">対象の指摘。</param>
    /// <param name="textLength">修正を当てるテキストの長さ。範囲がこの外に出る修正は除く。</param>
    /// <returns>修正。<see cref="Diagnostic.SuggestedReplacement"/> から作ったものが先頭に来る。</returns>
    /// <remarks>
    /// 範囲がテキストの外に出るのは、解析の後にテキストが変わった場合である (キャッシュから戻した指摘など)。
    /// </remarks>
    public static ImmutableArray<DiagnosticFix> Collect(Diagnostic diagnostic, int textLength)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        ImmutableArray<DiagnosticFix>.Builder fixes = ImmutableArray.CreateBuilder<DiagnosticFix>();

        if (diagnostic.SuggestedReplacement is { } replacement && diagnostic.Location.Span.End <= textLength)
        {
            // 置き換える文字列が既に引用符を持つ場合、さらに囲むと読みにくくなる。
            string shown = replacement.StartsWith('"') ? replacement : $"'{replacement}'";
            fixes.Add(DiagnosticFix.Replace($"{shown} に修正する", diagnostic.Location.Span, replacement));
        }

        foreach (DiagnosticFix fix in diagnostic.Fixes)
        {
            if (fix.Replacements.All(r => r.Span.End <= textLength))
            {
                fixes.Add(fix);
            }
        }

        return fixes.ToImmutable();
    }

    /// <summary>同じ修正を 1 つにまとめるための鍵。</summary>
    /// <param name="fix">対象の修正。</param>
    /// <returns>題名と置き換えから作った文字列。</returns>
    /// <remarks>
    /// シンボルの組み合わせごとに解析するので、同じ位置の同じ指摘が同じ修正を持って何度も現れる。
    /// </remarks>
    public static string KeyOf(DiagnosticFix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);
        return fix.Title + "\n" + string.Join("\n", fix.Replacements.Select(e => $"{e.Span.Start}:{e.Span.Length}:{e.NewText}"));
    }
}
