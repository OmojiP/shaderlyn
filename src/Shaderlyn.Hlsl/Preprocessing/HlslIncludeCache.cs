using System.Collections.Concurrent;
using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// 1 つの <c>#include</c> を展開した結果。
/// </summary>
/// <remarks>
/// <b>展開は「出力」だけでなく「状態の変化」も生む。</b>
/// マクロ表・<c>#pragma</c>・診断・条件付きの範囲まで含めて覚えておかないと、
/// 使い回したときに続きの展開が変わってしまう。
/// </remarks>
/// <param name="Tokens">出力へ足したトークン。</param>
/// <param name="MacroChanges">
/// 変わったマクロ。値が <see langword="null"/> のものは <c>#undef</c> された名前。
/// </param>
/// <param name="ConditionallyDefined">条件の中で定義された名前。</param>
/// <param name="Pragmas">足された <c>#pragma</c>。</param>
/// <param name="Diagnostics">展開中に報告された診断。</param>
/// <param name="ConditionalRegions">
/// 条件付きで残した範囲。開始位置は、この展開が出したトークンの先頭からの相対値。
/// </param>
/// <param name="ConditionalIdentifiers">条件で参照された名前。</param>
/// <param name="SkippedIdentifiers">非活性領域に現れた名前。</param>
/// <param name="ExpandedRootMacros">
/// この展開の中で使われた、解析しているファイルが条件付きで定義したマクロの名前。
/// 取り込みの結果を使い回したときも、並べてよかったかを判断できるようにするために覚える。
/// </param>
/// <param name="DeclinedBothBranchSymbols">両方の分岐を残せなかったシンボル。</param>
/// <param name="BothBranchDeclines">両方の分岐を残せなかった理由。</param>
/// <param name="MergedSymbols">両方の分岐を並べた条件のシンボル。</param>
/// <param name="MergedRegions">両方の分岐を並べた領域。</param>
/// <param name="KeywordRegions">構成によって結果が変わった連なり。</param>
/// <param name="MacroAffectingSymbols">分岐でマクロ表か取り込みを変えたキーワード。</param>
/// <param name="MacroKeywordChanges">マクロと、それを分岐で定義・削除したキーワードの組。</param>
/// <param name="UnknownMacroSources">どのキーワードで変わるのか分からない連なりで定義・削除されたマクロ。</param>
/// <param name="CodeIdentifiers">コードに現れた識別子。使い回すときに、その時点のマクロの記録で数え直す。</param>
/// <param name="HeaderDefinitionChanges">変えた、ヘッダの定義がある条件。<see langword="null"/> は削除。順に当て直す。</param>
/// <param name="MergedMacros">並べた分岐で定義したマクロと、その条件のシンボル。</param>
/// <param name="MergedMacroConflicts">並べた分岐のマクロがコードで使われたシンボル。</param>
/// <param name="Includes">この展開の中で現れた <c>#include</c>。</param>
/// <param name="ResolvedIncludes">解決できた取り込み先のパス。</param>
/// <param name="UnresolvedIncludes">解決できなかった取り込みのパス。</param>
public sealed record IncludeExpansion(
    ImmutableArray<HlslSyntaxToken> Tokens,
    ImmutableArray<KeyValuePair<string, MacroDefinition?>> MacroChanges,
    ImmutableArray<string> ConditionallyDefined,
    ImmutableArray<PragmaDirective> Pragmas,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<ConditionalTokenRange> ConditionalRegions,
    ImmutableArray<HlslSyntaxToken> ConditionalIdentifiers,
    ImmutableArray<string> SkippedIdentifiers,
    ImmutableArray<string> ExpandedRootMacros,
    ImmutableArray<string> DeclinedBothBranchSymbols,
    ImmutableArray<BothBranchDecline> BothBranchDeclines,
    ImmutableArray<string> MergedSymbols,
    ImmutableArray<MergedRegion> MergedRegions,
    ImmutableArray<KeywordRegion> KeywordRegions,
    ImmutableArray<string> MacroAffectingSymbols,
    ImmutableArray<KeyValuePair<string, string>> MacroKeywordChanges,
    ImmutableArray<string> UnknownMacroSources,
    ImmutableArray<string> CodeIdentifiers,
    ImmutableArray<KeyValuePair<string, SymbolCondition?>> HeaderDefinitionChanges,
    ImmutableArray<KeyValuePair<string, ImmutableArray<string>>> MergedMacros,
    ImmutableArray<string> MergedMacroConflicts,
    ImmutableArray<IncludeReference> Includes,
    ImmutableArray<string> ResolvedIncludes,
    ImmutableArray<string> UnresolvedIncludes)
{
    /// <summary>この展開が読んだ名前と、読んだときの状態。これが同じなら結果も同じである。</summary>
    public IncludeReadSet Reads { get; init; } = IncludeReadSet.Empty;
}

/// <summary>
/// 1 つの取り込みの展開が、書く前に読んだ名前と、読んだときの状態。
/// </summary>
/// <param name="Macros">読んだマクロの名前。</param>
/// <param name="ConditionallyDefined">読んだ「条件の中で定義された名前」。</param>
/// <param name="HeaderDefinitions">読んだヘッダの定義の名前。</param>
/// <param name="Digest">読んだときの各名前の状態をまとめた値。</param>
public sealed record IncludeReadSet(
    ImmutableArray<string> Macros,
    ImmutableArray<string> ConditionallyDefined,
    ImmutableArray<string> HeaderDefinitions,
    ulong Digest)
{
    /// <summary>何も読んでいない。</summary>
    public static IncludeReadSet Empty { get; } = new([], [], [], 0);
}

