using System.Globalization;

namespace Shaderlyn.Rules.Godot;

/// <summary>Godot シェーダー言語のリテラルを読む。</summary>
internal static class GodotLiterals
{
    /// <summary>整数のリテラル (10 進と 16 進、<c>u</c> の接尾辞) を読む。</summary>
    /// <param name="text">リテラルの文字列。</param>
    /// <param name="value">読んだ値。</param>
    /// <returns>整数のリテラルなら <see langword="true"/>。</returns>
    public static bool TryParseInteger(string text, out long value)
    {
        string digits = text.TrimEnd('u', 'U');

        return digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(digits.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
