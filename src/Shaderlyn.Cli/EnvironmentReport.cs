using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shaderlyn.Configuration;
using Shaderlyn.Hlsl.Preprocessing;

namespace Shaderlyn.Cli;

/// <summary>
/// 解析はせずに、解析が使う環境 (設定ファイル・Unity のインストール先・パッケージの解決先) を書き出す。
/// </summary>
/// <remarks>
/// <para>
/// <b>環境の誤りは、指摘が「出ない」ことでしか現れない。</b>
/// Editor やパッケージの場所を取り違えるとヘッダが読めず、ルールは誤検出を避けるために自ら検査を見送る
/// (<c>SL0002</c>)。設定ファイルを拾えていなければ、書いたはずの重要度が効かない。
/// どちらも解析結果だけを見ていては気づきにくいので、解析の前に確かめられるようにする。
/// </para>
/// <para>
/// <b>解析と同じ手順で決める。</b>
/// ここで独自に探すと、表示と実際の解析が食い違う。Editor の決め方は <see cref="UnityInstallation.Lookup"/>、
/// 探索順は <see cref="SemanticsSetup.GetIncludeSearchPaths"/>、パッケージは解析と同じ解決器に尋ねる。
/// </para>
/// </remarks>
internal static partial class EnvironmentReport
{
    /// <summary>解決できなかった <c>#include</c> を並べる上限。</summary>
    private const int MaxListedUnresolved = 20;

    /// <summary>問題を示す印。</summary>
    private const string Warning = "⚠ ";

    /// <summary>
    /// 環境を書き出した文字列を作る。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    /// <param name="files">見つかった解析対象のファイル。</param>
    /// <param name="missingPaths">存在しなかった解析対象のパス。</param>
    /// <param name="version">このツールのバージョン。</param>
    /// <returns>書き出した文字列。</returns>
    public static string Build(
        CommandLineOptions options,
        EffectiveSettings settings,
        ImmutableArray<string> files,
        ImmutableArray<string> missingPaths,
        string version)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        StringBuilder builder = new();

        Line(builder, $"Shaderlyn {version} の解析環境 (解析はしていません)");

        WriteInputs(builder, options, files, missingPaths);
        WriteConfiguration(builder, options, settings);

        EditorDataLookup editor = UnityInstallation.Lookup(options.UnityEditorDataPath, options.UnityProjectPath);

        WriteProject(builder, options);
        WriteEditor(builder, editor);

        UnityPackageIncludeResolver? packages = SemanticsSetup.CreatePackageResolver(options, editor.DataPath);

        WriteSearchOrder(builder, options, settings, editor.DataPath, packages);

        IIncludeResolver resolver = SemanticsSetup.CreateIncludeResolver(options, settings, editor.DataPath);
        ImmutableArray<IncludeLine> includes = ReadDirectIncludes(files);

        WritePackages(builder, options, packages, includes);
        WriteIncludes(builder, resolver, includes);
        WriteMacros(builder, settings);