/// <summary>
/// <c>#include</c> の展開結果を覚えておき、同じ状態なら使い回す。
/// </summary>
/// <remarks>
/// <para>
/// <b>同じヘッダを、同じマクロの状態で取り込んだなら、結果は同じである。</b>
/// Unity 同梱の 109 件では、コードブロックごとに取り込みの連鎖を展開し直しており、
/// 1 回の実行で 456 万行を展開していた。
/// その大半は同じヘッダを同じ状態で展開し直したものである。
/// </para>
/// <para>
/// <b>まずマクロの状態全体のハッシュで引く。</b>
/// 変更のたびに差分で更新するハッシュなので、引くのも比べるのも定数時間で済む。
/// </para>
/// <para>
/// <b>外れたら、同じファイルの展開を「その展開が読んだ名前の状態」で照合する。</b>
/// 全体の鍵は、そのヘッダに関係のないマクロが 1 つ違うだけで外れる。
/// バリアントごとの展開は <c>#define _KEYWORD 1</c> だけが違うので、ほとんどがこれで外れていた。
/// 読んだ名前で照合すると、Unity 同梱の 401 ファイルで展開は 61,336 回から 42,790 回に減り、
/// 展開の CPU 時間は半分になった。
/// 読んだ名前を漏れなく知るための仕組みは <see cref="TrackedTable{TValue}"/> にある。
/// </para>
/// <para>
/// <b>ただし、鍵が正しいだけでは足りない。</b>
/// 覚える中身が「そのヘッダが何を足したか」ではなく
/// 「そのヘッダより前に何を見たか」に依存していると、
/// 同じ鍵で中身の違う記録ができあがる。実際にそうなっていた
/// (<c>HlslPreprocessor.Record</c>)。
/// </para>
/// </remarks>
public sealed class HlslIncludeCache
{
    private readonly ConcurrentDictionary<IncludeCacheKey, IncludeExpansion> _entries = new();

    /// <summary>覚えている展開の数。</summary>
    public int Count => _entries.Count;

    private int _hits;
    private int _misses;

    /// <summary>使い回せた回数。</summary>
    /// <remarks>
    /// このキャッシュはファイルをまたいで共有され、複数のスレッドから同時に引かれる。
    /// 素の <c>++</c> では数え落とす。表示するだけの値だが、
    /// <b>速さを測るための値が測るたびに変わるのは困る。</b>
    /// </remarks>
    public int Hits => Volatile.Read(ref _hits);

    /// <summary>覚えていなかった回数。</summary>
    public int Misses => Volatile.Read(ref _misses);

    /// <summary>使い回せた回数のうち、読んだ名前の照合で当たった回数。</summary>
    public int ReadHits => Volatile.Read(ref _readHits);

    private int _readHits;

    /// <summary>展開結果を引く。</summary>
    /// <param name="key">鍵。</param>
    /// <param name="matches">
    /// 覚えた展開が読んだ名前の状態が、今の状態と同じかを判定する。
    /// 全体の鍵で当たらなかったときに、同じファイル・同じ設定の展開それぞれについて呼ぶ。
    /// </param>
    /// <param name="expansion">見つかった展開結果。</param>
    /// <returns>見つかった場合は <see langword="true"/>。</returns>
    public bool TryGet(IncludeCacheKey key, Func<IncludeReadSet, bool> matches, out IncludeExpansion? expansion)
    {
        ArgumentNullException.ThrowIfNull(matches);

        if (_entries.TryGetValue(key, out expansion))
        {
            Interlocked.Increment(ref _hits);
            return true;
        }

        if (_byFile.TryGetValue((key.FilePath, key.OptionsHash), out ImmutableArray<IncludeExpansion> candidates))
        {
            foreach (IncludeExpansion candidate in candidates)
            {
                if (matches(candidate.Reads))
                {
                    expansion = candidate;
                    Interlocked.Increment(ref _hits);
                    Interlocked.Increment(ref _readHits);
                    return true;
                }
            }
        }

        Interlocked.Increment(ref _misses);
        return false;
    }

    /// <summary>展開結果を覚える。</summary>
    /// <param name="key">鍵。</param>
    /// <param name="expansion">覚える展開結果。</param>
    public void Add(IncludeCacheKey key, IncludeExpansion expansion)
    {
        ArgumentNullException.ThrowIfNull(expansion);

        if (!_entries.TryAdd(key, expansion))
        {
            return;
        }

        _byFile.AddOrUpdate(
            (key.FilePath, key.OptionsHash),
            _ => [expansion],
            (_, existing) => existing.Length >= MaxExpansionsPerFile ? existing : existing.Add(expansion));
    }

    /// <summary>読んだ名前で照合する展開の、ファイルと設定ごとの上限。</summary>
    /// <remarks>
    /// 照合は 1 件ごとに読んだ名前の数だけ表を引く。取り込むたびに全件を照合すると、
    /// 当たらない取り込みの費用が件数に比例して膨らむ。
    /// </remarks>
    private const int MaxExpansionsPerFile = 16;

    private readonly ConcurrentDictionary<(string FilePath, int OptionsHash), ImmutableArray<IncludeExpansion>> _byFile = new();
}

/// <summary>
/// 展開結果を引くための鍵。
/// </summary>
/// <param name="FilePath">取り込んだファイルのパス。</param>
/// <param name="MacroTableHash">取り込む直前のマクロ表のハッシュ。</param>
/// <param name="OptionsHash">展開の設定 (両方の分岐を残すシンボルなど) のハッシュ。</param>
/// <remarks>
/// 取り込みの入れ子の状態は鍵に入れていない。
/// 覚えるのは条件の外で取り込まれた場合だけであり (<c>HlslPreprocessor</c>)、
/// そこでは同じパスが同時に 2 度現れることがないためである。
/// </remarks>
public readonly record struct IncludeCacheKey(string FilePath, ulong MacroTableHash, int OptionsHash);
