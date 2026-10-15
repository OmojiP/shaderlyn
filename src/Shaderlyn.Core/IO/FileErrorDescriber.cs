namespace Shaderlyn.Core.IO;

/// <summary>
/// ファイルを読み書きできなかった理由を、利用者が直せる言葉にする。
/// </summary>
/// <remarks>
/// <para>
/// <b>例外のメッセージをそのまま出さない。</b>
/// 配布する CLI は Native AOT で、<c>UseSystemResourceKeys</c> を有効にしている。
/// 例外のメッセージは <c>UnauthorizedAccess_IODenied_Path</c> のようなリソースの名前になり、利用者には読めない。
/// 例外の種類と、パスの実際の状態 (フォルダーか、上のフォルダーがあるか) から理由を作る。
/// </para>
/// <para>
/// 決められないときだけ、例外の種類と HRESULT を添える (調べる手がかりになる)。
/// </para>
/// </remarks>
internal static class FileErrorDescriber
{
    /// <summary>Windows の、ほかのプロセスが使っているときの HRESULT (ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION)。</summary>
    private static readonly int[] SharingViolations = [unchecked((int)0x80070020), unchecked((int)0x80070021)];

    /// <summary>Windows の、ディスクの空きが無いときの HRESULT (ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL)。</summary>
    private static readonly int[] DiskFull = [unchecked((int)0x80070070), unchecked((int)0x80070027)];

    /// <summary>Windows の、名前が正しくないときの HRESULT (ERROR_INVALID_NAME / ERROR_BAD_PATHNAME)。</summary>
    private static readonly int[] InvalidName = [unchecked((int)0x8007007B), unchecked((int)0x800700A1)];

    /// <summary>Windows で、ファイルの名前に使えない名前 (デバイス名)。</summary>
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 例外から、読み書きできなかった理由を作る。
    /// </summary>
    /// <param name="exception">読み書きで起きた例外。</param>
    /// <param name="path">読み書きしようとしたパス。</param>
    /// <returns>理由 (「〜です」「〜がありません」の形の文)。</returns>
    public static string Describe(Exception exception, string path)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(path);

        if (exception is ArgumentException or NotSupportedException || TryGetFullPath(path) is not { } fullPath)
        {
            return "パスに使えない文字が含まれています。";
        }

        if (Directory.Exists(fullPath))
        {
            return "そのパスはフォルダーです。ファイルのパスを指定してください。";
        }

        // 途中のフォルダーの位置にファイルがある (a.txt/cache.json)。
        if (FindFileInAncestors(fullPath) is { } file)
        {
            return $"途中の '{file}' がフォルダーではなくファイルです。";
        }

        if (Path.GetPathRoot(fullPath) is { Length: > 0 } root && !Directory.Exists(root))
        {
            return $"ドライブ (またはネットワークの場所) '{root}' がありません。";
        }

        return exception switch
        {
            FileNotFoundException => "ファイルがありません。",
            DirectoryNotFoundException => $"フォルダー '{Path.GetDirectoryName(fullPath)}' がありません。",
            PathTooLongException => "パスが長すぎます。",
            UnauthorizedAccessException => File.Exists(fullPath) && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReadOnly)
                ? "ファイルが読み取り専用です。"
                : "アクセスする権限がありません。",
            IOException io when SharingViolations.Contains(io.HResult) => "ほかのプログラムがファイルを使っています。",
            IOException io when DiskFull.Contains(io.HResult) => "ディスクの空きがありません。",
            IOException io when InvalidName.Contains(io.HResult) => "パスに使えない文字が含まれています。",
            _ => $"{exception.GetType().Name} (0x{exception.HResult:X8}) が起きました。",
        };
    }

    /// <summary>
    /// パスが、ファイルとして書けない名前 (Windows のデバイス名 <c>NUL</c> など) かを判定する。
    /// </summary>
    /// <param name="path">調べるパス。</param>
    /// <returns>デバイス名なら <see langword="true"/>。Windows 以外では常に <see langword="false"/>。</returns>
    /// <remarks>
    /// <c>NUL</c> に書くと、書き込みは成功したことになり、何も残らない。
    /// </remarks>
    public static bool IsDeviceName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return OperatingSystem.IsWindows()
               && WindowsDeviceNames.Contains(Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\', ':', '.', ' ')));
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>パスの途中にあるファイル (フォルダーであるべき位置) を探す。</summary>
    private static string? FindFileInAncestors(string fullPath)
    {
        for (string? current = Path.GetDirectoryName(fullPath); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current))
            {
                return current;
            }
        }

        return null;
    }
}
