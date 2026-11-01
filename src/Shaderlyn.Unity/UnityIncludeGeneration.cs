using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shaderlyn.Unity;

/// <summary>
/// <c>--unity-generate</c> が書き出すパッケージ 1 つ。
/// </summary>
/// <param name="Name">パッケージの名前 (<c>com.unity.render-pipelines.universal</c>)。</param>
/// <param name="Source">出どころ (<c>packages-lock.json</c> の <c>source</c>。<c>registry</c> / <c>git</c> / <c>builtin</c> など)。</param>
/// <param name="SourceDirectory">写す元のフォルダー。</param>
/// <param name="OutputDirectory">書き出すフォルダーの中での置き場所 (<c>Library/PackageCache/com.unity.…@d9aadd996b24</c>)。区切りは <c>/</c>。</param>
/// <param name="License">ライセンスの名前。見分けられなければ <see langword="null"/>。</param>
/// <param name="LicenseNote">ライセンスについての補足 (LICENSE が無いなど)。無ければ <see langword="null"/>。</param>
/// <param name="Files">写すファイル (元のフォルダーからの相対。区切りは <c>/</c>)。</param>
/// <param name="Bytes">写すファイルの大きさの合計。</param>
internal sealed record UnityGeneratedPackage(
    string Name,
    string Source,
    string SourceDirectory,
    string OutputDirectory,
    string? License,
    string? LicenseNote,
    ImmutableArray<string> Files,
    long Bytes);

/// <summary>
/// <c>--unity-generate</c> で書き出すものの一覧。書き出す前に利用者に見せて確かめる。
/// </summary>
/// <param name="Packages">書き出すパッケージ。シェーダーのファイルを持たないものは含まない。</param>
/// <param name="CgIncludesDirectory">Unity Editor のヘッダのフォルダー。Editor が見つからなければ <see langword="null"/>。</param>
/// <param name="CgIncludesFiles">Editor のヘッダのフォルダーのファイル (相対)。</param>
/// <param name="MissingPackages"><c>packages-lock.json</c> にあるが、実体が見つからなかったパッケージ (名前と出どころ)。</param>
/// <param name="ProjectEditorVersion">プロジェクトの <c>ProjectVersion.txt</c> のバージョン。読めなければ <see langword="null"/>。</param>
/// <param name="LockHash"><c>packages-lock.json</c> のハッシュ。無ければ <see langword="null"/>。</param>
internal sealed record UnityGenerationPlan(
    ImmutableArray<UnityGeneratedPackage> Packages,
    string? CgIncludesDirectory,
    ImmutableArray<string> CgIncludesFiles,
    ImmutableArray<(string Name, string Source)> MissingPackages,
    string? ProjectEditorVersion,
    string? LockHash)
{
    /// <summary>書き出すファイルの数。</summary>
    public int FileCount => Packages.Sum(p => p.Files.Length) + CgIncludesFiles.Length;

    /// <summary>書き出すファイルの大きさの合計。</summary>
    public long Bytes => Packages.Sum(p => p.Bytes) + CgIncludesFiles.Sum(f => new FileInfo(Path.Combine(CgIncludesDirectory!, f)).Length);
}

