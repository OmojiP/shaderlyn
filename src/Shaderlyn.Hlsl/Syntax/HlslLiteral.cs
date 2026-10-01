using System.Globalization;

namespace Shaderlyn.Hlsl.Syntax;

/// <summary>
/// 数値リテラルの書き方。
/// </summary>
/// <remarks>
/// 桁の意味が書き方で変わる。
/// <c>0x1e</c> の <c>e</c> は桁、<c>1e5</c> の <c>e</c> は指数である。
/// </remarks>
public enum HlslLiteralKind
{
    /// <summary>数値リテラルとして読めない。</summary>
    None,

    /// <summary>10 進の整数。</summary>
    Decimal,

    /// <summary>16 進の整数。<c>0x</c> で始まる。</summary>
    Hexadecimal,

    /// <summary>8 進の整数。<c>0</c> に数字が続く。</summary>
    Octal,

    /// <summary>小数点または指数を持つ。</summary>
    Floating,
}

/// <summary>
/// 数値リテラルの表記を読み解く。
/// </summary>
/// <remarks>
/// <para>
/// <b>リテラルの表記を各所で自前に読んではいけない。</b>
/// HLSL の数値リテラルは 10 進・16 進・8 進があり、
/// <c>f</c> <c>h</c> <c>u</c> <c>l</c> の接尾辞が付く。
/// <c>int.TryParse(token.Text)</c> で済ませると
/// <c>0x10</c> も <c>2u</c> も読めないまま「数字で書かれていない」と扱われ、
/// 検査がそのまま素通りする。
/// </para>
/// <para>
/// <b>読めないものは読めないと返す。</b>
/// 推測した値を返すと、それを根拠にした指摘が出る。
/// </para>
/// </remarks>
public static class HlslLiteral
{
    /// <summary>型を決める接尾辞に使える文字。</summary>
    private const string SuffixCharacters = "uUlLfFhH";

    /// <summary>
    /// リテラルの書き方を求める。
    /// </summary>
    /// <param name="token">対象のトークン。</param>
    /// <returns>書き方。数値リテラルとして読めない場合は <see cref="HlslLiteralKind.None"/>。</returns>
    /// <remarks>
    /// 桁の大きさを自分で数えるルール (HL0371 など) が、
    /// 8 進や 16 進の見分け方を書き写さずに済むようにしている。
    /// </remarks>
    public static HlslLiteralKind GetKind(HlslSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (token.Kind != HlslSyntaxKind.NumericLiteralToken)
        {
            return HlslLiteralKind.None;
        }

        (string digits, _) = Split(token.Text);

        if (digits.Length == 0)
        {
            return HlslLiteralKind.None;
        }

        if (IsHex(digits))
        {
            return HlslLiteralKind.Hexadecimal;
        }

        if (IsFloating(digits))
        {
            return HlslLiteralKind.Floating;
        }

        if (IsOctal(digits))
        {
            return HlslLiteralKind.Octal;
        }

        return digits.All(char.IsAsciiDigit) ? HlslLiteralKind.Decimal : HlslLiteralKind.None;
    }

    /// <summary>
    /// 接尾辞を除いた表記を返す。
    /// </summary>
    /// <param name="token">対象のトークン。</param>
    /// <returns>接尾辞を除いた表記。数値リテラルでない場合は空文字列。</returns>
    /// <remarks>16 進では末尾の <c>f</c> は桁なので、接尾辞として落とさない。</remarks>
    public static string GetDigits(HlslSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return token.Kind == HlslSyntaxKind.NumericLiteralToken ? Split(token.Text).Digits : string.Empty;
    }

    /// <summary>
    /// 数値リテラルと文字リテラルの型名を求める。
    /// </summary>
    /// <param name="token">対象のトークン。</param>
    /// <returns>型名。数値リテラルでも文字リテラルでもない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 接尾辞が付いていればそれに従う。付いていなければ、
    /// 小数点や指数を持つものを <c>float</c>、それ以外を <c>int</c> とする。
    /// </para>
    /// <para>
    /// <b>文字リテラル (<c>'r'</c>) は <c>uint</c> である。</b>
    /// C の <c>int</c> とは違う。fxc (X3017: cannot convert from 'uint') と
    /// DXC (rvalue of type 'unsigned int') のどちらも <c>uint</c> と答えることを確かめた。
    /// HDRP のデバッグ表示は画面に文字を描くためにこれを多用しており、
    /// 型を答えないと、その引数を含む式が軒並み検査されなかった。
    /// </para>
    /// <para>
    /// <b>接尾辞の無いリテラルの型は、厳密にはその場の文脈で決まる。</b>
    /// DXC は「リテラル float」という中間の型を持ち、
    /// <c>half</c> を受け取る所へ渡せば <c>half</c> として振る舞う。
    /// ここで <c>float</c> と答えるのは、その中間の型を最も広く言い換えたものである。
    /// 暗黙の変換の判定 (<c>HlslConversion</c>) は基底型の違いを見ないため、
    /// この答えで <c>half</c> への代入が誤りになることはない。
    /// </para>
    /// </remarks>
    public static string? GetTypeName(HlslSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (token.Kind == HlslSyntaxKind.CharacterLiteralToken)
        {
            return "uint";
        }

        if (token.Kind != HlslSyntaxKind.NumericLiteralToken)
        {
            return null;
        }

        (string digits, string suffix) = Split(token.Text);

        if (digits.Length == 0)
        {
            return null;
        }

        if (suffix.Contains('f', StringComparison.OrdinalIgnoreCase))
        {
            return "float";
        }

        if (suffix.Contains('h', StringComparison.OrdinalIgnoreCase))
        {
            return "half";
        }

        bool floating = IsFloating(digits);

        if (suffix.Contains('l', StringComparison.OrdinalIgnoreCase))
        {
            // 浮動小数に付く l は倍精度、整数に付く l は幅の指定であって型は int のままである。
            return floating ? "double" : "int";
        }

        if (suffix.Contains('u', StringComparison.OrdinalIgnoreCase))
        {
            return "uint";
        }

        return floating ? "float" : "int";
    }

