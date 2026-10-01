using System.Security.Cryptography;
using System.Text;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Cli;

/// <summary>
/// 診断を、行番号に依存しない形で識別するための指紋。
/// </summary>
/// <remarks>
/// <para>
/// ベースラインの照合と SARIF の <c>partialFingerprints</c> の両方で使う。
/// どちらも目的は同じで、<b>コードが動いても同じ指摘を同じものと認識する</b>ことである。
/// </para>
/// <list type="bullet">
///   <item><description>
///     ベースライン: 無関係な行を足しただけで凍結が外れては、導入の目的を果たせない
///   </description></item>
///   <item><description>
///     SARIF: 行がずれるたびに GitHub が新しいアラートを立てると、
///     同じ問題の指摘が重複して積み上がる
///   </description></item>
/// </list>
/// <para>
/// 両者で違う計算をすると、片方だけが外れるという分かりにくい挙動になる。
/// </para>
/// </remarks>
internal static class DiagnosticFingerprint
{
    /// <summary>
    /// 指摘が出ている行の内容から指紋を計算する。
    /// </summary>
    /// <param name="diagnostic">対象の診断。</param>
    /// <returns>16 桁の 16 進数。</returns>
    /// <remarks>
    /// <para>
    /// 空白の量を揃えてから計算する。字下げを直しただけで
    /// 一致しなくなるのは利用者にとって理不尽である。
    /// </para>
    /// <para>
    /// <b><see cref="string.GetHashCode()"/> は使えない。</b>
    /// .NET の文字列ハッシュはプロセスごとに変わるため、
    /// 実行のたびに違う値が出力される。
    /// </para>
    /// </remarks>
    public static string ComputeSnippetHash(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeWhitespace(diagnostic.Location.GetLineText())));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// 連続する空白を 1 つにまとめ、前後の空白を落とす。
    /// </summary>
    /// <param name="text">対象の文字列。</param>
    /// <returns>正規化した文字列。</returns>
    private static string NormalizeWhitespace(string text)
    {
        StringBuilder normalized = new(text.Length);
        bool lastWasSpace = true;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    normalized.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            normalized.Append(c);
            lastWasSpace = false;
        }

        return normalized.ToString().TrimEnd();
    }
}
