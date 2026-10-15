using System.Collections.Frozen;

namespace Shaderlyn.Core.Diagnostics;

/// <summary>
/// ID を付け替えたルールの、前の ID から今の ID への表。
/// </summary>
/// <remarks>
/// <para>
/// 0.2.1 で、接頭辞の意味に合わせて ID を付け替えた。
/// <c>HL</c> は C 系の言語に共通、<c>SL</c> は ShaderLab、<c>UN</c> は Unity に固有 (コンパイラ・プリプロセッサ・読み込みの振る舞い) である。
/// </para>
/// <list type="bullet">
///   <item><description>どの環境でも出る解析状況の知らせ: <c>SL0002</c> / <c>SL0003</c> / <c>SL0004</c> → <c>HL0005</c> / <c>HL0006</c> / <c>HL0007</c></description></item>
///   <item><description>Unity に固有のもの: <c>SL1050</c>〜<c>SL1058</c> → <c>UN1050</c>〜<c>UN1058</c>、<c>HL0302</c> → <c>UN0302</c></description></item>
/// </list>
/// <para>
/// <b>前の ID も読む。</b> 抑制コメント・設定ファイルの <c>rules:</c>・ベースラインに書いた前の ID は、今の ID として扱う。
/// 書き換えなくても、付け替える前と同じに効く。
/// </para>
/// </remarks>
public static class RenamedRuleIds
{
    /// <summary>前の ID から今の ID への表。大文字と小文字は区別しない。</summary>
    public static FrozenDictionary<string, string> Map { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SL0002"] = "HL0005",
        ["SL0003"] = "HL0006",
        ["SL0004"] = "HL0007",
        ["HL0302"] = "UN0302",
        ["SL1050"] = "UN1050",
        ["SL1051"] = "UN1051",
        ["SL1052"] = "UN1052",
        ["SL1053"] = "UN1053",
        ["SL1054"] = "UN1054",
        ["SL1055"] = "UN1055",
        ["SL1056"] = "UN1056",
        ["SL1057"] = "UN1057",
        ["SL1058"] = "UN1058",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>前の ID なら今の ID に直す。それ以外はそのまま返す。</summary>
    /// <param name="ruleId">ルール ID。</param>
    /// <returns>今の ID。</returns>
    public static string Normalize(string ruleId)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        return Map.TryGetValue(ruleId, out string? current) ? current : ruleId;
    }
}
