using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Shaderlyn.Configuration.Yaml;

/// <summary>
/// 読み込んだ YAML の 1 要素。
/// </summary>
/// <remarks>
/// <para>
/// スカラー・写像 (マッピング)・並び (シーケンス) の 3 種類しか持たない。
/// 設定ファイルの表現に必要なのはこれだけである。
/// </para>
/// <para>
/// どの要素も元の行番号を保持する。
/// <b>設定の誤りは、その設定が書かれた行を指して報告できなければ直せない。</b>
/// </para>
/// </remarks>
internal abstract class YamlNode
{
    /// <summary>要素が書かれていた 0 始まりの行番号。</summary>
    public required int Line { get; init; }
}

/// <summary>スカラー値。</summary>
internal sealed class YamlScalar : YamlNode
{
    /// <summary>値の文字列表現。</summary>
    public required string Value { get; init; }

    /// <summary>
    /// 真偽値として解釈する。
    /// </summary>
    /// <param name="value">解釈できた値。</param>
    /// <returns>解釈できた場合は <see langword="true"/>。</returns>
    /// <remarks>YAML 1.1 で真偽値として扱われる綴りのうち、実際に使われるものを受け付ける。</remarks>
    public bool TryGetBoolean(out bool value)
    {
        switch (Value.ToLowerInvariant())
        {
            case "true" or "yes" or "on":
                value = true;
                return true;

            case "false" or "no" or "off":
                value = false;
                return true;

            default:
                value = false;
                return false;
        }
    }

    /// <summary>整数として解釈する。</summary>
    /// <param name="value">解釈できた値。</param>
    /// <returns>解釈できた場合は <see langword="true"/>。</returns>
    public bool TryGetInt32(out int value)
        => int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>値をそのまま返す。</summary>
    /// <returns>スカラーの文字列表現。</returns>
    public override string ToString() => Value;
}

/// <summary>キーと値の対応。</summary>
internal sealed class YamlMapping : YamlNode
{
    /// <summary>キーから値への対応。</summary>
    /// <remarks>
    /// キーの大文字小文字は区別する。
    /// 設定ファイルのキーは仕様として決まっており、
    /// 揺れを許すと「書いたつもりが効いていない」誤りを見逃す。
    /// </remarks>
    public required ImmutableDictionary<string, YamlNode> Entries { get; init; }

    /// <summary>キーが現れた順。</summary>
    /// <remarks>診断を書かれた順に報告するために保持する。</remarks>
    public required ImmutableArray<string> Keys { get; init; }

    /// <summary>指定したキーの値を取り出す。</summary>
    /// <param name="key">キー。</param>
    /// <param name="node">見つかった値。</param>
    /// <returns>見つかった場合は <see langword="true"/>。</returns>
    public bool TryGet(string key, [NotNullWhen(true)] out YamlNode? node)
        => Entries.TryGetValue(key, out node);

    /// <summary>指定したキーの値をスカラーとして取り出す。</summary>
    /// <param name="key">キー。</param>
    /// <param name="scalar">見つかったスカラー。</param>
    /// <returns>見つかり、かつスカラーであれば <see langword="true"/>。</returns>
    public bool TryGetScalar(string key, [NotNullWhen(true)] out YamlScalar? scalar)
    {
        scalar = Entries.TryGetValue(key, out YamlNode? node) ? node as YamlScalar : null;
        return scalar is not null;
    }
}

/// <summary>値の並び。</summary>
internal sealed class YamlSequence : YamlNode
{
    /// <summary>並びの要素。</summary>
    public required ImmutableArray<YamlNode> Items { get; init; }
}
