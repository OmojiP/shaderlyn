using System.Text.Json;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cli.Inspection;

/// <summary>
/// シンボルごとに、両方の分岐を 1 本の木に並べたか、構成ごとに展開したかを書き出す。
/// </summary>
/// <remarks>
/// <para>
/// <b>構成ごとの展開 (バリアント) が増えた理由を、利用者が自分のシェーダーで確かめられるようにする。</b>
/// 展開は解析を遅くし、上限 (<c>--max-symbol-variants</c>) に届けば <c>SL0003</c> になる。
/// 書き方を変えれば並べられることが多いが、どの <c>#if</c> が原因なのかが見えなければ直しようがない。
/// </para>
/// <para>
/// 分類は <c>docs/guide/conditional-compilation.md</c> の段階の説明と同じ言葉を使う。
/// </para>
/// </remarks>
internal static partial class AnalysisInspector
{
    /// <summary>並べなかった理由の、画面に出す説明。</summary>
    private static readonly Dictionary<BothBranchDeclineReason, string> DeclineReasonLabels = new()
    {
        [BothBranchDeclineReason.RegionNotSelfContained] = "分岐の中身が文・宣言の単位で閉じていない",
        [BothBranchDeclineReason.RegionDefinesMacros] = "分岐の中で、コードで使うマクロを定義している (定義ごとの複製で補えなかった)",
        [BothBranchDeclineReason.RegionSwitchesIncludes] = "分岐で取り込むファイルを切り替えている",
        [BothBranchDeclineReason.RegionUnterminated] = "#endif が無い",
        [BothBranchDeclineReason.CallsConfigurationDependentMacro] = "分岐の中で、構成によって定義が変わるマクロを使っている",
        [BothBranchDeclineReason.UnreadableCondition] = "条件式をシンボルの条件として読めない (値として解いた)",
        [BothBranchDeclineReason.OuterNotMerged] = "外側の #if を並べていない",
        [BothBranchDeclineReason.FollowsUnmergedBranch] = "並べなかった #if に続く #elif",
        [BothBranchDeclineReason.NotInFile] = "指令がマクロの展開から出てきた",
        [BothBranchDeclineReason.OutsideDefaultMergedRegions] = "既定の構成が並べなかった領域",
    };

