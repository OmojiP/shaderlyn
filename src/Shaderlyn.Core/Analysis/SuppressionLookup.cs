using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Core.Analysis;

/// <summary>
/// ソースコード中の抑制コメントを集めて、どの指摘を抑制するかを判定する。
/// </summary>
/// <remarks>
/// <para>
/// 抑制コメントは 5 種類ある。いずれもコメントの中に書く。
/// </para>
/// <list type="table">
///   <item>
///     <term><c>shaderlyn-disable-next-line [ID...]</c></term>
///     <description>次の 1 行を抑制する</description>
///   </item>
///   <item>
///     <term><c>shaderlyn-disable-line [ID...]</c></term>
///     <description>その行を抑制する (行末コメントとして書く)</description>
///   </item>
///   <item>
///     <term><c>shaderlyn-disable [ID...]</c></term>
///     <description>その行以降を抑制する</description>
///   </item>
///   <item>
///     <term><c>shaderlyn-enable [ID...]</c></term>
///     <description>抑制を解除する</description>
///   </item>
///   <item>
///     <term><c>shaderlyn-disable-file [ID...]</c></term>
///     <description>ファイル全体を抑制する</description>
///   </item>
/// </list>
/// <para>
/// ルール ID を省略すると、そのコメントはすべてのルールに効く。
/// </para>
/// <para>
/// <b>テキストとして走査する。</b> 構文木の trivia を辿らないのは、
/// ShaderLab と HLSL の両方に、さらに include されたファイルにも
/// 同じ仕組みで対応するためである。
/// 抑制の書き方が言語によって変わるのは利用者にとって理不尽であり、
/// 木の形に依存させると必ずそうなる。
/// </para>
/// </remarks>
internal sealed class SuppressionLookup
{
    /// <summary>抑制コメントの目印。</summary>
    private const string Marker = "shaderlyn-";

    /// <summary>
    /// ソーステキストごとの索引を共有するための保管庫。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 同じヘッダが多数のファイルから取り込まれるため、
    /// 索引を毎回作り直すと走査の費用が積み上がる。
    /// キーは参照であり、内容の比較ではない。
    /// </para>
    /// <para>
    /// <b>キーを弱参照で持つ。</b>
    /// この保管庫は <see langword="static"/> であり、プロセスの寿命まで残る。
    /// 編集のたびに新しいソーステキストができる常駐プロセスで強参照にすると、
    /// 一度解析したテキストがすべて解放されなくなる。
    /// </para>
    /// </remarks>
    private static readonly ConditionalWeakTable<SourceText, SuppressionLookup> Cache = [];

    private readonly ImmutableArray<SuppressionRange> _ranges;

    private SuppressionLookup(ImmutableArray<SuppressionRange> ranges) => _ranges = ranges;

    /// <summary>抑制が 1 つも書かれていないかどうか。</summary>
    public bool IsEmpty => _ranges.IsEmpty;

    /// <summary>
    /// 抑制が有効な範囲 1 つ。
    /// </summary>
    /// <param name="StartLine">開始行 (この行を含む)。</param>
    /// <param name="EndLine">終了行 (この行を含む)。</param>
    /// <param name="RuleId">対象のルール ID。すべてのルールが対象の場合は <see langword="null"/>。</param>
    private readonly record struct SuppressionRange(int StartLine, int EndLine, string? RuleId);

    /// <summary>
    /// ソーステキストの索引を取得する。未作成であればここで走査する。
    /// </summary>
    /// <param name="text">対象のソーステキスト。</param>
    /// <returns>抑制の索引。</returns>
    public static SuppressionLookup GetOrCreate(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Cache.GetValue(text, static source => Build(source));
    }

