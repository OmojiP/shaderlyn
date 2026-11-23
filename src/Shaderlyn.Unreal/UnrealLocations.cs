using System.Text.Json;
using Microsoft.Win32;

namespace Shaderlyn.Unreal;

/// <summary>
/// 設定ファイルに書いていないエンジンとプロジェクトの場所を、解析するファイルから見つけた結果。
/// </summary>
/// <param name="EngineDirectory">エンジンの <c>Engine</c> フォルダー。見つからなければ <see langword="null"/>。</param>
/// <param name="ProjectDirectory">プロジェクトのフォルダー (<c>.uproject</c> のあるフォルダー)。見つからなければ <see langword="null"/>。</param>
/// <param name="Description">どう見つけたか、なぜ見つからなかったか (<c>--env</c> と言語サーバーの記録に出す)。</param>
internal sealed record UnrealLocations(string? EngineDirectory, string? ProjectDirectory, string Description)
{
    /// <summary>探さなかった、または何も見つからなかった。</summary>
    public static UnrealLocations None { get; } = new(null, null, "見つかりませんでした");
}

/// <summary>
/// 設定ファイルに書いていないエンジンとプロジェクトの場所を、解析するファイルから見つける。
/// </summary>
/// <remarks>
/// <para>
/// 手がかりは 2 つある。どちらも、設定ファイルの <c>unreal: engine</c> / <c>unreal: project</c> を書いていない場合だけ使う。
/// </para>
/// <list type="number">
///   <item>
///     <b>ファイルがエンジンの中にある。</b> <c>&lt;X&gt;/Engine/Shaders/…</c> のファイルなら、エンジンは <c>&lt;X&gt;/Engine</c> である。
///     ファイルの場所だけで決まる。
///   </item>
///   <item>
///     <b>上のフォルダーに <c>.uproject</c> がある。</b> そのフォルダーがプロジェクトで、エンジンは <c>EngineAssociation</c> から決める。
///     空ならプロジェクトを含むエンジンの木 (エンジンのソースの中のプロジェクト)、<c>5.5</c> のようなバージョンならインストールしたエンジン、
///     <c>{GUID}</c> ならソースからビルドして登録したエンジンである (エディタの「エンジンのバージョンの切り替え」と同じ)。
///     PC に何がインストールされているかで結果が変わる。
///   </item>
/// </list>
/// <para>
/// <b>どこまで上へたどるかは呼び出し側が決める。</b> CLI は止めずにたどり、言語サーバーはワークスペースのルートで止める (利用者の決定)。
/// 同じフォルダーに <c>.uproject</c> が複数あれば、どれか決められないので使わない (そこで探すのをやめる)。
/// エンジンのフォルダーは、印のファイル (<c>Build/Build.version</c> と <c>Shaders/Public/Platform.ush</c>) があるものだけを使う。
/// </para>
/// </remarks>
internal static class UnrealLocator
{
    /// <summary>エンジンのフォルダーにある印のファイル。</summary>
    private static readonly string[] EngineMarkers = [Path.Combine("Build", "Build.version"), Path.Combine("Shaders", "Public", "Platform.ush")];

    /// <summary>解析するファイルかフォルダーから、エンジンとプロジェクトを探す。</summary>
    /// <param name="start">解析するファイルかフォルダー。</param>
    /// <param name="stopAt">ここより上へはたどらない (このフォルダーまでは見る)。<see langword="null"/> なら止めない。</param>
    /// <returns>見つけた場所。</returns>
    public static UnrealLocations Find(string start, string? stopAt)
    {
        ArgumentNullException.ThrowIfNull(start);

        DirectoryInfo? first;
        try
        {
            string full = Path.GetFullPath(start);
            first = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full).Directory;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return UnrealLocations.None;
        }

