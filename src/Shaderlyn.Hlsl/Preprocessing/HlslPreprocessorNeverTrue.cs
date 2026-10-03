using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// どの構成でも成り立たない <c>#if</c> / <c>#elif</c> の条件。
/// </summary>
/// <param name="Directive">指令名のトークン。</param>
/// <param name="Text">条件式の書かれたとおりの文字列。</param>
internal readonly record struct NeverTrueCondition(HlslSyntaxToken Directive, string Text);

/// <summary>
/// どの構成でも成り立たない条件を見つける (HL0332)。
/// </summary>
/// <remarks>
/// <para>
/// <b>Unity は有効なシンボルを値 1 のマクロとして定義する。</b>
/// <c>#if _A == 2</c> の分岐は、<c>_A</c> を有効にしても無効にしても通らない。
/// 同じ <c>#pragma</c> 行のシンボルを両方求める条件 (<c>defined(_X) &amp;&amp; defined(_Y)</c>) も、
/// どれか 1 つが必ず有効な行のすべてを否定する条件も、実在する構成では成り立たない。
/// </para>
/// <para>
/// <b>宣言されたシンボルと定数だけでできた条件に限る。</b>
/// <c>SHADER_API_D3D11</c> のような環境のマクロは 1 通りの値を仮に選んでいるので、
/// それを含む条件が偽でも、別のプラットフォームでは真になりうる。
/// 別のマクロを通して書かれた条件も、マクロの中身が構成や環境で変わりうるので見ない。
/// </para>
/// </remarks>
internal sealed partial class HlslPreprocessor
{
    /// <summary>試すシンボルの数の上限。組は 2 のこの数乗になる。</summary>
    private const int MaxNeverTrueSymbols = 10;

    /// <summary>どの構成でも成り立たない条件。</summary>
    private readonly List<NeverTrueCondition> _neverTrueConditions = [];

    /// <summary>
    /// 条件がどの構成でも成り立たないかを判定し、成り立たなければ覚える。
    /// </summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="directive">指令名のトークン。</param>
    /// <param name="depth">外側の条件の数。この指令自身の条件は含めない。</param>
    /// <returns>どの構成でも成り立たなければ <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 条件に現れる宣言されたシンボルについて、有効・無効のすべての組を、
    /// 宣言から分かる制約で成り立たないものを除いて試す。どの組でも偽なら成り立たない。
    /// </para>
    /// <para>
    /// 構成によらない条件のために読み飛ばしている分岐の中は見ない。
    /// その分岐はどの構成でも読まれないので、中の条件を報告しても直す意味が無い。
    /// </para>
    /// </remarks>
    private bool NoteIfNeverTrue(ImmutableArray<HlslSyntaxToken> line, HlslSyntaxToken directive, int depth)
    {
        if (line.IsEmpty
            || _options.DeclaredSymbols.IsEmpty
            || directive.IsFromMacroExpansion
            || !IsInRootSource(directive)
            || IsInStaticSkippedBranch(depth)
            || ReadSymbolsOnly(line) is not { } symbols)
        {
            return false;
        }

        for (int mask = 0; mask < 1 << symbols.Length; mask++)
        {
            SymbolCondition configuration = SymbolCondition.Always;

            for (int i = 0; i < symbols.Length; i++)
            {
                configuration = configuration.And(SymbolCondition.Symbol(symbols[i], (mask & (1 << i)) != 0));
            }

            if (!_options.SymbolConstraints.IsPossible(configuration))
            {
                continue;
            }

            bool value = ConditionalExpressionEvaluator.Evaluate(
                SubstituteSymbols(line, symbols, mask),
                out string? error);

            // 評価できない式は判断しない。誤りは行全体の評価が報告している。
            if (error is not null || value)
            {
                return false;
            }
        }

        TextSpan written = TextSpan.FromBounds(line[0].Span.Start, line[^1].Span.End);
        _neverTrueConditions.Add(new NeverTrueCondition(directive, line[0].Source.ToString(written)));

        return true;
    }

    /// <summary>
    /// 条件式が、宣言されたシンボルと定数だけでできているかを調べる。
    /// </summary>
    /// <param name="line">条件式の行。</param>
    /// <returns>現れるシンボル (名前順)。ほかの名前を含むか、シンボルが無いか多すぎれば <see langword="null"/>。</returns>
    private ImmutableArray<string>? ReadSymbolsOnly(ImmutableArray<HlslSyntaxToken> line)
    {
        SortedSet<string> symbols = new(StringComparer.Ordinal);

        foreach (HlslSyntaxToken token in line)
        {
            if (token.Kind != HlslSyntaxKind.IdentifierToken || token.TextIs("defined"))
            {
                continue;
            }

            if (!_options.DeclaredSymbols.Contains(token.Text))
            {
                return null;
            }

            symbols.Add(token.Text);
        }

        return symbols.Count is > 0 and <= MaxNeverTrueSymbols ? [.. symbols] : null;
    }

    /// <summary>
    /// 条件式のシンボルを、その組での値に置き換える。
    /// </summary>
    /// <param name="line">条件式の行。</param>
    /// <param name="symbols">現れるシンボル。</param>
    /// <param name="mask">有効にするシンボルの印。<paramref name="symbols"/> の順に 1 ビットずつ。</param>
    /// <returns>置き換えた行。</returns>
    /// <remarks>
    /// <c>defined(X)</c> / <c>defined X</c> は定義されているかで 1 か 0 に、<c>X</c> だけなら値 (有効なら 1、無効なら 0) にする。
    /// どちらも同じ値になる。Unity は有効なシンボルを値 1 で定義し、未定義の名前は 0 として解かれる。
    /// </remarks>
    private static ImmutableArray<HlslSyntaxToken> SubstituteSymbols(
        ImmutableArray<HlslSyntaxToken> line,
        ImmutableArray<string> symbols,
        int mask)
    {
        ImmutableArray<HlslSyntaxToken>.Builder result = ImmutableArray.CreateBuilder<HlslSyntaxToken>(line.Length);

        for (int i = 0; i < line.Length; i++)
        {
            HlslSyntaxToken token = line[i];

            if (token.TextIs("defined"))
            {
                bool parenthesized = i + 3 < line.Length
                                     && line[i + 1].Kind == HlslSyntaxKind.OpenParenToken
                                     && line[i + 3].Kind == HlslSyntaxKind.CloseParenToken;
                int operand = parenthesized ? i + 2 : i + 1;

                if (operand < line.Length && line[operand].Kind == HlslSyntaxKind.IdentifierToken)
                {
                    result.Add(CreateNumericToken(token, ValueOf(line[operand].Text)));
                    i = parenthesized ? i + 3 : i + 1;
                    continue;
                }
            }

            result.Add(token.Kind == HlslSyntaxKind.IdentifierToken
                ? CreateNumericToken(token, ValueOf(token.Text))
                : token);
        }

        return result.ToImmutable();

        int ValueOf(string name)
        {
            int index = symbols.IndexOf(name);
            return index >= 0 && (mask & (1 << index)) != 0 ? 1 : 0;
        }
    }
}
