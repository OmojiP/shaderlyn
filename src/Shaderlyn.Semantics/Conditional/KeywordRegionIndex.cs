using System.Collections.Immutable;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Semantics.Conditional;

/// <summary>
/// 構成によって結果が変わる <c>#if</c> の連なりから、ノードがどのキーワードで現れたり消えたりするかを答える。
/// </summary>
/// <remarks>
/// <para>
/// <b>互いに関係しないキーワードをまとめて有効にした構成のためにある。</b>
/// その木と既定の木との違いのそれぞれに、どれか 1 つのキーワードの条件を付ける。
/// </para>
/// <para>
/// <b>違いのノードと重なる連なりが、まとめたキーワードのうち 1 つだけを見ていれば、そのキーワードの違いである。</b>
/// まとめるのは、分岐の中でマクロを定義・削除せずヘッダも取り込まないキーワードだけなので
/// (<see cref="PreprocessResult.MacroAffectingSymbols"/>)、有効にして変わるのは、そのキーワードを見ている連なりの中だけである。
/// 連なりはノードを囲んでいても (<c>#ifdef _A</c> の中の宣言)、ノードの中にあっても (文の途中で分かれる形) よい。
/// </para>
/// <para>
/// <b>言えないときは言わない。</b>
/// 重なる連なりが無い、2 つ以上のキーワードを見ている、
/// どのキーワードで変わるのか分からないマクロを見ている、のどれかなら <see langword="null"/> を返す。
/// 呼び出し側はその構成を使わず、キーワードを 1 つずつ展開し直す。
/// </para>
/// </remarks>
internal sealed class KeywordRegionIndex
{
    private readonly Dictionary<string, List<KeywordRegion>> _byFile = new(StringComparer.Ordinal);

    /// <summary>索引を作る。</summary>
    /// <param name="regions">展開が記録した連なり。</param>
    public KeywordRegionIndex(ImmutableArray<KeywordRegion> regions)
    {
        if (regions.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (KeywordRegion region in regions)
        {
            if (!_byFile.TryGetValue(region.FilePath, out List<KeywordRegion>? list))
            {
                list = [];
                _byFile[region.FilePath] = list;
            }

            list.Add(region);
        }
    }

    /// <summary>ノードと重なる連なりから、そのノードを変えたキーワードを求める。</summary>
    /// <param name="node">対象のノード。</param>
    /// <param name="memberOf">
    /// 連なりが見ているキーワードを、まとめたキーワードのどれに数えるかを答える処理。数えなければ <see langword="null"/>。
    /// 有効にしたキーワードと同時には有効にならないキーワードも、そのキーワードに数える
    /// (<c>_ADDITIONAL_LIGHTS</c> を有効にすると、同じ行の <c>_ADDITIONAL_LIGHTS_VERTEX</c> の分岐は並べずに外れる)。
    /// </param>
    /// <returns>1 つに決まればそのキーワード。決まらなければ <see langword="null"/>。</returns>
    public string? Attribute(HlslSyntaxNode node, Func<string, string?> memberOf)
    {
        if (node.Source is not { } source || !_byFile.TryGetValue(source.FilePath, out List<KeywordRegion>? regions))
        {
            return null;
        }

        string? found = null;

        foreach (KeywordRegion region in regions)
        {
            if (!Touches(region, node, source))
            {
                continue;
            }

            if (region.DependsOnMacros)
            {
                return null;
            }

            foreach (string symbol in region.Symbols)
            {
                if (memberOf(symbol) is not { } member)
                {
                    continue;
                }

                if (found is not null && !string.Equals(found, member, StringComparison.Ordinal))
                {
                    return null;
                }

                found = member;
            }
        }

        return found;
    }

    /// <summary>連なりがノードと重なるか、空白だけを挟んで接しているかを判定する。</summary>
    /// <param name="region">連なり。</param>
    /// <param name="node">ノード。</param>
    /// <param name="source">ノードのファイル。</param>
    /// <returns>重なるか接していれば <see langword="true"/>。</returns>
    /// <remarks>
    /// 接している連なりも、ノードの境目を変えうる。HDRP の <c>#if !defined(_DEPTHOFFSET_ON)</c> は
    /// 関数の前の <c>[earlydepthstencil]</c> だけを囲む。有効にすると関数は <c>void</c> から始まり、連なりとは重ならない。
    /// </remarks>
    private static bool Touches(KeywordRegion region, HlslSyntaxNode node, SourceText source)
    {
        if (region.End > node.Span.Start && node.Span.End > region.Start)
        {
            return true;
        }

        return region.End <= node.Span.Start
            ? IsBlank(source, region.End, node.Span.Start)
            : IsBlank(source, node.Span.End, region.Start);
    }

    /// <summary>その範囲が空白だけかを判定する。</summary>
    /// <param name="source">対象のファイル。</param>
    /// <param name="start">範囲の始まり。</param>
    /// <param name="end">範囲の終わり (含まない)。</param>
    /// <returns>空白だけなら <see langword="true"/>。</returns>
    private static bool IsBlank(SourceText source, int start, int end)
        => start <= end
           && end <= source.Content.Length
           && source.AsSpan(TextSpan.FromBounds(start, end)).IsWhiteSpace();
}
