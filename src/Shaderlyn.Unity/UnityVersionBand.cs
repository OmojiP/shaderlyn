using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Shaderlyn.Unity;

/// <summary>
/// シェーダーのコンパイラの選び方が同じ、Unity のバージョンのまとまり。
/// </summary>
/// <remarks>
/// <para>
/// 2022.3 / 2023.2 / 6.0〜6.6 の実測で分かれたまとまりである。2023.2 / 6.1 / 6.2 は 6.0 / 6.3 と同じだった。
/// </para>
/// <list type="bullet">
///   <item>2022.3: include ファイルの中の <c>#pragma use_dxc</c> / <c>require</c> も効く。<c>require inlineraytracing</c> を知らない</item>
///   <item>2023.2〜6.3: include ファイルの中の指定は効かない。<c>require inlineraytracing</c> はコンパイルされない</item>
///   <item>6.4〜6.5: <c>require inlineraytracing</c> が DXC になる</item>
///   <item>6.6〜: DX11 と DX12 を分けて扱う (<c>use_dxc dx12</c>、Shader Compiler Backend の設定)。<c>require</c> が <c>never_use_dxc</c> に勝つ</item>
/// </list>
/// </remarks>
public enum UnityVersionBand
{
    /// <summary>2022.3 (とそれより前)。</summary>
    Unity2022_3,

    /// <summary>2023.2〜6.3。</summary>
    Unity2023_2To6_3,

    /// <summary>6.4〜6.5。</summary>
    Unity6_4To6_5,

    /// <summary>6.6 以降。</summary>
    Unity6_6OrLater,
}

/// <summary>
/// Unity のバージョンの読み取り。
/// </summary>
public static class UnityVersions
{
    /// <summary>まとまりの一覧 (古い順)。</summary>
    public static IReadOnlyList<UnityVersionBand> AllBands { get; } =
        [UnityVersionBand.Unity2022_3, UnityVersionBand.Unity2023_2To6_3, UnityVersionBand.Unity6_4To6_5, UnityVersionBand.Unity6_6OrLater];

    /// <summary>まとまりの画面に出す名前。</summary>
    /// <param name="band">まとまり。</param>
    /// <returns>名前 (<c>Unity 6.4〜6.5</c> など)。</returns>
    public static string Describe(UnityVersionBand band) => band switch
    {
        UnityVersionBand.Unity2022_3 => "Unity 2022.3",
        UnityVersionBand.Unity2023_2To6_3 => "Unity 2023.2〜6.3",
        UnityVersionBand.Unity6_4To6_5 => "Unity 6.4〜6.5",
        _ => "Unity 6.6 以降",
    };

    /// <summary>
    /// バージョンを読んで、まとまりを返す。
    /// </summary>
    /// <param name="version">
    /// バージョン。<c>ProjectVersion.txt</c> の書き方 (<c>6000.3.0f1</c> / <c>2022.3.62f2</c>) と、
    /// 短い書き方 (<c>6.3</c> / <c>2022.3</c>) を受け付ける。
    /// </param>
    /// <param name="band">まとまり。</param>
    /// <returns>読めたら <see langword="true"/>。</returns>
    public static bool TryGetBand(string? version, [NotNullWhen(true)] out UnityVersionBand? band)
    {
        band = null;

        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        string[] parts = version.Trim().Split('.');

        if (parts.Length < 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(LeadingDigits(parts[1]), NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
        {
            return false;
        }

        // Unity 6 は 6000.x と書かれる。
        if (major == 6000)
        {
            major = 6;
        }

        band = major switch
        {
            // 2022.3 より前は確かめていないが、include の中の指定が効く古い側として扱う。
            >= 2017 and <= 2022 => UnityVersionBand.Unity2022_3,
            2023 => minor >= 2 ? UnityVersionBand.Unity2023_2To6_3 : UnityVersionBand.Unity2022_3,
            6 when minor <= 3 => UnityVersionBand.Unity2023_2To6_3,
            6 when minor <= 5 => UnityVersionBand.Unity6_4To6_5,
            6 => UnityVersionBand.Unity6_6OrLater,

            // 7 以降は、確かめた最も新しい 6.6 と同じとみなす。
            >= 7 and < 2017 => UnityVersionBand.Unity6_6OrLater,
            _ => null,
        };

        return band is not null;
    }

    /// <summary>先頭の数字だけを取り出す (<c>0f1</c> → <c>0</c>)。</summary>
    /// <param name="text">文字列。</param>
    /// <returns>数字の部分。</returns>
    private static string LeadingDigits(string text)
    {
        int length = 0;

        while (length < text.Length && char.IsAsciiDigit(text[length]))
        {
            length++;
        }

        return text[..length];
    }
}
