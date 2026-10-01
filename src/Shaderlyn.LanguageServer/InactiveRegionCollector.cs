using System.Collections.Immutable;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 効いていない行の範囲。
/// </summary>
/// <param name="StartLine">最初の行 (0 始まり)。</param>
/// <param name="EndLine">範囲の次の行 (0 始まり、この行は含まない)。</param>
internal readonly record struct InactiveLineRange(int StartLine, int EndLine);

/// <summary>
/// 条件が外れて、どの構成でも効いていない範囲を集める。
/// </summary>
/// <remarks>
/// <para>
/// <b>1 つの構成で外れただけでは「効いていない」と言わない。</b>
/// <c>#pragma multi_compile</c> のシンボルで守られた分岐は、既定の構成では外れていても
/// 別のバリアントでは通る。薄く表示すると「直しても意味が無いコード」に見え、
/// 実際に出荷されるコードが放置される。
/// だから既定の構成とシンボルのバリアントのすべてで外れていた範囲だけを出す。
/// </para>
/// <para>
/// <b>判断の前提が欠けている構成の結果は使わない。</b>
/// ヘッダを取り込めていなければ、そこで定義されるはずのマクロが見えず、
/// 通る分岐まで外れて見える。展開しなかったシンボルで守られた分岐も、別の構成では通る。
/// 分からないものは、効いていると見なす側へ倒す。
/// </para>
/// <para>
/// <b>プラットフォームやバージョンで決まる条件も、効いていると見なす。</b>
/// 解析は D3D11・Unity 6 の構成を仮に選んで展開している。
/// <c>#if SHADER_API_MOBILE</c> の中はこの構成では外れるが、モバイル向けのビルドでは通る。
/// URP の TerrainLit.shader がこの形の分岐を持っている。
/// </para>
/// </remarks>
internal static class InactiveRegionCollector
{
    /// <summary>
    /// ビルドの対象や段階によって値が変わる、Unity が定義するマクロの名前の先頭。
    /// </summary>
    /// <remarks>
    /// 解析が仮に選んだ値 (<see cref="SemanticsOptions.DefaultPredefinedMacros"/>) に加えて、
    /// 解析では定義しないが実際のビルドでは定義される側の名前も含める。
    /// </remarks>
    private static readonly string[] EnvironmentMacroPrefixes =
        ["SHADER_API_", "SHADER_STAGE_", "SHADER_TARGET", "UNITY_PLATFORM_", "UNITY_COMPILER_", "UNITY_VERSION"];

    /// <summary>すべての構成で効いていない範囲を集める。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <returns>行の順に並べ、重なりをまとめた範囲。</returns>
    public static ImmutableArray<InactiveLineRange> Collect(ShaderCompilation compilation)
        => Collect(compilation, trustSymbolConfiguration: false);

    /// <summary>
    /// 利用者が選んだ 1 つの構成で効いていない範囲を集める。
    /// </summary>
    /// <param name="compilation">
    /// 選んだシンボルだけを有効にし、バリアントも両方の分岐の併記も使わずに組んだセマンティックモデル
    /// (<see cref="AnalysisSession.CreateConfigurationModel"/>)。
    /// </param>
    /// <returns>行の順に並べ、重なりをまとめた範囲。</returns>
    /// <remarks>
    /// <para>
    /// <b>シンボルで守られた分岐も薄くする。</b>
    /// 利用者が構成を選んだのだから、選ばなかったシンボルは無効である。
    /// バリアントを展開しないモデルでは条件が見ているシンボルがすべて「展開しなかった」側に入るが、
    /// ここではそれを「判断できない」とは扱わない。
    /// </para>
    /// <para>
    /// プラットフォームで決まる条件と、ヘッダを取り込めなかった構成は、今までどおり効いていると見なす。
    /// シンボルを選んでも、それらの値は分からないままである。
    /// </para>
    /// </remarks>
    public static ImmutableArray<InactiveLineRange> CollectSelectedConfiguration(ShaderCompilation compilation)
        => Collect(compilation, trustSymbolConfiguration: true);

    /// <summary>範囲を集める。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="trustSymbolConfiguration">展開しなかったシンボルの分岐も判断に使うかどうか。</param>
    /// <returns>行の順に並べ、重なりをまとめた範囲。</returns>
    private static ImmutableArray<InactiveLineRange> Collect(ShaderCompilation compilation, bool trustSymbolConfiguration)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        string filePath = compilation.Text.FilePath;

