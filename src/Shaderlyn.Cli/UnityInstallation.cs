using System.Collections.Immutable;

namespace Shaderlyn.Cli;

/// <summary>
/// Unity Editor のインストール先を探す。
/// </summary>
/// <remarks>
/// <para>
/// <c>--unity-editor</c> を省略できるようにするための仕組みである。
/// プロジェクトのパスさえ渡せば動くようにしておかないと、
/// 「オプションを 1 つ書き忘れたせいで検査が、何も伝えられないまま素通りしていた」という事故が起きる。
/// </para>
/// <para>
/// <b>見つからないこと自体は、ここでは失敗にしない。</b>
/// Editor が無くても、プロジェクトの <c>Library/PackageCache</c> から引ければ解析は成立する。
/// どちらからも引けず <c>#include</c> を 1 つも解決できなかった場合に、
/// <c>TOOL0004</c> のエラーとして報告される (docs/rules/TOOL0004.md)。
/// </para>
/// </remarks>
internal static class UnityInstallation
{
    /// <summary>
    /// Editor のデータフォルダーを決め、どう決めたかも返す。
    /// </summary>
    /// <param name="explicitPath"><c>--unity-editor</c> の指定。無ければ <see langword="null"/>。</param>
    /// <param name="projectPath"><c>--unity-project</c> の指定。無ければ <see langword="null"/>。</param>
    /// <returns>決めた結果。</returns>
    /// <remarks>
    /// 解析が使う道 (<see cref="SemanticsSetup.ResolveEditorDataPath"/>) もこれを通る。
    /// 環境の表示 (<c>--env</c>) が別の手順で決めると、表示と実際が食い違う。
    /// </remarks>
    public static EditorDataLookup Lookup(string? explicitPath, string? projectPath)
    {
        ImmutableArray<string> roots = GetHubRoots();
        ImmutableArray<string> installed = ListInstalledVersions(roots);

        if (explicitPath is not null)
        {
            return new EditorDataLookup(explicitPath, EditorDataSource.Explicit, null, roots, installed);
        }

        string? version = projectPath is null ? null : ReadProjectEditorVersion(projectPath);

        if (version is not null)
        {
            foreach (string root in roots)
            {
                if (TryGetDataDirectory(Path.Combine(root, version)) is { } exact)
                {
                    return new EditorDataLookup(exact, EditorDataSource.ProjectVersion, version, roots, installed);
                }
            }
        }

        return FindNewestInstalledEditorData(roots) is { } newest
            ? new EditorDataLookup(
                newest,
                projectPath is null ? EditorDataSource.NewestInstalled : EditorDataSource.NewestInstalledInsteadOfProject,
                version,
                roots,
                installed)
            : new EditorDataLookup(null, EditorDataSource.NotFound, version, roots, installed);
    }

    /// <summary>インストール済みの Editor のバージョンを、新しい順に並べる。</summary>
    /// <param name="roots">探索するルート。</param>
    /// <returns>データフォルダーを持つもののバージョン名。</returns>
    private static ImmutableArray<string> ListInstalledVersions(ImmutableArray<string> roots)
    {
        List<string> versions = [];

        foreach (string root in roots)
        {
            try
            {
                versions.AddRange(Directory.EnumerateDirectories(root)
                    .Where(d => TryGetDataDirectory(d) is not null)
                    .Select(Path.GetFileName)
                    .OfType<string>());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 読めないルートは飛ばす。
            }
        }

        return [.. versions.OrderDescending(StringComparer.Ordinal)];
    }

    /// <summary>Editor のデータフォルダーであることを確かめるための目印。</summary>
    /// <remarks>
    /// <c>CGIncludes</c> は Unity のシェーダーヘッダが置かれるフォルダーで、
    /// Editor のデータフォルダーにのみ存在する。
    /// </remarks>
    private const string DataDirectoryMarker = "CGIncludes";

    /// <summary>
    /// プロジェクトが使っている Unity Editor のデータフォルダーを探す。
    /// </summary>
    /// <param name="projectPath">Unity プロジェクトのルート。</param>
    /// <returns>見つかったデータフォルダー。見つからない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// プロジェクトが記録しているバージョンを優先して探し、
    /// 見つからなければインストール済みの最も新しいバージョンで代用する。
    /// シェーダーヘッダはマイナーバージョン間でほとんど変わらないため、
    /// 厳密に一致しなくても解析の役には立つ。
    /// </remarks>
    public static string? FindEditorDataForProject(string projectPath)
    {
        ArgumentNullException.ThrowIfNull(projectPath);

        return Lookup(null, projectPath).DataPath;
    }

