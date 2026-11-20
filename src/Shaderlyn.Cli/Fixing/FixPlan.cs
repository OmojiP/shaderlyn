using System.Collections.Immutable;
using System.Text;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Cli.Fixing;

/// <summary>
/// 適用すると決めた修正 1 つ。
/// </summary>
/// <param name="RuleId">修正を添えたルールの ID。</param>
/// <param name="Fix">修正。</param>
internal readonly record struct PlannedFix(string RuleId, DiagnosticFix Fix);

/// <summary>
/// 1 つのファイルに適用する修正。
/// </summary>
/// <param name="FilePath">ファイルのフルパス。</param>
/// <param name="SourceLength">解析したときのテキストの長さ。書き込む前に、ファイルが変わっていないかの確認に使う。</param>
/// <param name="Fixes">適用する修正。置き換えはどれも重ならない。</param>
internal sealed record FileFixes(string FilePath, int SourceLength, ImmutableArray<PlannedFix> Fixes)
{
    /// <summary>置き換えを、前から順に並べたもの。</summary>
    public ImmutableArray<TextReplacement> Replacements { get; } =
        [.. Fixes.SelectMany(f => f.Fix.Replacements).OrderBy(r => r.Span.Start).ThenBy(r => r.Span.Length)];

    /// <summary>テキストに置き換えを当てる。</summary>
    /// <param name="content">元のテキスト。</param>
    /// <returns>当てた後のテキスト。</returns>
    public string Apply(string content)
    {
        StringBuilder builder = new(content.Length);
        int position = 0;

        foreach (TextReplacement replacement in Replacements)
        {
            builder.Append(content, position, replacement.Span.Start - position).Append(replacement.NewText);
            position = replacement.Span.End;
        }

        return builder.Append(content, position, content.Length - position).ToString();
    }
}

/// <summary>
/// CLI の <c>--fix</c> / <c>--fix-dry-run</c> で適用する修正を決める。
/// </summary>
/// <remarks>
/// <para>
/// <b>確認せずにファイルを書き換えるので、書き手の意図が変わらない修正だけを選ぶ。</b>
/// </para>
/// <list type="bullet">
/// <item>既定の候補 (<see cref="DiagnosticFix.IsPreferred"/>) がちょうど 1 つある修正を選ぶ。</item>
/// <item>
/// 既定の候補が無く、候補が 1 つだけの修正 (SL1003 の型の修正など) は、そのルールを <c>--fix-rule</c> で指定したときだけ選ぶ。
/// 利用者がそのルールの修正を確認して頼んだものとして扱う。
/// 候補が 2 つ以上ある修正は、どちらにするかを決められないので選ばない。
/// </item>
/// <item>解析の対象のファイルだけを書き換える。取り込んだヘッダ (パッケージや Unity 同梱のものを含む) は書き換えない。</item>
/// <item>
/// 同じ位置を書き換える修正が 2 つあれば、前のものだけを選ぶ。両方を当てると、どちらの意図とも違う文になる。
/// 残った指摘は、修正を当てた後の解析で報告される。
/// </item>
/// </list>
/// </remarks>
internal sealed class FixPlan
{
    private FixPlan(ImmutableArray<FileFixes> files, int ambiguous, int outsideTargets, int conflicting)
    {
        Files = files;
        AmbiguousCount = ambiguous;
        OutsideTargetCount = outsideTargets;
        ConflictingCount = conflicting;
    }

    /// <summary>ファイルごとの、適用する修正。パスの順に並ぶ。</summary>
    public ImmutableArray<FileFixes> Files { get; }

    /// <summary>適用する修正の数。</summary>
    public int FixCount => Files.Sum(f => f.Fixes.Length);

    /// <summary>候補を 1 つに決められず、選ばなかった指摘の数。</summary>
    public int AmbiguousCount { get; }

    /// <summary>解析の対象の外 (取り込んだヘッダ) にあり、選ばなかった修正の数。</summary>
    public int OutsideTargetCount { get; }

    /// <summary>ほかの修正と同じ位置を書き換えるので、選ばなかった修正の数。</summary>
    public int ConflictingCount { get; }