        // 組として同時に有効にする構成を作らなかったシンボルも、判断できない側に入れる。
        // 組でしか通らない分岐は、1 つずつの構成ではどれも無効に見える。
        HashSet<string> unexplored = trustSymbolConfiguration
            ? []
            : new(
                compilation.UnexploredSymbols.Concat(compilation.UnexploredSymbolCombinations.SelectMany(c => c.Symbols)),
                StringComparer.Ordinal);
        ImmutableArray<AnalyzedProgram> programs = [.. compilation.Programs, .. compilation.SymbolVariants];

        // HLSLINCLUDE は Pass ごとに取り込まれるので、同じ範囲が何度も出てくる。
        HashSet<TextSpan> candidates = [];

        foreach (AnalyzedProgram program in programs)
        {
            PreprocessResult result = program.Tree.PreprocessResult;

            if (result.InactiveRegions.IsDefaultOrEmpty || !result.UnresolvedIncludes.IsEmpty)
            {
                continue;
            }

            foreach (InactiveRegion region in result.InactiveRegions)
            {
                if (string.Equals(region.Location.FilePath, filePath, StringComparison.Ordinal)
                    && !region.ConditionSymbols.Any(unexplored.Contains)
                    && !region.ConditionSymbols.Any(IsEnvironmentMacro))
                {
                    candidates.Add(region.Location.Span);
                }
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        List<int> active = CollectActiveOffsets(programs, filePath);
        List<InactiveLineRange> lines = [];

        foreach (TextSpan span in candidates)
        {
            if (ContainsAny(active, span))
            {
                continue;
            }

            // 範囲は指令の名前の終わりから始まる。指令の行そのものは効いているので次の行から数える。
            int start = compilation.Text.GetLinePosition(span.Start).Line + 1;
            int end = compilation.Text.GetLinePosition(span.End).Line;

            if (end > start)
            {
                lines.Add(new InactiveLineRange(start, end));
            }
        }

        return Merge(lines);
    }

    /// <summary>ビルドの対象や段階によって値が変わるマクロかどうかを判定する。</summary>
    /// <param name="name">条件に書かれた名前。</param>
    /// <returns>変わるものであれば <see langword="true"/>。</returns>
    private static bool IsEnvironmentMacro(string name)
        => SemanticsOptions.DefaultPredefinedMacros.ContainsKey(name)
           || EnvironmentMacroPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// いずれかの構成で展開の結果に残った、このファイルのトークンの位置を集める。
    /// </summary>
    /// <param name="programs">既定の構成とシンボルのバリアント。</param>
    /// <param name="filePath">このファイルのパス。</param>
    /// <returns>並べ替えた開始位置。</returns>
    /// <remarks>
    /// マクロ展開で生まれたトークンも数える。位置は呼び出し側に重なっており、
    /// 呼び出しが書かれた場所は効いているコードである。
    /// </remarks>
    private static List<int> CollectActiveOffsets(ImmutableArray<AnalyzedProgram> programs, string filePath)
    {
        List<int> offsets = [];

        foreach (AnalyzedProgram program in programs)
        {
            foreach (HlslSyntaxToken token in program.Tree.PreprocessResult.Tokens)
            {
                if (token.Span.Length > 0
                    && string.Equals(token.Source.FilePath, filePath, StringComparison.Ordinal))
                {
                    offsets.Add(token.Span.Start);
                }
            }
        }

        offsets.Sort();
        return offsets;
    }

    /// <summary>範囲の中に始まる位置が 1 つでもあるかを判定する。</summary>
    /// <param name="sorted">並べ替えた位置。</param>
    /// <param name="span">調べる範囲。</param>
    /// <returns>あれば <see langword="true"/>。</returns>
    private static bool ContainsAny(List<int> sorted, TextSpan span)
    {
        int index = sorted.BinarySearch(span.Start);

        if (index < 0)
        {
            index = ~index;
        }

        return index < sorted.Count && sorted[index] < span.End;
    }

    /// <summary>重なる範囲と接する範囲をまとめる。</summary>
    /// <param name="ranges">まとめる範囲。</param>
    /// <returns>行の順に並べた範囲。</returns>
    private static ImmutableArray<InactiveLineRange> Merge(List<InactiveLineRange> ranges)
    {
        ranges.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));

        List<InactiveLineRange> merged = [];

        foreach (InactiveLineRange range in ranges)
        {
            if (merged.Count > 0 && range.StartLine <= merged[^1].EndLine)
            {
                merged[^1] = merged[^1] with { EndLine = Math.Max(merged[^1].EndLine, range.EndLine) };
            }
            else
            {
                merged.Add(range);
            }
        }

        return [.. merged];
    }
}
