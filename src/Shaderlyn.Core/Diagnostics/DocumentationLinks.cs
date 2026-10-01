namespace Shaderlyn.Core.Diagnostics;

/// <summary>
/// ルールの説明ドキュメントへの URL を組み立てる。
/// </summary>
/// <remarks>
/// <para>
/// SARIF の <c>helpUri</c> として出力され、GitHub のアラート画面から辿れるようになる。
/// 「なぜ問題なのか」を読めるようにしておかないと、指摘は理解されないまま抑制される。
/// </para>
/// <para>
/// 基底 URL をここ 1 か所に置いているのは、リポジトリの移動や
/// ドキュメント配置の変更に追従する箇所を 1 つに保つためである。
/// </para>
/// </remarks>
internal static class DocumentationLinks
{
    /// <summary>ルールドキュメントが置かれている場所の基底 URL。</summary>
    private const string BaseUrl =
        "https://github.com/OmojiP/shaderlyn/blob/main/docs/rules/";

    /// <summary>
    /// 指定したルール ID に対応するドキュメントの URL を返す。
    /// </summary>
    /// <param name="ruleId">ルール ID。</param>
    /// <returns>ドキュメントの URL。</returns>
    public static string For(string ruleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        return $"{BaseUrl}{ruleId}.md";
    }
}