    /// <summary>
    /// コードブロック 1 つの、シンボルごとの扱いを書き出す。
    /// </summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="program">対象のコードブロック (既定の構成)。</param>
    /// <remarks>
    /// 扱いは次の順に決める。前のものに当たれば、後ろは見ない。
    /// <list type="number">
    /// <item>構成ごとに展開した (方法 B)</item>
    /// <item>展開が要ったが、上限で展開しなかった (<c>SL0003</c>)</item>
    /// <item>並べなかったが、既定の構成で有効なシンボルである (<c>_</c> の無い行の先頭)</item>
    /// <item>並べなかったが、構成は作っていない</item>
    /// <item>条件に現れ、両方の分岐を並べた (方法 A)</item>
    /// <item>条件に現れない</item>
    /// </list>
    /// </remarks>
    private static void WriteSymbolStates(Utf8JsonWriter writer, ShaderCompilation compilation, AnalyzedProgram program)
    {
        PreprocessResult result = program.Tree.PreprocessResult;

        AnalyzedProgram[] variants =
        [
            .. compilation.SymbolVariants.Where(v =>
                v.CodeSpan == program.CodeSpan
                && string.Equals(v.KernelName, program.KernelName, StringComparison.Ordinal)),
        ];

        HashSet<string> conditional = new(result.ConditionalIdentifiers.Select(t => t.Text), StringComparer.Ordinal);
        HashSet<string> declined = new(result.DeclinedBothBranchSymbols, StringComparer.Ordinal);
        HashSet<string> conflicts = new(program.MacroConflictSymbols.IsDefault ? [] : program.MacroConflictSymbols, StringComparer.Ordinal);
        HashSet<string> defaults = new(
            ShaderSymbols.CollectConstraints(result.Pragmas).RequiredGroups.Select(g => g[0]),
            StringComparer.Ordinal);

        SortedSet<string> symbols = new(ShaderSymbols.CollectDeclared(result.Pragmas), StringComparer.Ordinal);
        symbols.UnionWith(declined);
        symbols.UnionWith(conflicts);
        symbols.UnionWith(variants.SelectMany(v => v.EnabledSymbols));

        writer.WriteStartArray("symbols");

        foreach (string symbol in symbols)
        {
            string[] expanded = [.. variants.Where(v => v.EnabledSymbols.Contains(symbol)).Select(DescribeConfiguration)];

            (string state, string label) = expanded.Length > 0
                ? ("variant", "方法 B: 構成ごとに展開した")
                : compilation.UnexploredSymbols.Contains(symbol)
                    ? ("unexplored", "方法 B が要るが、上限で展開していない (SL0003)")
                    : (declined.Contains(symbol) || conflicts.Contains(symbol)) && defaults.Contains(symbol)
                        ? ("default", "既定の構成で有効。無効な側は同じ行の別のシンボルの構成で読む")
                        : declined.Contains(symbol) || conflicts.Contains(symbol)
                            ? ("declined", "並べられなかったが、構成は作っていない")
                            : conditional.Contains(symbol)
                                ? ("merged", "方法 A: 両方の分岐を 1 本の木に並べた")
                                : ("unused", "条件に現れない");

            writer.WriteStartObject();
            writer.WriteString("name", symbol);
            writer.WriteString("state", state);
            writer.WriteString("label", label);
            WriteStrings(writer, "configurations", expanded);

            writer.WriteStartArray("reasons");

            foreach (BothBranchDecline decline in result.BothBranchDeclines.IsDefault ? [] : result.BothBranchDeclines)
            {
                if (string.Equals(decline.Symbol, symbol, StringComparison.Ordinal))
                {
                    WriteReason(writer, compilation, DeclineReasonLabels.GetValueOrDefault(decline.Reason, decline.Reason.ToString()), decline.FilePath, decline.DirectiveSpan);
                }
            }

            if (conflicts.Contains(symbol))
            {
                WriteReason(writer, compilation, "並べた分岐で定義したマクロを、コードで使っている (取り込むファイルの切り替えなど)", null, null);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        WriteStrings(writer, "configurations", variants.Select(DescribeConfiguration));
    }

    /// <summary>上限で展開しなかった組を書き出す。</summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <remarks>
    /// 組はシェーダー全体で数えるので、ブロックごとではなく 1 か所にまとめる。
    /// 位置は、その組でしか通らない分岐を始めた指令である。
    /// </remarks>
    private static void WriteUnexploredCombinations(Utf8JsonWriter writer, ShaderCompilation compilation)
    {
        writer.WriteStartArray("unexploredCombinations");

        foreach (SymbolCombination combination in compilation.UnexploredSymbolCombinations)
        {
            WriteReason(
                writer,
                compilation,
                string.Join("+", combination.Symbols),
                combination.Location.FilePath,
                combination.Location.Span);
        }

        writer.WriteEndArray();
    }

    /// <summary>並べなかった理由 1 件を書き出す。</summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="label">理由の説明。</param>
    /// <param name="filePath">その指令が書かれたファイル。分からなければ <see langword="null"/>。</param>
    /// <param name="span">その指令の位置。分からなければ <see langword="null"/>。</param>
    /// <remarks>
    /// このファイルに書かれた指令なら行を出し、選ぶとソースを示せるようにする。
    /// ヘッダの指令は、このファイルの行では表せないのでパスだけを出す。
    /// </remarks>
    private static void WriteReason(
        Utf8JsonWriter writer,
        ShaderCompilation compilation,
        string label,
        string? filePath,
        TextSpan? span)
    {
        bool here = filePath is not null
                    && string.Equals(filePath, compilation.Text.FilePath, StringComparison.Ordinal);

        writer.WriteStartObject();
        writer.WriteString("label", label);

        if (here && span is { } at)
        {
            writer.WriteString("where", compilation.Text.GetLinePositionSpan(at).Start.ToString());
            writer.WriteNumber("start", at.Start);
            writer.WriteNumber("length", at.Length);
        }
        else
        {
            writer.WriteString("where", filePath ?? string.Empty);
            writer.WriteNumber("start", 0);
            writer.WriteNumber("length", 0);
        }

        writer.WriteEndObject();
    }

    /// <summary>構成ごとの展開 1 件を、有効にしたシンボルで表す。</summary>
    /// <param name="variant">対象のバリアント。</param>
    /// <returns>
    /// 1 つなら <c>_A</c>、論理積の組なら <c>_A+_B</c>、
    /// 互いに関係しないシンボルを 1 回にまとめた展開なら <c>{_A,_B}</c>。
    /// </returns>
    private static string DescribeConfiguration(AnalyzedProgram variant)
        => variant.IsPacked
            ? "{" + string.Join(",", variant.EnabledSymbols) + "}"
            : string.Join("+", variant.EnabledSymbols);

    /// <summary>文字列の並びを書き出す。</summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="name">プロパティ名。</param>
    /// <param name="values">書き出す値。</param>
    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);

        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