    /// <summary>
    /// 指摘から、適用する修正を決める。
    /// </summary>
    /// <param name="diagnostics">指摘。ベースラインと抑制コメントで除いた後のもの。</param>
    /// <param name="targetFiles">解析の対象のファイル (フルパス)。</param>
    /// <param name="ruleIds"><c>--fix-rule</c> で指定したルール ID。空ならすべてのルール。</param>
    /// <returns>適用する修正。</returns>
    public static FixPlan Create(
        IEnumerable<Diagnostic> diagnostics,
        IReadOnlySet<string> targetFiles,
        IReadOnlySet<string> ruleIds)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(targetFiles);
        ArgumentNullException.ThrowIfNull(ruleIds);

        int ambiguous = 0;
        int outside = 0;
        int conflicting = 0;
        Dictionary<string, List<PlannedFix>> byFile = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> lengths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (Diagnostic diagnostic in diagnostics)
        {
            bool named = ruleIds.Contains(diagnostic.Id);

            if (ruleIds.Count > 0 && !named)
            {
                continue;
            }

            ImmutableArray<DiagnosticFix> candidates = DiagnosticFixes.Collect(diagnostic, diagnostic.Location.Source.Length);

            if (candidates.IsEmpty)
            {
                continue;
            }

            if (Choose(candidates, named) is not { } chosen)
            {
                ambiguous++;
                continue;
            }

            string path = Path.GetFullPath(diagnostic.Location.FilePath);
            string key = path + "\n" + DiagnosticFixes.KeyOf(chosen);

            if (!seen.Add(key))
            {
                continue;
            }

            if (!targetFiles.Contains(path))
            {
                outside++;
                continue;
            }

            if (!byFile.TryGetValue(path, out List<PlannedFix>? list))
            {
                byFile[path] = list = [];
                lengths[path] = diagnostic.Location.Source.Length;
            }

            list.Add(new PlannedFix(diagnostic.Id, chosen));
        }

        ImmutableArray<FileFixes>.Builder files = ImmutableArray.CreateBuilder<FileFixes>();

        foreach ((string path, List<PlannedFix> fixes) in byFile.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            List<PlannedFix> accepted = [];
            List<TextReplacement> taken = [];

            foreach (PlannedFix fix in fixes.OrderBy(f => f.Fix.Replacements.Min(r => r.Span.Start)))
            {
                if (fix.Fix.Replacements.Any(r => taken.Any(t => Conflicts(r, t))))
                {
                    conflicting++;
                    continue;
                }

                accepted.Add(fix);
                taken.AddRange(fix.Fix.Replacements);
            }

            files.Add(new FileFixes(path, lengths[path], [.. accepted]));
        }

        return new FixPlan(files.ToImmutable(), ambiguous, outside, conflicting);
    }

    /// <summary>候補から、適用する修正を 1 つ選ぶ。</summary>
    /// <param name="candidates">指摘の修正。</param>
    /// <param name="named">そのルールを <c>--fix-rule</c> で指定したか。</param>
    /// <returns>選んだ修正。決められなければ <see langword="null"/>。</returns>
    private static DiagnosticFix? Choose(ImmutableArray<DiagnosticFix> candidates, bool named)
    {
        DiagnosticFix[] preferred = [.. candidates.Where(f => f.IsPreferred)];

        return preferred.Length switch
        {
            1 => preferred[0],
            0 when named && candidates.Length == 1 => candidates[0],
            _ => null,
        };
    }

    /// <summary>
    /// 2 つの置き換えが同じ位置を書き換えるか。
    /// </summary>
    /// <remarks>
    /// 範囲が重なるものに加えて、同じ位置への差し込み 2 つと、置き換える範囲の内側への差し込みも重なるものとする。
    /// どちらも当てる順で結果が変わる。
    /// </remarks>
    private static bool Conflicts(TextReplacement left, TextReplacement right)
    {
        if (left.Span.Start == right.Span.Start)
        {
            return true;
        }

        return left.Span.Start < right.Span.End && right.Span.Start < left.Span.End
            || left.Span.Length == 0 && right.Span.Start < left.Span.Start && left.Span.Start < right.Span.End
            || right.Span.Length == 0 && left.Span.Start < right.Span.Start && right.Span.Start < left.Span.End;
    }
}
