using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang のリテラルの書き方を検査する (SG0004)。
/// </summary>
/// <remarks>
/// <para>slangc 2026.19 の字句の誤りに当たる (slangc で確かめた)。</para>
/// <list type="bullet">
/// <item>知らないエスケープ (<c>\q</c>・<c>\e</c>・<c>\8</c>)、中身の無い <c>\x</c>・<c>\x{}</c>、閉じない <c>\u{12</c>、32 ビットに収まらない <c>\x</c> の値 (E10007)</item>
/// <item>16 進の桁の足りない <c>\u</c> (4 桁) と <c>\U</c> (8 桁) (E10008)</item>
/// <item>文字列の中の、0～255 に収まらない <c>\x</c> の値 (E10009) と、UTF-8 にできない符号位置 (0x1FFFFF を超える。E10013)</item>
/// <item>中身がちょうど 1 文字でない文字のリテラル (<c>''</c>・<c>'ab'</c>。E10001)</item>
/// <item>64 ビットの符号無しの整数に収まらない整数のリテラル (E10012)</item>
/// </list>
/// <para>使えるエスケープ: <c>\' \" \\ \? \a \b \f \n \r \t \v</c>、8 進 (1～3 桁)、<c>\x</c> (16 進、<c>\x{…}</c> も)、<c>\u</c> (4 桁か <c>\u{…}</c>)、<c>\U</c> (8 桁)。</para>
/// </remarks>
internal sealed class SlangLiteralAnalyzer : SemanticRuleAnalyzer
{
    private static readonly BigInteger MaxUInt64 = ulong.MaxValue;

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.InvalidLiteral, SlangRuleDescriptors.InvalidUtf8];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        HashSet<(string? File, int Start)> seen = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ShaderSyntaxToken? previous = null;

            CheckEncoding(context, program.Tree.Text);


            foreach (ShaderSyntaxToken token in program.Tree.Root.DescendantTokens())
            {
                // import "a\b.slang" のパスは、エスケープを読まない。
                bool path = previous is { Kind: ShaderSyntaxKind.IdentifierToken, Text: "import" or "__import" or "__include" or "implementing" or "module" };
                previous = token;

                if (path
                    || token.IsMissing
                    || token.Kind is not (ShaderSyntaxKind.StringLiteralToken or ShaderSyntaxKind.CharacterLiteralToken or ShaderSyntaxKind.NumericLiteralToken)
                    || !compilation.IsReportable(token)
                    || token.GetLocation() is not { } location
                    || !seen.Add((location.FilePath, location.Span.Start)))
                {
                    continue;
                }

                string? problem = token.Kind == ShaderSyntaxKind.NumericLiteralToken
                    ? CheckInteger(token)
                    : CheckQuoted(token.Text, character: token.Kind == ShaderSyntaxKind.CharacterLiteralToken);

                if (problem is not null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.InvalidLiteral, location, problem));
                }
            }
        }
    }

    /// <summary>
    /// UTF-8 として読めないバイト (E10006) を、行ごとに 1 つ報告する。コメントの中も見る。
    /// </summary>
    /// <remarks>位置は、ファイルを読んだときに SourceText が覚える (本物の U+FFFD は含まない)。</remarks>
    private static void CheckEncoding(SyntaxTreeAnalysisContext context, Core.Text.SourceText text)
    {
        int lastLine = -1;

        foreach (int i in text.InvalidUtf8Offsets)
        {
            Location location = Location.Create(text, new Core.Text.TextSpan(i, 1));

            if (location.LineSpan.Start.Line != lastLine)
            {
                lastLine = location.LineSpan.Start.Line;
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.InvalidUtf8, location, "UTF-8 として読めないバイトがあります。ファイルを UTF-8 で保存し直してください。"));
            }
        }
    }

    /// <summary>64 ビットの符号無しの整数に収まらない整数のリテラル。</summary>
    private static string? CheckInteger(ShaderSyntaxToken token)
    {
        string digits = ShaderLiteral.GetDigits(token);

        // 16 進の浮動小数のリテラル (0x1.8p3、0xffp0LF) は整数ではない。
        if (token.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && token.Text.IndexOfAny(['p', 'P', '.']) >= 0)
        {
            return null;
        }
        (int radix, string body) = ShaderLiteral.GetKind(token) switch
        {
            ShaderLiteralKind.Decimal => (10, digits),
            ShaderLiteralKind.Hexadecimal => (16, digits[2..]),
            ShaderLiteralKind.Binary => (2, digits[2..]),
            ShaderLiteralKind.Octal => (8, digits.TrimStart('0')),
            _ => (0, string.Empty),
        };

        if (radix == 0 || body.Length == 0)
        {
            return null;
        }

        BigInteger value = BigInteger.Zero;

        foreach (char c in body)
        {
            int digit = HexValue(c);

            if (digit < 0 || digit >= radix)
            {
                return null;
            }

            value = (value * radix) + digit;

            if (value > MaxUInt64)
            {
                return $"整数のリテラル {token.Text} は大きすぎて、64 ビットの整数にも収まりません。";
            }
        }

        return null;
    }

    /// <summary>文字と文字列のリテラルのエスケープと、文字のリテラルの長さ。</summary>
    private static string? CheckQuoted(string text, bool character)
    {
        if (text.Length < 2 || text[0] != text[^1] || text[0] is not ('"' or '\''))
        {
            return null;
        }

        string body = text[1..^1];
        int count = 0;

        for (int i = 0; i < body.Length; count++)
        {
            if (body[i] != '\\')
            {
                // サロゲートの組は 1 文字。
                i += char.IsHighSurrogate(body[i]) && i + 1 < body.Length && char.IsLowSurrogate(body[i + 1]) ? 2 : 1;
                continue;
            }

            int start = i;
            i++;

            if (i >= body.Length)
            {
                return "エスケープ '\\' の後ろに文字がありません。";
            }

            char kind = body[i++];

            switch (kind)
            {
                case '\'' or '"' or '\\' or '?' or 'a' or 'b' or 'f' or 'n' or 'r' or 't' or 'v':
                    continue;

                case >= '0' and <= '7':
                    for (int digits = 1; digits < 3 && i < body.Length && body[i] is >= '0' and <= '7'; digits++)
                    {
                        i++;
                    }

                    continue;

                case 'x' or 'u' when i < body.Length && body[i] == '{':
                {
                    int close = body.IndexOf('}', i + 1);
                    string hex = close < 0 ? string.Empty : body[(i + 1)..close];

                    if (close < 0 || hex.Length == 0 || !hex.All(char.IsAsciiHexDigit))
                    {
                        int end = close < 0 ? body.Length : close + 1;
                        return $"エスケープ '{Escape(body, start, Math.Min(end, i + 1 + hex.Length + (close < 0 ? 0 : 1)))}' が正しくありません。{{ }} の中に 16 進の数を書いてください。";
                    }

                    i = close + 1;

                    if (BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) is var braced && braced > uint.MaxValue)
                    {
                        return $"エスケープ '{body[start..i]}' の値が 32 ビットに収まりません。";
                    }

                    if (!character && CheckCodeUnit(kind, braced, body[start..i]) is { } braceProblem)
                    {
                        return braceProblem;
                    }

                    continue;
                }

                case 'x':
                {
                    int from = i;

                    while (i < body.Length && char.IsAsciiHexDigit(body[i]))
                    {
                        i++;
                    }

                    if (i == from)
                    {
                        return "エスケープ '\\x' の後ろに 16 進の数がありません。";
                    }

                    BigInteger value = BigInteger.Parse("0" + body[from..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                    if (value > uint.MaxValue)
                    {
                        return $"エスケープ '{body[start..i]}' の値が 32 ビットに収まりません。";
                    }

                    if (!character && CheckCodeUnit('x', value, body[start..i]) is { } unitProblem)
                    {
                        return unitProblem;
                    }

                    continue;
                }

                case 'u' or 'U':
                {
                    int need = kind == 'u' ? 4 : 8;
                    int from = i;

                    while (i < body.Length && i - from < need && char.IsAsciiHexDigit(body[i]))
                    {
                        i++;
                    }

                    if (i - from < need)
                    {
                        return $"エスケープ '\\{kind}' の後ろには、16 進の数をちょうど {need} 桁書いてください。";
                    }

                    if (!character && CheckCodeUnit(kind, BigInteger.Parse("0" + body[from..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture), body[start..i]) is { } pointProblem)
                    {
                        return pointProblem;
                    }

                    continue;
                }

                default:
                    return $"エスケープ '\\{kind}' は使えません。";
            }
        }

        return character && count != 1
            ? count == 0 ? "文字のリテラルの中身がありません。" : "文字のリテラルには、ちょうど 1 文字を書いてください (文字列なら \" \" で囲みます)。"
            : null;
    }

    /// <summary>文字列の中の値: \x は 1 バイト (0～255)、\u と \U は UTF-8 にできる符号位置 (0x1FFFFF まで)。</summary>
    private static string? CheckCodeUnit(char kind, BigInteger value, string escape)
        => kind == 'x'
            ? value > 255 ? $"文字列の中のエスケープ '{escape}' の値 {value} (0x{value:X}) は、1 バイト (0～255) に収まりません。" : null
            : value > 0x1FFFFF ? $"文字列の中のエスケープ '{escape}' の符号位置 {value} (0x{value:X}) は、UTF-8 にできません。" : null;

    private static string Escape(string body, int start, int end) => body[start..Math.Min(Math.Max(end, start + 1), body.Length)];

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
