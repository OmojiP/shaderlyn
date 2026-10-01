namespace Shaderlyn.LanguageServer;

/// <summary>
/// LSP の URI とファイルパスを相互に変換する。
/// </summary>
/// <remarks>
/// <para>
/// <b>エディタは URI、解析はパスで話す。</b>
/// 変換を 1 か所にまとめておかないと、
/// Windows のドライブ文字や日本語を含むパスの扱いが場所ごとに食い違う。
/// </para>
/// <para>
/// <b>同じファイルを指す URI の書き方は 1 つではない。</b>
/// VS Code は Windows のドライブ文字のコロンを <c>file:///c%3A/...</c> と符号化して送ってくるが、
/// .NET の <see cref="Uri.AbsoluteUri"/> は <c>file:///c:/...</c> を返す。
/// 文字列としては別物であり、
/// エディタは開いているスクリプトとは<b>別のスクリプト</b>として扱う。
/// 定義へ飛ぶ操作でこれを返すと、同じファイルなのに新しいタブが開き、
/// 「ファイルが見つからなかったため、エディターを開くことができませんでした」と表示される。
/// </para>
/// </remarks>
internal static class DocumentUri
{
    /// <summary>ドライブ文字のコロンを符号化した形。</summary>
    private const string EncodedDriveColon = "%3A";

    /// <summary>
    /// URI をファイルパスへ変換する。
    /// </summary>
    /// <param name="uri">LSP の URI。</param>
    /// <returns>ファイルパス。変換できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b><see cref="Uri.LocalPath"/> は符号化されたドライブ文字を見抜けない。</b>
    /// VS Code は <c>file:///c%3A/Users/...</c> と送ってくるが、
    /// <see cref="Uri"/> は解析の時点で <c>c%3A</c> をドライブとして認識せず、
    /// 復号だけして <c>/c:/Users/...</c> を返す。先頭のスラッシュが残る。
    /// </para>
    /// <para>
    /// これを <see cref="Path.GetFullPath(string)"/> に渡すと、
    /// 現在のドライブからの絶対パスとみなされて <c>C:\c:\Users\...</c> になる。
    /// <b>存在しようのないパスである。</b>
    /// エディタはこれを開こうとして
    /// 「ファイルが見つからなかったため、エディターを開くことができませんでした」と表示する。
    /// </para>
    /// <para>
    /// ドライブ文字が付いた形に直してから返す。
    /// </para>
    /// </remarks>
    public static string? ToFilePath(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || !parsed.IsFile)
        {
            return null;
        }

        string path = parsed.LocalPath;

        return HasLeadingSlashBeforeDrive(path) ? path[1..] : path;
    }

    /// <summary>
    /// パスが <c>/c:/...</c> の形をしているかを判定する。
    /// </summary>
    /// <param name="path">判定するパス。</param>
    /// <returns>この形であれば <see langword="true"/>。</returns>
    /// <remarks>
    /// UNC パス (<c>\\server\share</c>) や、
    /// ドライブ文字を持たない環境の絶対パスと取り違えないよう、
    /// 「スラッシュ、英字 1 文字、コロン」がそろっている場合だけを対象にする。
    /// </remarks>
    private static bool HasLeadingSlashBeforeDrive(string path)
        => path.Length >= 3
           && (path[0] == '/' || path[0] == '\\')
           && char.IsAsciiLetter(path[1])
           && path[2] == ':';

    /// <summary>
    /// ファイルパスを URI へ変換する。
    /// </summary>
    /// <param name="filePath">ファイルパス。</param>
    /// <returns>LSP の URI。</returns>
    public static string FromFilePath(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        return new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
    }

    /// <summary>
    /// 相手が使っている書き方に合わせて、ファイルパスを URI へ変換する。
    /// </summary>
    /// <param name="filePath">ファイルパス。</param>
    /// <param name="likeUri">相手から受け取った URI。書き方の見本として使う。</param>
    /// <returns>LSP の URI。</returns>
    /// <remarks>
    /// <b>こちらの流儀を押し付けない。</b>
    /// 送り手がドライブ文字のコロンを符号化しているなら、こちらも符号化して返す。
    /// 同じファイルを指しているのに文字列が違うと、
    /// エディタはそれを別のスクリプトとして開こうとする。
    /// </remarks>
    public static string FromFilePath(string filePath, string? likeUri)
    {
        string uri = FromFilePath(filePath);

        return likeUri is not null && UsesEncodedDriveColon(likeUri)
            ? EncodeDriveColon(uri)
            : uri;
    }

    /// <summary>URI がドライブ文字のコロンを符号化しているかを判定する。</summary>
    /// <param name="uri">調べる URI。</param>
    /// <returns>符号化していれば <see langword="true"/>。</returns>
    private static bool UsesEncodedDriveColon(string uri)
        => uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
           && uri.Length >= 12
           && char.IsAsciiLetter(uri[8])
           && uri.AsSpan(9, 3).Equals(EncodedDriveColon, StringComparison.OrdinalIgnoreCase);

    /// <summary>ドライブ文字のコロンを符号化する。</summary>
    /// <param name="uri">対象の URI。</param>
    /// <returns>符号化した URI。ドライブ文字で始まらない場合はそのまま。</returns>
    private static string EncodeDriveColon(string uri)
        => uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
           && uri.Length >= 10
           && char.IsAsciiLetter(uri[8])
           && uri[9] == ':'
            ? string.Concat(uri.AsSpan(0, 9), EncodedDriveColon, uri.AsSpan(10))
            : uri;
}