    /// <summary>
    /// 整数リテラルの値を求める。
    /// </summary>
    /// <param name="token">対象のトークン。</param>
    /// <param name="value">読み取った値。</param>
    /// <returns>整数として読めた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// 16 進 (<c>0x10</c>)、8 進 (<c>010</c>)、接尾辞付き (<c>2u</c>) を読む。
    /// 小数点や指数を持つものは整数ではないので <see langword="false"/> を返す。
    /// </remarks>
    public static bool TryGetInt64(HlslSyntaxToken token, out long value)
    {
        ArgumentNullException.ThrowIfNull(token);

        value = 0;

        if (token.Kind != HlslSyntaxKind.NumericLiteralToken)
        {
            return false;
        }

        (string digits, _) = Split(token.Text);

        if (digits.Length == 0 || IsFloating(digits))
        {
            return false;
        }

        if (IsHex(digits))
        {
            return long.TryParse(
                digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        if (IsOctal(digits))
        {
            try
            {
                value = Convert.ToInt64(digits, 8);
                return true;
            }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
            {
                return false;
            }
        }

        return long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// 数値リテラルの値を求める。
    /// </summary>
    /// <param name="token">対象のトークン。</param>
    /// <param name="value">読み取った値。</param>
    /// <returns>数として読めた場合は <see langword="true"/>。</returns>
    /// <remarks>整数も読める。閾値との比較にはこちらを使う。</remarks>
    public static bool TryGetDouble(HlslSyntaxToken token, out double value)
    {
        ArgumentNullException.ThrowIfNull(token);

        value = 0;

        if (token.Kind != HlslSyntaxKind.NumericLiteralToken)
        {
            return false;
        }

        (string digits, _) = Split(token.Text);

        if (digits.Length == 0)
        {
            return false;
        }

        if (!IsFloating(digits))
        {
            if (!TryGetInt64(token, out long integer))
            {
                return false;
            }

            value = integer;
            return true;
        }

        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// 小数点や指数を持つ表記かどうかを判定する。
    /// </summary>
    /// <param name="digits">接尾辞を除いた表記。</param>
    /// <returns>浮動小数の表記なら <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>16 進では <c>e</c> は指数ではなく桁である。</b>
    /// <c>0x1e</c> を浮動小数と読むと、値が求まらなくなる。
    /// </remarks>
    private static bool IsFloating(string digits)
        => !IsHex(digits)
           && (digits.Contains('.', StringComparison.Ordinal)
               || digits.Contains('e', StringComparison.OrdinalIgnoreCase));

    /// <summary>16 進の表記かどうかを判定する。</summary>
    /// <param name="digits">接尾辞を除いた表記。</param>
    /// <returns>16 進なら <see langword="true"/>。</returns>
    private static bool IsHex(string digits)
        => digits.Length > 2 && digits[0] == '0' && (digits[1] is 'x' or 'X');

    /// <summary>8 進の表記かどうかを判定する。</summary>
    /// <param name="digits">接尾辞を除いた表記。</param>
    /// <returns>8 進なら <see langword="true"/>。</returns>
    /// <remarks>先頭の <c>0</c> に数字が続くものが 8 進である。</remarks>
    private static bool IsOctal(string digits)
        => digits.Length > 1 && digits[0] == '0' && digits.All(c => c is >= '0' and <= '7');

    /// <summary>表記を、数の部分と接尾辞に分ける。</summary>
    /// <param name="text">リテラルの表記。</param>
    /// <returns>数の部分と接尾辞。</returns>
    /// <remarks>
    /// 16 進では末尾の <c>f</c> は桁である。
    /// <c>0xff</c> を「接尾辞 ff」と読まないよう、16 進は分けない。
    /// </remarks>
    private static (string Digits, string Suffix) Split(string text)
    {
        if (IsHex(text))
        {
            int hexEnd = text.Length;

            // 16 進で接尾辞になりうるのは u と l だけである。
            while (hexEnd > 2 && text[hexEnd - 1] is 'u' or 'U' or 'l' or 'L')
            {
                hexEnd--;
            }

            return (text[..hexEnd], text[hexEnd..]);
        }

        int end = text.Length;

        while (end > 0 && SuffixCharacters.Contains(text[end - 1], StringComparison.Ordinal))
        {
            end--;
        }

        return (text[..end], text[end..]);
    }
}
