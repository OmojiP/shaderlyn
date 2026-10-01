using System.Collections.Immutable;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// 条件領域が、両方の分岐をそのまま並べられる形かどうか。
/// </summary>
internal enum ConditionalRegionLayout
{
    /// <summary>両方の分岐がコードだけで、そのまま並べても構文として成立する。</summary>
    Mergeable,

    /// <summary>取り込みを切り替えている。展開結果そのものが変わる。</summary>
    SwitchesIncludes,

    /// <summary>
    /// 形は並べられるが、分岐の中でマクロを定義している。
    /// </summary>
    /// <remarks>
    /// 並べると <c>#define</c> がどの構成でも効くことになる。
    /// 条件でしか使われないマクロなら、その定義がある条件として読めるので並べてよい
    /// (<c>HlslPreprocessor.CanMergeDefinedMacros</c>)。
    /// </remarks>
    DefinesMacros,

    /// <summary>構文の単位で閉じていない。並べると構文として通らない。</summary>
    NotSelfContained,

    /// <summary>対応する <c>#endif</c> が見つからない。</summary>
    Unterminated,
}

/// <summary>
/// <c>#if</c> の領域を先読みして、両方の分岐を並べてよい形かを判定する。
/// </summary>
/// <remarks>
/// <para>
/// <b>両方の分岐を 1 本のトークン列に並べられるとは限らない。</b>
/// 文や宣言の単位で閉じていれば並べても通る。
/// </para>
/// <code>
/// // 並べると "return 1; return 2;" になり、文が 2 つとして通る
/// #ifdef A
///     return 1;
/// #else
///     return 2;
/// #endif
///
/// // 並べると "x = 1 x = 2 ;" になり、構文として通らない
/// #if A
///     x = 1
/// #else
///     x = 2
/// #endif
///     ;
/// </code>
/// <para>
/// <b>判定できないものは並べない側に倒す。</b>
/// 誤って並べると構文木が壊れ、その先のすべての判断が狂う。
/// 並べないほうへ倒しても、今までどおり構成ごとに展開し直すだけである。
/// </para>
/// </remarks>
internal static class ConditionalRegionScanner
{
    /// <summary>
    /// 条件領域の形を調べる。
    /// </summary>
    /// <param name="tokens">走査するトークン列。</param>
    /// <param name="start"><c>#if</c> の行の次のトークンの位置。</param>
    /// <returns>領域の形。</returns>
    public static ConditionalRegionLayout Scan(ImmutableArray<HlslSyntaxToken> tokens, int start)
        => Scan(tokens, start, out _);

    /// <summary>
    /// 条件領域の形を調べ、その領域が終わる位置も返す。
    /// </summary>
    /// <param name="tokens">走査するトークン列。</param>
    /// <param name="start"><c>#if</c> の行の次のトークンの位置。</param>
    /// <param name="end">対応する <c>#endif</c> の行の次のトークンの位置。</param>
    /// <returns>領域の形。</returns>
    /// <remarks>
    /// <para>
    /// <b>入れ子は同じ規則で見る。</b>
    /// 内側が並べてよい形なら、内側の 2 つの分岐を連ねたものも
    /// 括弧が釣り合い、文の切れ目で終わる。
    /// つまり外側から見れば、内側は 1 つのまとまったコードとして扱ってよい。
    /// </para>
    /// <para>
    /// 終わる位置を返すのは、呼び出し側が領域の中身をもう一度見るためである。
    /// 形が整っていても、中で呼んでいるマクロが構成ごとに変わるなら並べられない。
    /// </para>
    /// </remarks>
    public static ConditionalRegionLayout Scan(
        ImmutableArray<HlslSyntaxToken> tokens,
        int start,
        out int end)
    {
        int branchStart = start;
        bool definesMacros = false;
        end = tokens.Length;

        for (int i = start; i < tokens.Length; i++)
        {
            if (!IsDirectiveStart(tokens, i, out string? name))
            {
                continue;
            }

            switch (name)
            {
                case "if" or "ifdef" or "ifndef":
                {
                    ConditionalRegionLayout inner =
                        Scan(tokens, SkipDirectiveLine(tokens, i), out int innerEnd);

                    if (inner is not (ConditionalRegionLayout.Mergeable or ConditionalRegionLayout.DefinesMacros))
                    {
                        return inner;
                    }

                    definesMacros |= inner == ConditionalRegionLayout.DefinesMacros;

                    // 内側は見終わった。ここから先は外側の分岐の続きである。
                    i = innerEnd - 1;
                    break;
                }

                case "define" or "undef":
                    // 並べると、この定義がどの構成でも効くことになる。呼び出し側が並べてよいかを決める。
                    definesMacros = true;
                    i = SkipDirectiveLine(tokens, i) - 1;
                    break;

                case "include":
                    // その先の展開結果そのものが変わる。並べても意味を成さない。
                    return ConditionalRegionLayout.SwitchesIncludes;

                case "else" or "elif":
                    if (ClassifyBranch(tokens, branchStart, i) is { } branchShape)
                    {
                        return branchShape;
                    }

                    branchStart = SkipDirectiveLine(tokens, i);
                    break;

                case "endif":
                    end = SkipDirectiveLine(tokens, i);

                    return ClassifyBranch(tokens, branchStart, i)
                        ?? (definesMacros ? ConditionalRegionLayout.DefinesMacros : ConditionalRegionLayout.Mergeable);

                default:
                    break;
            }
        }

        return ConditionalRegionLayout.Unterminated;
    }