    /// <summary>
    /// 指定した位置の指摘が抑制されているかを判定する。
    /// </summary>
    /// <param name="ruleId">ルール ID。</param>
    /// <param name="line">0 始まりの行番号。</param>
    /// <returns>抑制されている場合は <see langword="true"/>。</returns>
    public bool IsSuppressed(string ruleId, int line)
    {
        ArgumentNullException.ThrowIfNull(ruleId);

        foreach (SuppressionRange range in _ranges)
        {
            if (line >= range.StartLine
                && line <= range.EndLine
                && (range.RuleId is null || string.Equals(range.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ソーステキストを走査して抑制の範囲を組み立てる。
    /// </summary>
    /// <param name="text">対象のソーステキスト。</param>
    /// <returns>組み立てた索引。</returns>
    private static SuppressionLookup Build(SourceText text)
    {
        ImmutableArray<SuppressionRange>.Builder ranges = ImmutableArray.CreateBuilder<SuppressionRange>();

        // 「ここから抑制」に対応する解除がまだ現れていないもの。
        // ルール ID ごとに開始行を覚えておく。null のキーはすべてのルールを表す。
        Dictionary<string, int> openRanges = new(StringComparer.OrdinalIgnoreCase);
        const string allRulesKey = "\0all";

        for (int line = 0; line < text.LineCount; line++)
        {
            string content = text.GetLineText(line);

            if (!TryReadDirective(content, out string kind, out ImmutableArray<string> ruleIds))
            {
                continue;
            }

            switch (kind)
            {
                case "disable-file":
                    AddRanges(ranges, 0, int.MaxValue, ruleIds);
                    break;

                case "disable-next-line":
                    AddRanges(ranges, line + 1, line + 1, ruleIds);
                    break;

                case "disable-line":
                    AddRanges(ranges, line, line, ruleIds);
                    break;

                case "disable":
                    foreach (string key in EnumerateKeys(ruleIds, allRulesKey))
                    {
                        openRanges.TryAdd(key, line);
                    }

                    break;

                case "enable":
                    foreach (string key in EnumerateKeys(ruleIds, allRulesKey))
                    {
                        if (openRanges.Remove(key, out int start))
                        {
                            ranges.Add(new SuppressionRange(
                                start, line, key == allRulesKey ? null : key));
                        }
                    }

                    break;
            }
        }

        // 解除されなかった「ここから抑制」はファイル末尾まで有効とする。
        foreach ((string key, int start) in openRanges)
        {
            ranges.Add(new SuppressionRange(start, int.MaxValue, key == allRulesKey ? null : key));
        }

        return new SuppressionLookup(ranges.ToImmutable());
    }

    /// <summary>ルール ID の一覧を、索引のキーへ変換する。</summary>
    /// <param name="ruleIds">書かれていたルール ID。空の場合はすべてのルールが対象。</param>
    /// <param name="allRulesKey">すべてのルールを表すキー。</param>
    /// <returns>キーの列。</returns>
    private static IEnumerable<string> EnumerateKeys(ImmutableArray<string> ruleIds, string allRulesKey)
        => ruleIds.IsEmpty ? [allRulesKey] : ruleIds;

    /// <summary>抑制の範囲を、対象のルールごとに追加する。</summary>
    /// <param name="ranges">追加先。</param>
    /// <param name="startLine">開始行。</param>
    /// <param name="endLine">終了行。</param>
    /// <param name="ruleIds">対象のルール ID。空の場合はすべてのルール。</param>
    private static void AddRanges(
        ImmutableArray<SuppressionRange>.Builder ranges,
        int startLine,
        int endLine,
        ImmutableArray<string> ruleIds)
    {
        if (ruleIds.IsEmpty)
        {
            ranges.Add(new SuppressionRange(startLine, endLine, null));
            return;
        }

        foreach (string ruleId in ruleIds)
        {
            ranges.Add(new SuppressionRange(startLine, endLine, ruleId));
        }
    }

    /// <summary>
    /// 1 行から抑制コメントを読み取る。
    /// </summary>
    /// <param name="content">行の内容。</param>
    /// <param name="kind">読み取った指示の種類。</param>
    /// <param name="ruleIds">読み取ったルール ID。</param>
    /// <returns>抑制コメントであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>コメントの中にあることを確かめる。</b>
    /// 目印を含む文字列リテラルを抑制コメントと誤認すると、
    /// 意図せず指摘が消える。指摘が消えたことには誰も気づけない。
    /// </remarks>
    private static bool TryReadDirective(string content, out string kind, out ImmutableArray<string> ruleIds)
    {
        kind = string.Empty;
        ruleIds = [];

        int markerIndex = content.IndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0 || !IsInsideComment(content, markerIndex))
        {
            return false;
        }

        ReadOnlySpan<char> rest = content.AsSpan(markerIndex + Marker.Length);

        int nameLength = 0;
        while (nameLength < rest.Length && (char.IsAsciiLetter(rest[nameLength]) || rest[nameLength] == '-'))
        {
            nameLength++;
        }

        kind = rest[..nameLength].ToString();

        if (kind is not ("disable" or "enable" or "disable-line" or "disable-next-line" or "disable-file"))
        {
            return false;
        }

        ImmutableArray<string>.Builder ids = ImmutableArray.CreateBuilder<string>();

        // ルール ID はカンマまたは空白で区切って並べられる。
        foreach (string token in rest[nameLength..].ToString()
            .Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            // コメントの続きに説明を書けるようにする。
            // ルール ID の形をしていないものが現れたら、そこから先は説明とみなす。
            if (!LooksLikeRuleId(token))
            {
                break;
            }

            ids.Add(token);
        }

        ruleIds = ids.ToImmutable();
        return true;
    }

    /// <summary>
    /// 目印がコメントの中にあるかを判定する。
    /// </summary>
    /// <param name="content">行の内容。</param>
    /// <param name="markerIndex">目印の位置。</param>
    /// <returns>コメントの中であれば <see langword="true"/>。</returns>
    /// <remarks>
    /// 同じ行の目印より前に <c>//</c> か <c>/*</c> があることを確かめる。
    /// 複数行コメントの途中の行までは追わない。
    /// 抑制コメントを複数行コメントの中に書く必然性が無いためである。
    /// </remarks>
    private static bool IsInsideComment(string content, int markerIndex)
    {
        for (int i = 0; i + 1 < markerIndex; i++)
        {
            if (content[i] == '/' && content[i + 1] is '/' or '*')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 文字列がルール ID の形をしているかを判定する。
    /// </summary>
    /// <param name="token">判定する文字列。</param>
    /// <returns>ルール ID の形であれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <see cref="Diagnostics.DiagnosticDescriptor"/> と同じ「英大文字の接頭辞 + 4 桁」の形で判定する。
    /// </remarks>
    private static bool LooksLikeRuleId(string token)
    {
        if (token.Length < 5)
        {
            return false;
        }

        int digits = 0;
        while (digits < 4 && char.IsAsciiDigit(token[^(digits + 1)]))
        {
            digits++;
        }

        if (digits != 4)
        {
            return false;
        }

        foreach (char c in token.AsSpan(0, token.Length - 4))
        {
            if (!char.IsAsciiLetterUpper(c))
            {
                return false;
            }
        }

        return true;
    }
}
