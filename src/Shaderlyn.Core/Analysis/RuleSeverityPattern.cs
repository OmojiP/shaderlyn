using System.Text.RegularExpressions;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Core.Analysis;

/// <summary>
/// ルール ID の正規表現と、それに当たるルールの重要度 (設定ファイルの <c>rules:</c> の <c>/SL.*/: error</c>)。
/// </summary>
/// <remarks>
/// <para>
/// <b>ID の全体と一致したものだけを当たりとする。</b>
/// 部分一致にすると、<c>/L00/</c> のような書き方が思わぬ ID (<c>SL0001</c> と <c>CF0001</c>) に当たる。
/// 大文字と小文字は区別しない (ID をそのまま書いた設定と同じ)。
/// </para>
/// </remarks>
public sealed class RuleSeverityPattern
{
    /// <summary>照合にかける時間の上限。設定に書かれた式が極端に遅くても、解析を止めない。</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Regex _regex;

    /// <summary>正規表現と重要度から作る。</summary>
    /// <param name="pattern">ルール ID の正規表現 (<c>/</c> で囲まない中身)。</param>
    /// <param name="severity">当たったルールの重要度。</param>
    /// <exception cref="ArgumentException">正規表現として読めない。</exception>
    public RuleSeverityPattern(string pattern, DiagnosticSeverity severity)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        Pattern = pattern;
        Severity = severity;
        _regex = new Regex(
            $"^(?:{pattern})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            MatchTimeout);
    }

    /// <summary>正規表現 (<c>/</c> で囲まない中身)。</summary>
    public string Pattern { get; }

    /// <summary>当たったルールの重要度。</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>ルール ID に当たるかを判定する。</summary>
    /// <param name="ruleId">ルール ID。</param>
    /// <returns>ID の全体と一致すれば <see langword="true"/>。照合が時間切れなら <see langword="false"/>。</returns>
    public bool Matches(string ruleId)
    {
        ArgumentNullException.ThrowIfNull(ruleId);

        try
        {
            return _regex.IsMatch(ruleId);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