        return builder.ToString();
    }

    /// <summary>解析対象を書き出す。</summary>
    /// <param name="builder">書き出し先。</param>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="files">見つかった解析対象のファイル。</param>
    /// <param name="missingPaths">存在しなかった解析対象のパス。</param>
    private static void WriteInputs(
        StringBuilder builder,
        CommandLineOptions options,
        ImmutableArray<string> files,
        ImmutableArray<string> missingPaths)
    {
        Section(builder, "解析対象");

        if (options.InputPaths.IsEmpty)
        {
            Line(builder, "  (指定なし)");
        }

        foreach (string path in options.InputPaths)
        {
            string kind = Directory.Exists(path) ? "フォルダー" : File.Exists(path) ? "ファイル" : "存在しない";
            Line(builder, $"  {(kind == "存在しない" ? Warning : string.Empty)}{FullPath(path)} ({kind})");
        }

        Line(builder, $"  見つかったシェーダー: {files.Length} 件 ({string.Join(" / ", CommandLineOptions.DefaultExtensions)})");

        if (!options.InputPaths.IsEmpty && files.IsEmpty && missingPaths.IsEmpty)
        {
            Line(builder, $"  {Warning}解析するファイルがありません。.hlsl / .cginc はフォルダーからは探しません。");
        }
    }

    /// <summary>設定ファイルと、それを反映した設定を書き出す。</summary>
    /// <param name="builder">書き出し先。</param>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    private static void WriteConfiguration(StringBuilder builder, CommandLineOptions options, EffectiveSettings settings)
    {
        Section(builder, $"設定ファイル ({ShaderlynConfiguration.FileName})");

        ShaderlynConfiguration configuration = settings.Configuration;

        if (options.NoConfig)
        {
            Line(builder, "  --no-config のため読んでいません。");
        }
        else if (options.ConfigPath is { } explicitPath)
        {
            Line(builder, File.Exists(explicitPath)
                ? $"  --config で指定: {FullPath(explicitPath)}"
                : $"  {Warning}--config で指定したファイルがありません: {FullPath(explicitPath)}");
        }
        else
        {
            Line(builder, $"  探し始めた場所: {settings.ConfigurationSearchDirectory} (ここから上のフォルダーへ辿ります)");
            Line(builder, configuration.FilePath is { } found
                ? $"  見つかったファイル: {found}"
                : "  見つかりませんでした。既定の設定で解析します。");
        }

        foreach (ConfigurationProblem problem in configuration.Problems)
        {
            Line(builder, $"  {Warning}{problem.Line + 1} 行目: {problem.Message}");
        }

        string profileSource = options.Profile is not null
            ? "コマンドライン"
            : configuration.Profile is not null ? "設定ファイル" : "既定";

        Line(builder, $"  プロファイル: {settings.Profile} ({profileSource})");

        foreach (string define in configuration.Defines)
        {
            Line(builder, $"  追加のマクロ: {define} (設定ファイル)");
        }

        foreach (string define in options.Defines)
        {
            Line(builder, $"  追加のマクロ: {define} (コマンドライン)");
        }

        if (!configuration.RuleSeverities.IsEmpty)
        {
            Line(builder, $"  重要度を変えたルール: {string.Join(", ", configuration.RuleSeverities.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.ToString().ToLowerInvariant()}"))}");
        }

        if (!configuration.RuleOptions.IsEmpty)
        {
            Line(builder, $"  オプションを設定したルール: {string.Join(", ", configuration.RuleOptions.Keys.Order(StringComparer.Ordinal))}");
        }
    }

    /// <summary>Unity プロジェクトを書き出す。</summary>
    /// <param name="builder">書き出し先。</param>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    private static void WriteProject(StringBuilder builder, CommandLineOptions options)
    {
        Section(builder, "Unity プロジェクト");

        if (options.UnityProjectPath is not { } project)
        {
            Line(builder, "  --unity-project: 指定なし (Editor 同梱のパッケージだけを解決します)");

            // 指定を推定では補わない (解析は明示を求める)。候補があることだけを伝える。
            if (FindProjectAbove(options.InputPaths) is { } candidate)
            {
                Line(builder, $"  {Warning}解析対象の上に Unity プロジェクトがあります: {candidate}");
                Line(builder, $"     --unity-project \"{candidate}\" を指定すると、Library/PackageCache と Packages のパッケージも解決します。");
            }

            return;
        }

        Line(builder, $"  --unity-project: {FullPath(project)}");

        if (!Directory.Exists(project))
        {
            Line(builder, $"  {Warning}フォルダーがありません。");
            return;
        }

        foreach (string part in (string[])["Assets", "Packages", Path.Combine("Library", "PackageCache"), Path.Combine("ProjectSettings", "ProjectVersion.txt")])
        {
            string path = Path.Combine(project, part);
            bool exists = Directory.Exists(path) || File.Exists(path);
            Line(builder, $"  {(exists ? string.Empty : Warning)}{part}: {(exists ? "あり" : "なし")}");
        }

        if (!Directory.Exists(Path.Combine(project, "Library", "PackageCache")))
        {
            Line(builder, "     Library/PackageCache は Unity でプロジェクトを 1 度開くと作られます。無いとパッケージのヘッダを解決できません。");
        }
    }

    /// <summary>Unity Editor を書き出す。</summary>
    /// <param name="builder">書き出し先。</param>
    /// <param name="editor">Editor を決めた結果。</param>
    private static void WriteEditor(StringBuilder builder, EditorDataLookup editor)
    {
        Section(builder, "Unity Editor");

        string source = editor.Source switch
        {
            EditorDataSource.Explicit => "--unity-editor で指定",
            EditorDataSource.ProjectVersion => $"プロジェクトの ProjectVersion.txt と同じバージョン ({editor.ProjectVersion})",
            EditorDataSource.NewestInstalledInsteadOfProject => editor.ProjectVersion is { } wanted
                ? $"{Warning}プロジェクトのバージョン {wanted} が見つからないため、インストール済みの最新バージョンで代用"
                : $"{Warning}プロジェクトのバージョンが読めないため、インストール済みの最新バージョンで代用",
            EditorDataSource.NewestInstalled => "インストール済みの最新バージョン",
            _ => $"{Warning}見つかりませんでした",
        };

        Line(builder, $"  データフォルダー: {editor.DataPath ?? "(なし)"}");
        Line(builder, $"  決め方: {source}");

        if (editor.DataPath is { } data)
        {
            bool hasIncludes = Directory.Exists(Path.Combine(data, "CGIncludes"));
            Line(builder, $"  {(hasIncludes ? string.Empty : Warning)}CGIncludes: {(hasIncludes ? "あり" : "なし (Editor のデータフォルダーではない可能性があります)")}");
        }

        Line(builder, $"  探したインストール先: {(editor.HubRoots.IsEmpty ? "(なし)" : string.Join(", ", editor.HubRoots))}");
        Line(builder, $"  インストール済み: {(editor.InstalledVersions.IsEmpty ? "(なし)" : string.Join(", ", editor.InstalledVersions))}");
    }

    /// <summary><c>#include</c> を探す順序を書き出す。</summary>
    /// <param name="builder">書き出し先。</param>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    /// <param name="editorData">Editor のデータフォルダー。</param>
    /// <param name="packages">パッケージの解決器。</param>
    private static void WriteSearchOrder(
        StringBuilder builder,
        CommandLineOptions options,
        EffectiveSettings settings,
        string? editorData,
        UnityPackageIncludeResolver? packages)
    {
        Section(builder, "#include を探す順序");

        int index = 1;
        Line(builder, $"  {index++}. #include を書いているファイルからの相対");

        foreach (string path in SemanticsSetup.GetIncludeSearchPaths(options, settings, editorData))
        {
            bool exists = Directory.Exists(path);
            Line(builder, $"  {index++}. {(exists ? string.Empty : Warning)}{FullPath(path)}{(exists ? string.Empty : " (ありません)")}");
        }

        Line(builder, packages is null
            ? $"  {index}. {Warning}Packages/<名前>/... は解決できません (--unity-project も Editor もありません)"
            : $"  {index}. Packages/<名前>/... はパッケージの置き場所から: {string.Join(", ", packages.PackageRoots.Select(FullPath))}");
    }

    /// <summary>パッケージの解決先を書き出す。</summary>
    /// <param name="builder">書き出し先。</param>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="packages">パッケージの解決器。</param>
    /// <param name="includes">解析対象に直接書かれた <c>#include</c>。</param>
    /// <remarks>
    /// プロジェクトの <c>Packages/manifest.json</c> の依存と、解析対象が <c>Packages/</c> で参照しているパッケージを並べる。
    /// </remarks>
    private static void WritePackages(
        StringBuilder builder,
        CommandLineOptions options,
        UnityPackageIncludeResolver? packages,
        ImmutableArray<IncludeLine> includes)
    {
        Section(builder, "パッケージの解決先");

        SortedSet<string> referenced = new(
            includes.Select(i => PackageNameOf(i.Path)).OfType<string>(),
            StringComparer.Ordinal);

        SortedSet<string> fromManifest = options.UnityProjectPath is { } project
            ? ReadManifestDependencies(Path.Combine(project, "Packages", "manifest.json"))
            : new SortedSet<string>(StringComparer.Ordinal);

        if (referenced.Count == 0 && fromManifest.Count == 0)
        {
            Line(builder, "  解析対象は Packages/ のヘッダを参照していません。");
            return;
        }

        foreach (string name in referenced.Union(fromManifest).Order(StringComparer.Ordinal))
        {
            string? found = packages?.FindPackage(name);
            string origin = referenced.Contains(name) ? "参照あり" : "manifest.json";

            // 参照しているのに見つからないものだけを問題として示す。manifest.json にはヘッダを持たないパッケージも並ぶ。
            string mark = found is null && referenced.Contains(name) ? Warning : string.Empty;

            Line(builder, found is null
                ? $"  {mark}{name} → 見つかりません ({origin})"
                : $"  {name} → {FullPath(found)} ({DescribePackageRoot(found)}、{origin})");
        }
    }

    /// <summary>パッケージがどの置き場所にあったかを表す。</summary>
    /// <param name="packageDirectory">パッケージのフォルダー。</param>
    /// <returns>置き場所の説明。</returns>
    private static string DescribePackageRoot(string packageDirectory)
    {
        string? root = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(packageDirectory)));

        return root switch
        {
            "PackageCache" => "Library/PackageCache",
            "Packages" => "Packages (埋め込み)",
            "BuiltInPackages" => "Editor 同梱",
            _ => root ?? "?",
        };
    }

    /// <summary>解析対象に直接書かれた <c>#include</c> の解決結果を書き出す。</summary>
    /// <param name="builder">書き出し先。</param>
    /// <param name="resolver">解析と同じ解決器。</param>
    /// <param name="includes">解析対象に直接書かれた <c>#include</c>。</param>
    private static void WriteIncludes(StringBuilder builder, IIncludeResolver resolver, ImmutableArray<IncludeLine> includes)
    {
        Section(builder, "解析対象に書かれた #include (直接書かれたものだけ。#if の条件は見ていません)");

        if (includes.IsEmpty)
        {
            Line(builder, "  ありません。");
            return;
        }

        List<IncludeLine> unresolved = [.. includes.Where(i => !resolver.TryResolve(i.Path, i.FilePath, out _))];

        Line(builder, $"  解決できた: {includes.Length - unresolved.Count} / {includes.Length}");

        foreach (IncludeLine include in unresolved.Take(MaxListedUnresolved))
        {
            Line(builder, $"  {Warning}{include.FilePath}({include.Line}): \"{include.Path}\"");
        }

        if (unresolved.Count > MaxListedUnresolved)
        {
            Line(builder, $"  ほか {unresolved.Count - MaxListedUnresolved} 件");
        }
    }

    /// <summary>展開に使う定義済みマクロを書き出す。</summary>
    /// <param name="builder">書き出し先。</param>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    private static void WriteMacros(StringBuilder builder, EffectiveSettings settings)
    {
        Section(builder, "定義済みマクロ (展開に使う値)");

        ImmutableDictionary<string, string> macros = SemanticsSetup.BuildPredefinedMacros(settings.Defines);

        foreach ((string name, string value) in macros.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Line(builder, $"  {name}={value}");
        }
    }

    /// <summary>解析対象に直接書かれた <c>#include</c> 1 行。</summary>
    /// <param name="FilePath">書かれたファイル。</param>
    /// <param name="Line">行番号 (1 始まり)。</param>
    /// <param name="Path">書かれたパス。</param>
    private readonly record struct IncludeLine(string FilePath, int Line, string Path);

    /// <summary>解析対象に直接書かれた <c>#include</c> を集める。</summary>
    /// <param name="files">解析対象のファイル。</param>
    /// <returns>集めた行。</returns>
    /// <remarks>
    /// 展開はしない。環境が揃っているかを見るためのものなので、条件で外れる行も数える。
    /// </remarks>
    private static ImmutableArray<IncludeLine> ReadDirectIncludes(ImmutableArray<string> files)
    {
        ImmutableArray<IncludeLine>.Builder lines = ImmutableArray.CreateBuilder<IncludeLine>();

        foreach (string file in files)
        {
            string[] content;
            try
            {
                content = File.ReadAllLines(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            for (int i = 0; i < content.Length; i++)
            {
                if (IncludePattern().Match(content[i]) is { Success: true } match)
                {
                    lines.Add(new IncludeLine(Path.GetFullPath(file), i + 1, match.Groups["path"].Value));
                }
            }
        }

        return lines.ToImmutable();
    }

    /// <summary><c>Packages/&lt;名前&gt;/...</c> の形のパスからパッケージ名を取り出す。</summary>
    /// <param name="path">書かれたパス。</param>
    /// <returns>パッケージ名。その形でなければ <see langword="null"/>。</returns>
    private static string? PackageNameOf(string path)
    {
        string normalized = path.Replace('\\', '/');

        if (!normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int end = normalized.IndexOf('/', "Packages/".Length);
        return end > "Packages/".Length ? normalized["Packages/".Length..end] : null;
    }

    /// <summary><c>Packages/manifest.json</c> の依存を読む。</summary>
    /// <param name="manifestPath">ファイルのパス。</param>
    /// <returns>パッケージ名。読めなければ空。</returns>
    private static SortedSet<string> ReadManifestDependencies(string manifestPath)
    {
        SortedSet<string> names = new(StringComparer.Ordinal);

        try
        {
            if (!File.Exists(manifestPath))
            {
                return names;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));

            if (document.RootElement.TryGetProperty("dependencies", out JsonElement dependencies)
                && dependencies.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty dependency in dependencies.EnumerateObject())
                {
                    names.Add(dependency.Name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 読めない manifest.json は、並べないだけにする。解決は解決器が行う。
        }

        return names;
    }

    /// <summary>解析対象の上にある Unity プロジェクトを探す。</summary>
    /// <param name="inputPaths">解析対象のパス。</param>
    /// <returns>見つかったプロジェクトのルート。無ければ <see langword="null"/>。</returns>
    private static string? FindProjectAbove(ImmutableArray<string> inputPaths)
    {
        foreach (string input in inputPaths)
        {
            DirectoryInfo? directory;
            try
            {
                string full = Path.GetFullPath(input);
                directory = new DirectoryInfo(Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                continue;
            }

            for (; directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ProjectSettings", "ProjectVersion.txt"))
                    && Directory.Exists(Path.Combine(directory.FullName, "Assets")))
                {
                    return directory.FullName;
                }
            }
        }

        return null;
    }

    /// <summary>絶対パスにする。できなければそのまま返す。</summary>
    /// <param name="path">対象のパス。</param>
    /// <returns>絶対パス。</returns>
    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>見出しを書く。</summary>
    /// <param name="builder">書き出し先。</param>
    /// <param name="title">見出し。</param>
    private static void Section(StringBuilder builder, string title)
    {
        builder.AppendLine();
        Line(builder, $"■ {title}");
    }

    /// <summary>1 行書く。</summary>
    /// <param name="builder">書き出し先。</param>
    /// <param name="text">内容。</param>
    private static void Line(StringBuilder builder, string text)
        => builder.Append(CultureInfo.InvariantCulture, $"{text}").AppendLine();

    /// <summary><c>#include</c> と <c>#include_with_pragmas</c> の行。</summary>
    /// <returns>正規表現。</returns>
    [GeneratedRegex("""^\s*#\s*include(?:_with_pragmas)?\s*[<"](?<path>[^">]+)[">]""")]
    private static partial Regex IncludePattern();
}
