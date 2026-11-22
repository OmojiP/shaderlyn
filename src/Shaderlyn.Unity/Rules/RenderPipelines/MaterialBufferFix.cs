using System.Text.RegularExpressions;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Unity.Rules;

/// <summary>
/// マテリアルの定数バッファの外で宣言した uniform を、バッファの中へ移す修正を作る (URP0001)。
/// </summary>
/// <remarks>
/// <para>
/// <b>移しても意味が変わらないと言い切れる場合だけ作る。</b> 条件は次のとおり。
/// </para>
/// <list type="number">
/// <item>そのバッファ (<c>CBUFFER_START(名前)</c> … <c>CBUFFER_END</c>) がファイルに 1 つだけある。</item>
/// <item>バッファの外の宣言が、1 行だけの単純な宣言 (<c>half _Smoothness;</c>) としてファイルに 1 つだけある。</item>
/// <item>宣言とバッファが同じコードのブロック (<c>HLSLPROGRAM</c> など) にあるか、バッファが共通コード片 (<c>HLSLINCLUDE</c>・<c>CGINCLUDE</c>) にある。
/// 別の Pass のブロックへ移すと、元の Pass から宣言が消える。</item>
/// <item>宣言と <c>CBUFFER_END</c> の間に条件の指令 (<c>#if</c> など) が無い。条件が変わってしまう。</item>
/// <item>宣言を後ろへ移す場合は、その間でその名前を使っていない。使う位置より後ろに宣言が来てしまう。</item>
/// </list>
/// <para>
/// 宣言の行を消し、<c>CBUFFER_END</c> の行の直前に、バッファの中の行と同じ字下げで加える。
/// ファイルの文字だけを見るので、構文木を持たない ShaderLab のファイルの中でも作れる。
/// </para>
/// </remarks>
internal static partial class MaterialBufferFix
{
    /// <summary>修正を作る。</summary>
    /// <param name="text">.shader のファイルの文字。</param>
    /// <param name="name">uniform の名前 (プロパティの名前)。</param>
    /// <param name="bufferName">マテリアルの定数バッファの名前 (<c>UnityPerMaterial</c>)。</param>
    /// <returns>修正。条件を満たさなければ <see langword="null"/>。</returns>
    public static DiagnosticFix? Create(string text, string name, string bufferName)
    {
        Match[] starts = [.. BufferStart().Matches(text).Where(m => m.Groups["name"].Value == bufferName)];

        if (starts.Length != 1)
        {
            return null;
        }

        int bufferStart = starts[0].Index;
        Match end = BufferEnd().Match(text, starts[0].Index + starts[0].Length);

        if (!end.Success || BufferStart().Matches(text).Any(m => m.Index > bufferStart && m.Index < end.Index))
        {
            return null;
        }

        Regex declarationPattern = new($@"^[ \t]*(?:uniform[ \t]+)?[A-Za-z_][A-Za-z0-9_]*[ \t]+{Regex.Escape(name)}[ \t]*;[ \t]*(?://[^\r\n]*)?(?:\r\n|\n|$)", RegexOptions.Multiline);
        Match[] declarations = [.. declarationPattern.Matches(text).Where(m => m.Index < bufferStart || m.Index > end.Index)];

        if (declarations.Length != 1)
        {
            return null;
        }

        Match declaration = declarations[0];
        int insertAt = end.Index;

        if (!InSameBlockOrShared(text, declaration.Index, bufferStart))
        {
            return null;
        }

        // 宣言と CBUFFER_END の間 (どちらが先でも) に、条件の指令があれば作らない。
        int from = Math.Min(declaration.Index, insertAt);
        int to = Math.Max(declaration.Index + declaration.Length, insertAt);

        if (ConditionalDirective().IsMatch(text[from..to]))
        {
            return null;
        }

        // 宣言を後ろへ移すなら、その間でその名前を使っていないこと。
        if (declaration.Index < insertAt
            && Regex.IsMatch(text[(declaration.Index + declaration.Length)..insertAt], $@"\b{Regex.Escape(name)}\b"))
        {
            return null;
        }

        string newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string line = declaration.Value.Trim() + newLine;
        string indent = IndentOfLineBefore(text, insertAt);

        TextSpan removed = new(declaration.Index, declaration.Length);
        TextSpan inserted = new(insertAt, 0);

        return new DiagnosticFix(
            $"'{name}' の宣言を {bufferName} の中へ移す",
            [new TextReplacement(removed, string.Empty), new TextReplacement(inserted, indent + line)],
            IsPreferred: true);
    }

    /// <summary>2 つの位置が同じコードのブロックにあるか、<paramref name="buffer"/> が共通コード片にあるか。</summary>
    private static bool InSameBlockOrShared(string text, int declaration, int buffer)
    {
        Match? blockOfDeclaration = BlockStart().Matches(text).LastOrDefault(m => m.Index < declaration);
        Match? blockOfBuffer = BlockStart().Matches(text).LastOrDefault(m => m.Index < buffer);

        if (blockOfDeclaration is null || blockOfBuffer is null)
        {
            // ブロックの無いファイル (.hlsl をそのまま書いたものなど) は、ファイル全体が 1 つのブロック。
            return blockOfDeclaration is null && blockOfBuffer is null;
        }

        return blockOfDeclaration.Index == blockOfBuffer.Index || blockOfBuffer.Value.EndsWith("INCLUDE", StringComparison.Ordinal);
    }

    /// <summary><paramref name="position"/> の行の前の行の字下げ。前の行が無ければ空白 4 つ。</summary>
    private static string IndentOfLineBefore(string text, int position)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;

        if (lineStart <= 1)
        {
            return "    ";
        }

        int previousStart = text.LastIndexOf('\n', lineStart - 2) + 1;
        string previous = text[previousStart..(lineStart - 1)].TrimEnd('\r');
        return previous[..(previous.Length - previous.TrimStart(' ', '\t').Length)];
    }

    [GeneratedRegex(@"\bCBUFFER_START[ \t]*\([ \t]*(?<name>[A-Za-z_][A-Za-z0-9_]*)[ \t]*\)")]
    private static partial Regex BufferStart();

    [GeneratedRegex(@"^[ \t]*CBUFFER_END\b", RegexOptions.Multiline)]
    private static partial Regex BufferEnd();

    [GeneratedRegex(@"^[ \t]*#[ \t]*(?:if|ifdef|ifndef|elif|else|endif)\b", RegexOptions.Multiline)]
    private static partial Regex ConditionalDirective();

    [GeneratedRegex(@"\b(?:HLSLPROGRAM|HLSLINCLUDE|CGPROGRAM|CGINCLUDE)\b")]
    private static partial Regex BlockStart();
}
