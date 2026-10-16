using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Core.Diagnostics;

/// <summary>
/// ヘッダの中の指摘が、解析したファイルからどの <c>#include</c> をたどって取り込まれたか。
/// </summary>
/// <param name="FilePath">解析したファイル (<c>.shader</c> など)。</param>
/// <param name="Origin">そのファイルの中のどこから取り込んだか (<c>Pass "Forward"</c> など)。分からなければ <see langword="null"/>。</param>
/// <param name="Includes">
/// たどった <c>#include</c> の位置。先頭は解析したファイルに書いたもので、末尾は指摘のあるヘッダを取り込んだもの。
/// </param>
/// <remarks>
/// <para>
/// <b>ヘッダの指摘は、どのシェーダーから来たかが分からないと直しにくい。</b>
/// 共通のヘッダの指摘は、取り込む側の構成 (キーワード) や先に定義したマクロで中身が変わる。
/// 指摘はヘッダの位置に出すので、経路を添えて、取り込んだ側を辿れるようにする。
/// </para>
/// <para>
/// 同じ指摘を複数のシェーダーや Pass から報告したときは、1 件にまとめて経路を並べる。
/// </para>
/// </remarks>
public sealed record IncludeRoute(string FilePath, string? Origin, ImmutableArray<Location> Includes)
{
    /// <summary>同じ経路かを見分ける鍵。</summary>
    public string Key => string.Join(
        "|",
        [FilePath, Origin ?? string.Empty, .. Includes.Select(l => $"{l.FilePath}:{l.Span.Start.ToString(CultureInfo.InvariantCulture)}")]);

    /// <summary>
    /// 1 行で表す (<c>Lit.shader(23:13) Pass "Forward" → Common.hlsl(5:10)</c>)。
    /// </summary>
    /// <param name="formatPath">ファイルのパスを表示の形にする手続き。</param>
    /// <returns>表示する文字列。</returns>
    public string Describe(Func<string, string> formatPath)
    {
        ArgumentNullException.ThrowIfNull(formatPath);

        IEnumerable<string> steps = Includes.Select((include, index) =>
        {
            LinePosition start = include.LineSpan.Start;
            string step = $"{formatPath(include.FilePath)}({start.Line + 1}:{start.Character + 1})";
            return index == 0 && Origin is not null ? $"{step} {Origin}" : step;
        });

        return string.Join(" → ", steps);
    }
}