/// <summary>
/// Unity のプロジェクトの <c>Library</c> と Unity Editor から、シェーダーの解析に要るヘッダだけを書き出す (<c>--unity-generate</c>)。
/// </summary>
/// <remarks>
/// <para>
/// <b>GitHub のホストランナーには、プロジェクトの <c>Library</c> も Unity Editor も無い。</b>
/// パッケージのヘッダ (<c>Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl</c> など) と
/// Editor のヘッダ (<c>UnityCG.cginc</c> など) を読めないので、セルフホストランナーでしか解析できなかった。
/// 手元で要るものだけを書き出してリポジトリに入れれば、ホストランナーでも解析できる。
/// </para>
/// <para>
/// 書き出すフォルダーの中は、Editor のデータフォルダーとプロジェクトの <c>Library</c> と同じ形にする
/// (<c>Editor/Data/Resources/CGIncludes</c>、<c>Editor/Data/Resources/PackageManager/BuiltInPackages</c>、<c>Library/PackageCache</c>)。
/// 解析では、本物が見つからないときに、そのフォルダーを代わりに渡すだけで今の解決器がそのまま読める。
/// </para>
/// <para>
/// 写すのは、取り込まれうる拡張子のファイル (<see cref="IncludableExtensions"/>) と、<c>package.json</c>、ライセンスの表示 (<c>LICENSE.md</c> と <c>Third Party Notices.md</c>) だけである。
/// Unity のパッケージの多くは Unity Companion License で、写しにライセンスと著作権の表示を添えることを求める (5 項)。
/// <c>Library/PackageCache</c> は URP のプロジェクトで 1.5 GB あるが、写すものは数 MB になる。
/// </para>
/// <para>
/// <b>記録 (<see cref="RecordFileName"/>) にはマシンに依存するパスを書かない。</b> 書き出したものはリポジトリに入れて、ほかのマシンで読む。
/// </para>
/// </remarks>
internal static class UnityIncludeGeneration
{
    /// <summary>記録のファイルの名前。書き出すフォルダーの直下に置く。</summary>
    public const string RecordFileName = "ShaderlynUnityGeneration.tsv";

    /// <summary>記録の形式のバージョン。形式を変えたら上げる。</summary>
    private const string FormatVersion = "1";

    /// <summary>書き出すフォルダーの中の、Editor のデータフォルダーの置き場所。</summary>
    public const string EditorDataDirectory = "Editor/Data";

    /// <summary>書き出すフォルダーの中の、プロジェクトの <c>Library/PackageCache</c> の置き場所。</summary>
    public const string PackageCacheDirectory = "Library/PackageCache";

    /// <summary>書き出すフォルダーの中の、Editor のヘッダの置き場所。</summary>
    private const string CgIncludesOutputDirectory = "Editor/Data/Resources/CGIncludes";

    /// <summary>書き出すフォルダーの中の、Editor の同梱パッケージの置き場所。</summary>
    private const string BuiltInPackagesOutputDirectory = "Editor/Data/Resources/PackageManager/BuiltInPackages";

