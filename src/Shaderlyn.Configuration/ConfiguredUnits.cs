using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace Shaderlyn.Configuration;

/// <summary>
/// 設定ファイルの <c>symbols:</c> で宣言した、構成の軸になるシンボル。
/// </summary>
/// <param name="Name">シンボルの名前。</param>
/// <param name="Values">とりうる値。空なら「定義されているか・いないか」。先頭が既定の値。</param>
/// <param name="Line">書いた 0 始まりの行。</param>
internal sealed record ConfiguredSymbol(string Name, ImmutableArray<int> Values, int Line);

/// <summary>
/// 設定ファイルの <c>symbols:</c> で宣言した、同時には有効にならないシンボルの組。
/// </summary>
/// <param name="Symbols">組のシンボル。</param>
/// <param name="RequiresOne">どれか 1 つが必ず有効か (<c>one-of</c>)。そうでなければ <c>at-most-one</c>。</param>
/// <param name="Line">書いた 0 始まりの行。</param>
internal sealed record ConfiguredSymbolGroup(ImmutableArray<string> Symbols, bool RequiresOne, int Line);

/// <summary>
/// 設定ファイルの <c>units:</c> に書いた、コンパイル単位 1 つ。
/// </summary>
/// <param name="Files">対象のファイルの glob (<c>Shaders/**/*.hlsl</c>)。設定ファイルの場所からの相対。</param>
/// <param name="BaseDirectory">glob の基点 (設定ファイルのあるフォルダー)。</param>
/// <param name="Line">書いた 0 始まりの行。</param>
/// <remarks>
/// <b>1 つのファイルに複数の単位を当てられる。</b> 頂点シェーダーとピクセルシェーダーを 1 つのファイルに書き、
/// エントリポイントを変えて 2 回コンパイルする構成がそうである。
/// </remarks>
internal sealed record ConfiguredUnit(string Files, string BaseDirectory, int Line)
{
    private Regex? _pattern;

    /// <summary>エントリポイントの関数名 (<c>entry</c>)。</summary>
    public string? Entry { get; init; }

    /// <summary>コンパイルのターゲット (<c>target</c>、<c>ps_6_6</c> の形)。</summary>
    public string? Target { get; init; }

    /// <summary>この単位でだけ定義するマクロ (<c>defines</c>)。<c>NAME</c> または <c>NAME=VALUE</c>。</summary>
    public ImmutableArray<string> Defines { get; init; } = [];

    /// <summary>
    /// この単位で使う構成の軸 (<c>symbols</c>)。<see langword="null"/> なら <c>symbols:</c> で宣言したものすべて。
    /// </summary>
    public ImmutableArray<string>? Symbols { get; init; }

    /// <summary>ファイルがこの単位の対象かを判定する。</summary>
    /// <param name="filePath">ファイルのパス。</param>
    /// <returns>対象なら <see langword="true"/>。</returns>
    public bool Matches(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        string relative = Path.GetRelativePath(BaseDirectory, Path.GetFullPath(filePath)).Replace('\\', '/');
        return (_pattern ??= PathGlob.ToRegex(Files)).IsMatch(relative);
    }
}

/// <summary>
/// パスの glob (<c>**</c> / <c>*</c> / <c>?</c>) を正規表現に直す。
/// </summary>
/// <remarks>
/// 区切りは <c>/</c>。<c>**/</c> は 0 個以上のフォルダー、<c>*</c> と <c>?</c> は区切りをまたがない。
/// Windows では大文字と小文字を区別しない (ファイルシステムに合わせる)。
/// </remarks>
internal static class PathGlob
{
    /// <summary>glob を正規表現に直す。</summary>
    /// <param name="glob">glob。</param>
    /// <returns>パス全体に一致する正規表現。</returns>
    public static Regex ToRegex(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);

        string pattern = glob.Replace('\\', '/');

        if (pattern.StartsWith("./", StringComparison.Ordinal))
        {
            pattern = pattern[2..];
        }

        StringBuilder regex = new("^");

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];

            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                bool slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                regex.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*')
            {
                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        regex.Append('$');

        RegexOptions options = RegexOptions.CultureInvariant;

        if (OperatingSystem.IsWindows())
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(regex.ToString(), options);
    }
}