    /// <summary>
    /// インストール済みの Unity のうち、最も新しいもののデータフォルダーを探す。
    /// </summary>
    /// <returns>見つかったデータフォルダー。見つからない場合は <see langword="null"/>。</returns>
    public static string? FindNewestInstalledEditorData() => FindNewestInstalledEditorData(GetHubRoots());

    /// <summary>
    /// <c>ProjectSettings/ProjectVersion.txt</c> から Editor のバージョンを読む。
    /// </summary>
    /// <param name="projectPath">Unity プロジェクトのルート。</param>
    /// <returns>読み取れたバージョン文字列。読めない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// ファイルの形式は <c>m_EditorVersion: 6000.0.23f1</c> という行を含む YAML である。
    /// YAML パーサを持ち込むほどの内容ではないので、行の前方一致で読む。
    /// </remarks>
    private static string? ReadProjectEditorVersion(string projectPath)
    {
        const string key = "m_EditorVersion:";
        string path = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith(key, StringComparison.Ordinal))
                {
                    string version = line[key.Length..].Trim();
                    return version.Length > 0 ? version : null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Unity Hub がインストール先として使うフォルダーを、プラットフォームごとに列挙する。
    /// </summary>
    /// <returns>存在する探索ルート。</returns>
    private static ImmutableArray<string> GetHubRoots()
    {
        List<string> candidates = [];

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Unity/Hub/Editor");
        }
        else
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Unity", "Hub", "Editor"));
        }

        return [.. candidates.Where(Directory.Exists)];
    }

    /// <summary>指定したルート群から最も新しい Editor のデータフォルダーを探す。</summary>
    /// <param name="roots">探索するルート。</param>
    /// <returns>見つかったデータフォルダー。無ければ <see langword="null"/>。</returns>
    private static string? FindNewestInstalledEditorData(ImmutableArray<string> roots)
    {
        foreach (string root in roots)
        {
            IEnumerable<string> versions;
            try
            {
                // バージョン名は 6000.0.23f1 のような形式で、辞書順の降順が新しい順とおおむね一致する。
                versions = Directory.EnumerateDirectories(root).OrderDescending(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string version in versions)
            {
                if (TryGetDataDirectory(version) is { } data)
                {
                    return data;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Editor のインストールフォルダーからデータフォルダーを求める。
    /// </summary>
    /// <param name="editorRoot">Editor のインストールフォルダー。</param>
    /// <returns>データフォルダー。存在しない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// データフォルダーの位置はプラットフォームで異なる。
    /// Windows と Linux は <c>Editor/Data</c>、macOS は <c>Unity.app/Contents</c> である。
    /// </remarks>
    private static string? TryGetDataDirectory(string editorRoot)
    {
        string[] candidates =
        [
            Path.Combine(editorRoot, "Editor", "Data"),
            Path.Combine(editorRoot, "Unity.app", "Contents"),
        ];

        foreach (string candidate in candidates)
        {
            if (Directory.Exists(Path.Combine(candidate, DataDirectoryMarker)))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>Editor のデータフォルダーをどう決めたか。</summary>
internal enum EditorDataSource
{
    /// <summary><c>--unity-editor</c> で指定された。</summary>
    Explicit,

    /// <summary>プロジェクトの <c>ProjectVersion.txt</c> と同じバージョンが見つかった。</summary>
    ProjectVersion,

    /// <summary>プロジェクトのバージョンが見つからず、インストール済みの最新バージョンで代用した。</summary>
    NewestInstalledInsteadOfProject,

    /// <summary>プロジェクトの指定が無く、インストール済みの最新バージョンを使った。</summary>
    NewestInstalled,

    /// <summary>見つからなかった。</summary>
    NotFound,
}

/// <summary>Editor のデータフォルダーを決めた結果。</summary>
/// <param name="DataPath">データフォルダー。見つからなければ <see langword="null"/>。</param>
/// <param name="Source">どう決めたか。</param>
/// <param name="ProjectVersion">プロジェクトが記録しているバージョン。読めなければ <see langword="null"/>。</param>
/// <param name="HubRoots">探した Unity Hub のインストール先。</param>
/// <param name="InstalledVersions">見つかったインストール済みのバージョン。新しい順。</param>
internal sealed record EditorDataLookup(
    string? DataPath,
    EditorDataSource Source,
    string? ProjectVersion,
    ImmutableArray<string> HubRoots,
    ImmutableArray<string> InstalledVersions);