    /// <summary><c>#include</c> で取り込まれうるファイルの拡張子。</summary>
    private static readonly HashSet<string> IncludableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".hlsl", ".hlsli", ".cginc", ".cg", ".glslinc", ".glsl", ".inc", ".h",
    };

    /// <summary>
    /// 書き出すものを決める。何も書かない。
    /// </summary>
    /// <param name="projectDirectory">Unity のプロジェクトのルート。</param>
    /// <param name="editorDataDirectory">Unity Editor のデータフォルダー。見つからなければ <see langword="null"/>。</param>
    /// <param name="cgIncludesDirectory">Editor のヘッダのフォルダー。見つからなければ <see langword="null"/>。</param>
    /// <returns>書き出すものの一覧。</returns>
    /// <remarks>
    /// <para>
    /// <c>Packages/packages-lock.json</c> があれば、そこに並ぶパッケージを、出どころ (<c>source</c>) ごとの置き場所から写す。
    /// <c>registry</c> と <c>git</c> は <c>Library/PackageCache/&lt;名前&gt;@&lt;ハッシュ&gt;</c>、<c>builtin</c> は Editor の同梱パッケージから。
    /// <c>embedded</c> (プロジェクトの <c>Packages</c> の下) はリポジトリにあるので写さない。
    /// <c>local</c> (<c>file:</c> で参照するもの) は、解析も読まないので写さない。
    /// </para>
    /// <para>
    /// lock が無ければ、<c>Library/PackageCache</c> と同梱パッケージのうち、シェーダーのファイルを持つものを全部写す。
    /// </para>
    /// </remarks>
    public static UnityGenerationPlan Plan(string projectDirectory, string? editorDataDirectory, string? cgIncludesDirectory)
    {
        ArgumentNullException.ThrowIfNull(projectDirectory);

        string packageCache = Path.Combine(projectDirectory, "Library", "PackageCache");
        string? builtInPackages = editorDataDirectory is null
            ? null
            : Path.Combine(editorDataDirectory, "Resources", "PackageManager", "BuiltInPackages");
        string lockPath = Path.Combine(projectDirectory, "Packages", "packages-lock.json");

        List<UnityGeneratedPackage> packages = [];
        List<(string Name, string Source)> missing = [];

        if (ReadLock(lockPath) is { } locked)
        {
            foreach ((string name, string source) in locked)
            {
                string? directory = source switch
                {
                    "registry" or "git" => FindPackageCacheDirectory(packageCache, name),
                    // Unity 6.3 などは同梱パッケージも Library/PackageCache に展開し、解析はそちらを先に読む。あれば同じものを写す。
                    "builtin" => FindPackageCacheDirectory(packageCache, name)
                                 ?? (builtInPackages is not null && Directory.Exists(Path.Combine(builtInPackages, name)) ? Path.Combine(builtInPackages, name) : null),
                    _ => string.Empty,
                };

                // com.unity.modules.* はエンジンの機能で、フォルダーを持たない。
                if (directory is null && !name.StartsWith("com.unity.modules.", StringComparison.Ordinal))
                {
                    missing.Add((name, source));
                }
                else if (directory is { Length: > 0 } && Describe(name, source, directory, OutputDirectoryOf(source, directory)) is { } package)
                {
                    packages.Add(package);
                }
            }
        }
        else
        {
            foreach (string directory in EnumerateDirectories(packageCache))
            {
                string folder = Path.GetFileName(directory);
                int at = folder.IndexOf('@', StringComparison.Ordinal);

                if (Describe(at > 0 ? folder[..at] : folder, "registry", directory, $"{PackageCacheDirectory}/{folder}") is { } package)
                {
                    packages.Add(package);
                }
            }

            foreach (string directory in builtInPackages is null ? [] : EnumerateDirectories(builtInPackages))
            {
                string name = Path.GetFileName(directory);

                if (!packages.Any(p => p.Name == name) && Describe(name, "builtin", directory, $"{BuiltInPackagesOutputDirectory}/{name}") is { } package)
                {
                    packages.Add(package);
                }
            }
        }

        ImmutableArray<string> cgIncludes = cgIncludesDirectory is not null && Directory.Exists(cgIncludesDirectory)
            ? [.. EnumerateFiles(cgIncludesDirectory).Select(f => Relative(cgIncludesDirectory, f)).Order(StringComparer.Ordinal)]
            : [];

        return new UnityGenerationPlan(
            [.. packages.OrderBy(p => p.Name, StringComparer.Ordinal)],
            cgIncludes.IsEmpty ? null : cgIncludesDirectory,
            cgIncludes,
            [.. missing],
            ReadProjectEditorVersion(projectDirectory),
            File.Exists(lockPath) ? HashFile(lockPath) : null);

        // 写す元と同じ置き場所に書き出す (Library/PackageCache から写したものは Library/PackageCache へ)。
        string OutputDirectoryOf(string source, string directory)
            => source == "builtin" && !IsUnder(directory, packageCache)
                ? $"{BuiltInPackagesOutputDirectory}/{Path.GetFileName(directory)}"
                : $"{PackageCacheDirectory}/{Path.GetFileName(directory)}";

        static bool IsUnder(string path, string root)
            => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 書き出す。前に書き出したもの (<see cref="RecordFileName"/> のあるフォルダー) は、消してから書き直す。
    /// </summary>
    /// <param name="plan">書き出すもの (<see cref="Plan"/>)。</param>
    /// <param name="outputDirectory">書き出す先。</param>
    /// <exception cref="InvalidOperationException">書き出す先に、記録の無いファイルがある (ほかのものを消さないため)。</exception>
    public static void Write(UnityGenerationPlan plan, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outputDirectory);

        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            if (!File.Exists(Path.Combine(outputDirectory, RecordFileName)))
            {
                throw new InvalidOperationException(
                    $"{outputDirectory} には、--unity-generate で書き出したものでないファイルがあります。空のフォルダーか、新しいフォルダーを指定してください。");
            }

            // 前に書き出したものを消す。パッケージを上げると古いフォルダー (名前@古いハッシュ) が残るため。
            foreach (string owned in new[] { "Editor", "Library" })
            {
                string path = Path.Combine(outputDirectory, owned);

                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
        }

        Directory.CreateDirectory(outputDirectory);

        foreach (UnityGeneratedPackage package in plan.Packages)
        {
            foreach (string file in package.Files)
            {
                Copy(Path.Combine(package.SourceDirectory, file), Path.Combine(outputDirectory, package.OutputDirectory, file));
            }
        }

        if (plan.CgIncludesDirectory is { } cgIncludes)
        {
            foreach (string file in plan.CgIncludesFiles)
            {
                Copy(Path.Combine(cgIncludes, file), Path.Combine(outputDirectory, CgIncludesOutputDirectory, file));
            }
        }

        File.WriteAllText(Path.Combine(outputDirectory, RecordFileName), CreateRecord(plan));
    }

    /// <summary>
    /// 書き出したものが、今のプロジェクトと合っているかを確かめる。
    /// </summary>
    /// <param name="generatedDirectory">書き出したフォルダー (設定の <c>unity: generated</c>)。</param>
    /// <param name="projectDirectory">Unity のプロジェクトのルート。</param>
    /// <returns>合っていなければ、利用者に伝える文。合っていれば <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 比べるのは、<c>ProjectVersion.txt</c> の Unity のバージョンと、<c>packages-lock.json</c> の中身 (リポジトリにあるので CI でも比べられる)、
    /// それに本物の <c>Library/PackageCache</c> があれば、書き出したパッケージのフォルダーの名前 (<c>名前@ハッシュ</c>)。
    /// </para>
    /// <para>
    /// 手元で Unity かパッケージを上げたのに書き出し直さずにコミットすると、CI だけが古いヘッダで解析する。手元で解析したときに気づけるようにする。
    /// </para>
    /// </remarks>
    public static string? DescribeMismatch(string generatedDirectory, string projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(generatedDirectory);
        ArgumentNullException.ThrowIfNull(projectDirectory);

        string[] lines;

        try
        {
            string path = Path.Combine(generatedDirectory, RecordFileName);
            lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lines = [];
        }

        if (!lines.Contains($"version\t{FormatVersion}"))
        {
            return $"unity: generated のフォルダー ({generatedDirectory}) に、--unity-generate で書き出したものがありません。"
                + "Library や Unity Editor の無いところで解析するなら、--unity-generate で書き出してください。";
        }

        List<string> reasons = [];
        string? recordedVersion = Value("project-version");
        string? currentVersion = ReadProjectEditorVersion(projectDirectory);

        if (recordedVersion != currentVersion)
        {
            reasons.Add($"Unity のバージョンが変わりました (書き出したとき {recordedVersion ?? "不明"}、今 {currentVersion ?? "不明"})。");
        }

        string lockPath = Path.Combine(projectDirectory, "Packages", "packages-lock.json");
        string? currentLock = File.Exists(lockPath) ? HashFile(lockPath) : null;

        if (Value("packages-lock") != currentLock)
        {
            reasons.Add("Packages/packages-lock.json が変わりました。");
        }

        // 本物の Library/PackageCache があれば、書き出したパッケージのフォルダー (名前@ハッシュ) が今もあるかを見る。
        string packageCache = Path.Combine(projectDirectory, "Library", "PackageCache");

        if (Directory.Exists(packageCache))
        {
            string[] changed =
            [
                .. lines
                    .Select(line => line.Split('\t'))
                    .Where(fields => fields.Length >= 4 && fields[0] == "package" && fields[3].StartsWith(PackageCacheDirectory + "/", StringComparison.Ordinal))
                    .Where(fields => !Directory.Exists(Path.Combine(packageCache, fields[3][(PackageCacheDirectory.Length + 1)..])))
                    .Select(fields => fields[1]),
            ];

            if (changed.Length > 0)
            {
                reasons.Add($"Library/PackageCache のパッケージが変わりました ({string.Join(", ", changed)})。");
            }
        }

        return reasons.Count == 0
            ? null
            : $"--unity-generate で書き出したもの ({generatedDirectory}) が、プロジェクトと合っていません。{string.Join(string.Empty, reasons)}"
              + "--unity-generate で書き出し直してください。";

        string? Value(string key)
            => lines.Select(line => line.Split('\t')).FirstOrDefault(fields => fields.Length == 2 && fields[0] == key)?[1];
    }

    /// <summary>記録の中身を作る。</summary>
    private static string CreateRecord(UnityGenerationPlan plan)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Shaderlyn が --unity-generate のときに書き出したものの記録。解析を始めるときに、プロジェクトと合っているかを確かめる。");
        builder.Append("version\t").AppendLine(FormatVersion);

        if (plan.ProjectEditorVersion is { } editorVersion)
        {
            builder.Append("project-version\t").AppendLine(editorVersion);
        }

        if (plan.LockHash is { } lockHash)
        {
            builder.Append("packages-lock\t").AppendLine(lockHash);
        }

        if (plan.CgIncludesDirectory is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $"cgincludes\t{plan.CgIncludesFiles.Length}").AppendLine();
        }

        foreach (UnityGeneratedPackage package in plan.Packages)
        {
            builder.Append(CultureInfo.InvariantCulture, $"package\t{package.Name}\t{package.Source}\t{package.OutputDirectory}\t{package.License ?? "?"}\t{package.Files.Length}").AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>パッケージ 1 つの、写すファイルとライセンスを調べる。シェーダーのファイルが無ければ <see langword="null"/>。</summary>
    private static UnityGeneratedPackage? Describe(string name, string source, string directory, string outputDirectory)
    {
        List<string> files = [];
        long bytes = 0;

        foreach (string file in EnumerateFiles(directory))
        {
            if (IncludableExtensions.Contains(Path.GetExtension(file)) && !IsHiddenFromUnity(Relative(directory, file)))
            {
                files.Add(Relative(directory, file));
                bytes += new FileInfo(file).Length;
            }
        }

        if (files.Count == 0)
        {
            return null;
        }

        // ライセンスの表示と package.json は、パッケージの直下のものを添える。
        foreach (string file in EnumerateTopFiles(directory))
        {
            string fileName = Path.GetFileName(file);

            if (fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) || IsNotice(fileName))
            {
                files.Add(fileName);
                bytes += new FileInfo(file).Length;
            }
        }

        (string? license, string? note) = DetectLicense(directory);
        return new UnityGeneratedPackage(name, source, directory, outputDirectory, license, note, [.. files.Order(StringComparer.Ordinal)], bytes);
    }

    /// <summary>
    /// Unity がアセットとして扱わないフォルダーの下か。名前が <c>~</c> で終わるか <c>.</c> で始まるフォルダー
    /// (<c>Documentation~</c>、C++ のソースを置いた <c>COMIntegration~</c> など)。
    /// </summary>
    private static bool IsHiddenFromUnity(string relativePath)
        => relativePath.Split('/')[..^1].Any(folder => folder.EndsWith('~') || folder.StartsWith('.'));

    /// <summary>ライセンスの表示のファイルか (<c>LICENSE.md</c> / <c>LICENSE</c> / <c>LICENSE.txt</c> / <c>Third Party Notices.md</c>)。</summary>
    private static bool IsNotice(string fileName)
        => !fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
           && (Path.GetFileNameWithoutExtension(fileName).Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
               || fileName.StartsWith("Third Party Notices", StringComparison.OrdinalIgnoreCase)
               || fileName.StartsWith("Third-Party Notices", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// パッケージのライセンスを見分ける。LICENSE の中身を見て、見分けられなければ <c>package.json</c> の <c>license</c> を使う。
    /// </summary>
    /// <returns>ライセンスの名前と補足。見分けられなければ名前は <see langword="null"/>。</returns>
    private static (string? License, string? Note) DetectLicense(string directory)
    {
        string? licenseFile = EnumerateTopFiles(directory)
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals("LICENSE", StringComparison.OrdinalIgnoreCase));

        if (licenseFile is not null && Classify(ReadText(licenseFile)) is { } known)
        {
            return (known, null);
        }

        if (ReadPackageLicense(Path.Combine(directory, "package.json")) is { } declared)
        {
            return (declared, null);
        }

        return (null, licenseFile is null ? "LICENSE が無い" : "LICENSE を見分けられない");

        static string? Classify(string? text)
            => text is null ? null
                : text.Contains("Unity Companion License", StringComparison.OrdinalIgnoreCase) ? "Unity Companion License"
                : text.Contains("Unity Package Distribution License", StringComparison.OrdinalIgnoreCase) ? "Unity Package Distribution License"
                : text.Contains("Apache License", StringComparison.OrdinalIgnoreCase) ? "Apache-2.0"
                : text.Contains("MIT License", StringComparison.OrdinalIgnoreCase)
                  || text.Contains("Permission is hereby granted, free of charge", StringComparison.OrdinalIgnoreCase) ? "MIT"
                : text.Contains("Redistribution and use in source and binary forms", StringComparison.OrdinalIgnoreCase) ? "BSD"
                : null;
    }

    /// <summary><c>package.json</c> の <c>license</c> を読む。</summary>
    private static string? ReadPackageLicense(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("license", out JsonElement license) && license.ValueKind == JsonValueKind.String
                ? license.GetString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary><c>packages-lock.json</c> の依存の名前と出どころを読む。読めなければ <see langword="null"/>。</summary>
    private static List<(string Name, string Source)>? ReadLock(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            List<(string, string)> packages = [];

            foreach (JsonProperty dependency in dependencies.EnumerateObject())
            {
                string source = dependency.Value.ValueKind == JsonValueKind.Object
                                && dependency.Value.TryGetProperty("source", out JsonElement value)
                                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
                packages.Add((dependency.Name, source));
            }

            return packages;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary><c>Library/PackageCache</c> の中の、そのパッケージのフォルダー (<c>名前@ハッシュ</c>)。いくつかあれば新しいもの。</summary>
    private static string? FindPackageCacheDirectory(string packageCache, string name)
    {
        if (!Directory.Exists(packageCache))
        {
            return null;
        }

        string direct = Path.Combine(packageCache, name);

        return Directory.Exists(direct)
            ? direct
            : Directory.EnumerateDirectories(packageCache, $"{name}@*").OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary><c>ProjectSettings/ProjectVersion.txt</c> の <c>m_EditorVersion</c> を読む。</summary>
    internal static string? ReadProjectEditorVersion(string projectDirectory)
    {
        string? text = ReadText(Path.Combine(projectDirectory, "ProjectSettings", "ProjectVersion.txt"));

        return text?
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))?["m_EditorVersion:".Length..]
            .Trim();
    }

    /// <summary>ファイルの中身のハッシュ (SHA-256 の 16 進)。改行の違い (CRLF と LF) は同じとみなす。</summary>
    internal static string HashFile(string path)
    {
        string text = (ReadText(path) ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static IEnumerable<string> EnumerateDirectories(string directory)
        => Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal) : [];

    private static IEnumerable<string> EnumerateTopFiles(string directory)
        => Directory.Exists(directory) ? Directory.EnumerateFiles(directory) : [];

    private static IEnumerable<string> EnumerateFiles(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            : [];
}