    /// <summary>
    /// 分岐 1 つ分が、そのまま並べられる形かを調べる。
    /// </summary>
    /// <param name="tokens">走査するトークン列。</param>
    /// <param name="start">分岐の開始位置。</param>
    /// <param name="end">分岐の終了位置 (この位置は含まない)。</param>
    /// <returns>並べられない理由。並べられる場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 括弧が釣り合っていて、文か宣言の切れ目で終わっていることを見る。
    /// 空の分岐は何も足さないので、そのまま並べてよい。
    /// </para>
    /// <para>
    /// <b>指令の行は数えない。</b>
    /// 指令そのものは出力に残らないので、並べたときの構文には現れない。
    /// 内側の条件は <see cref="Scan(ImmutableArray{HlslSyntaxToken}, int)"/> が別に見ており、
    /// 並べてよい形だと分かっているものだけがここまで来る。
    /// </para>
    /// </remarks>
    private static ConditionalRegionLayout? ClassifyBranch(
        ImmutableArray<HlslSyntaxToken> tokens,
        int start,
        int end)
    {
        int brace = 0;
        int paren = 0;
        int bracket = 0;
        HlslSyntaxToken? last = null;

        for (int i = start; i < end; i++)
        {
            if (IsDirectiveStart(tokens, i, out _))
            {
                i = SkipDirectiveLine(tokens, i) - 1;
                continue;
            }

            HlslSyntaxToken token = tokens[i];

            switch (token.Kind)
            {
                case HlslSyntaxKind.OpenBraceToken: brace++; break;
                case HlslSyntaxKind.CloseBraceToken: brace--; break;
                case HlslSyntaxKind.OpenParenToken: paren++; break;
                case HlslSyntaxKind.CloseParenToken: paren--; break;
                case HlslSyntaxKind.OpenBracketToken: bracket++; break;
                case HlslSyntaxKind.CloseBracketToken: bracket--; break;
                default: break;
            }

            last = token;
        }

        if (brace != 0 || paren != 0 || bracket != 0)
        {
            return ConditionalRegionLayout.NotSelfContained;
        }

        if (last is null)
        {
            // 空の分岐。並べても何も足さない。
            return null;
        }

        return last.Kind is HlslSyntaxKind.SemicolonToken or HlslSyntaxKind.CloseBraceToken
            ? null
            : ConditionalRegionLayout.NotSelfContained;
    }

    /// <summary>
    /// その位置が指令の始まりかを判定し、指令の名前を取り出す。
    /// </summary>
    /// <param name="tokens">走査するトークン列。</param>
    /// <param name="index">調べる位置。</param>
    /// <param name="name">取り出した指令の名前。</param>
    /// <returns>指令の始まりであれば <see langword="true"/>。</returns>
    private static bool IsDirectiveStart(
        ImmutableArray<HlslSyntaxToken> tokens,
        int index,
        out string? name)
    {
        name = null;

        if (tokens[index].Kind != HlslSyntaxKind.HashToken || !tokens[index].IsAtLineStart)
        {
            return false;
        }

        if (index + 1 >= tokens.Length
            || tokens[index + 1].Kind != HlslSyntaxKind.IdentifierToken)
        {
            return false;
        }

        name = tokens[index + 1].Text;
        return true;
    }

    /// <summary>指令の行を読み飛ばした次の位置を返す。</summary>
    /// <param name="tokens">走査するトークン列。</param>
    /// <param name="index">指令の <c>#</c> の位置。</param>
    /// <returns>次の行の先頭の位置。</returns>
    private static int SkipDirectiveLine(ImmutableArray<HlslSyntaxToken> tokens, int index)
    {
        for (int i = index + 1; i < tokens.Length; i++)
        {
            if (tokens[i].IsAtLineStart)
            {
                return i;
            }
        }

        return tokens.Length;
    }
}