        string? stop = stopAt is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));

        for (DirectoryInfo? directory = first; directory is not null; directory = directory.Parent)
        {
            // 手がかり 1: <X>/Engine/Shaders の中。
            if (directory.Name.Equals("Shaders", StringComparison.OrdinalIgnoreCase)
                && directory.Parent is { } parent
                && parent.Name.Equals("Engine", StringComparison.OrdinalIgnoreCase)
                && IsEngine(parent.FullName))
            {
                // エンジンのファイルにプロジェクトはない。上へたどると、エンジンの外の無関係な .uproject を掴む。
                return new UnrealLocations(parent.FullName, null, $"ファイルがエンジンの中にある ({parent.FullName})");
            }

            // 手がかり 2: .uproject。
            string[] projects = SafeGetFiles(directory.FullName, "*.uproject");

            if (projects.Length > 1)
            {
                return new UnrealLocations(null, null,
                    $"{directory.FullName} に .uproject が {projects.Length} 個あり、どれか決められないので使いません");
            }

            if (projects.Length == 1)
            {
                string project = directory.FullName;
                (string? associated, string how) = EngineOf(projects[0]);
                return new UnrealLocations(associated, project, $"{projects[0]} の EngineAssociation から{how}");
            }

            if (stop is not null && Path.TrimEndingDirectorySeparator(directory.FullName).Equals(stop, PathComparison))
            {
                break;
            }
        }

        return UnrealLocations.None;
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>エンジンの Engine フォルダーか (印のファイルがあるか)。</summary>
    /// <param name="directory">調べるフォルダー。</param>
    /// <returns>そうなら <see langword="true"/>。</returns>
    public static bool IsEngine(string directory)
        => EngineMarkers.All(marker => File.Exists(Path.Combine(directory, marker)));

    private static string[] SafeGetFiles(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary><c>.uproject</c> の <c>EngineAssociation</c> から、エンジンの Engine フォルダーを決める。</summary>
    /// <param name="uproject">.uproject のパス。</param>
    /// <returns>Engine フォルダー (見つからなければ <see langword="null"/>) と、どう決めたか。</returns>
    private static (string? Engine, string How) EngineOf(string uproject)
    {
        string association;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(uproject), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            association = document.RootElement.TryGetProperty("EngineAssociation", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"決めようとしましたが、読み込めませんでした ({e.Message})");
        }

        if (association.Length == 0)
        {
            // エンジンのソースの中のプロジェクト。上のフォルダーの Engine を探す。
            for (DirectoryInfo? directory = new FileInfo(uproject).Directory; directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "Engine");
                if (IsEngine(candidate))
                {
                    return (candidate, $"決めました (空なので、プロジェクトを含むエンジン {candidate})");
                }
            }

            return (null, "決めようとしましたが、空で、プロジェクトを含むエンジンが見つかりませんでした");
        }

        foreach ((string root, string source) in InstalledEngines(association))
        {
            string candidate = Path.Combine(root, "Engine");
            if (IsEngine(candidate))
            {
                return (candidate, $"決めました ({association}。{source})");
            }
        }

        return (null, $"決めようとしましたが、{association} のエンジンがこの PC に見つかりませんでした");
    }

    /// <summary>EngineAssociation に当たる、インストールしたエンジンのルートの候補を、優先する順に返す。</summary>
    /// <param name="association">EngineAssociation の値。</param>
    /// <returns>ルートのフォルダーと、どこに登録されていたか。</returns>
    /// <remarks>
    /// エンジン自身 (DesktopPlatform) が探す場所と同じ所を見る。
    /// Windows: ソースのビルドは HKCU\Software\Epic Games\Unreal Engine\Builds、インストールしたものは
    /// HKLM\SOFTWARE\EpicGames\Unreal Engine\&lt;バージョン&gt; の InstalledDirectory と Epic Games Launcher の LauncherInstalled.dat。
    /// Linux・macOS: Install.ini の [Installations] と、macOS の LauncherInstalled.dat。
    /// </remarks>
    private static IEnumerable<(string Root, string Source)> InstalledEngines(string association)
    {
        if (OperatingSystem.IsWindows())
        {
            if (ReadRegistry(Registry.CurrentUser, @"Software\Epic Games\Unreal Engine\Builds", association) is { } build)
            {
                yield return (build, "ソースからビルドしたエンジンの登録");
            }

            if (ReadRegistry(Registry.LocalMachine, $@"SOFTWARE\EpicGames\Unreal Engine\{association}", "InstalledDirectory") is { } installed)
            {
                yield return (installed, "インストールしたエンジンの登録");
            }

            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            foreach (string launcher in LauncherInstalls(Path.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat"), association))
            {
                yield return (launcher, "Epic Games Launcher のインストールの記録");
            }

            yield break;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string config = OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "Epic")
            : Path.Combine(home, ".config", "Epic");

        foreach (string installed in InstallIni(Path.Combine(config, "UnrealEngine", "Install.ini"), association))
        {
            yield return (installed, "Install.ini の登録");
        }

        if (OperatingSystem.IsMacOS())
        {
            foreach (string launcher in LauncherInstalls(Path.Combine(config, "UnrealEngineLauncher", "LauncherInstalled.dat"), association))
            {
                yield return (launcher, "Epic Games Launcher のインストールの記録");
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadRegistry(RegistryKey hive, string path, string name)
    {
        try
        {
            using RegistryKey? key = hive.OpenSubKey(path);
            return key?.GetValue(name) as string is { Length: > 0 } value ? value : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>LauncherInstalled.dat から、<c>UE_&lt;バージョン&gt;</c> のインストール先を読む。</summary>
    private static IEnumerable<string> LauncherInstalls(string path, string association)
    {
        List<string> found = [];

        try
        {
            if (!File.Exists(path))
            {
                return found;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("InstallationList", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.TryGetProperty("AppName", out JsonElement app) && app.GetString() == "UE_" + association
                        && item.TryGetProperty("InstallLocation", out JsonElement location) && location.GetString() is { Length: > 0 } root)
                    {
                        found.Add(root);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // 読めなければ、その記録はないものとする。
        }

        return found;
    }

    /// <summary>Install.ini の [Installations] から、識別子 (GUID かバージョン) のエンジンのルートを読む。</summary>
    private static IEnumerable<string> InstallIni(string path, string association)
    {
        List<string> found = [];

        try
        {
            if (!File.Exists(path))
            {
                return found;
            }

            bool inSection = false;
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inSection = line.Equals("[Installations]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                int equals = line.IndexOf('=', StringComparison.Ordinal);
                if (inSection && equals > 0 && line[..equals].Trim().Equals(association, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(line[(equals + 1)..].Trim());
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 読めなければ、その記録はないものとする。
        }

        return found;
    }
}
