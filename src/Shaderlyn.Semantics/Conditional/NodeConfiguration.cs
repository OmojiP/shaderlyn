using System.Collections.Immutable;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Semantics.Conditional;

/// <summary>
/// ノードの出現条件に現れるシンボルを 1 通りに決めた構成と、そこに存在するノード。
/// </summary>
/// <remarks>
/// <para>
/// <b>数えたり合計したりするルールのためにある。</b>
/// 両方の分岐を並べた木には、同時には存在しないノードが並んでいる。
/// <c>#ifdef _A</c> と <c>#else</c> にサンプリングを 1 回ずつ書けば、木には 2 回あるが、どの構成でも 1 回である。
/// 木のノードをそのまま数えると、実在しない構成の数を報告することになる。
/// </para>
/// <para>
/// <see cref="ShaderCompilation.TryEnumerateConfigurations"/> で得る。
/// 構成ごとに <see cref="Contains"/> で絞って数え、その最大を上限と比べる。
/// </para>
/// </remarks>
public sealed class NodeConfiguration
{
    private readonly HashSet<HlslSyntaxNode> _present;

    /// <summary>構成を作る。</summary>
    /// <param name="enabledSymbols">有効にしたシンボル。</param>
    /// <param name="present">この構成で存在するノード。</param>
    internal NodeConfiguration(ImmutableHashSet<string> enabledSymbols, HashSet<HlslSyntaxNode> present)
    {
        EnabledSymbols = enabledSymbols;
        _present = present;
    }

    /// <summary>
    /// この構成で有効にしたシンボル。出現条件に現れたシンボルのうち、有効な側を並べる。
    /// </summary>
    public ImmutableHashSet<string> EnabledSymbols { get; }

    /// <summary>そのノードが、この構成で存在するかを判定する。</summary>
    /// <param name="node">対象のノード。構成を求めるときに渡したものに限る。</param>
    /// <returns>存在すれば <see langword="true"/>。</returns>
    public bool Contains(HlslSyntaxNode node) => _present.Contains(node);
}
