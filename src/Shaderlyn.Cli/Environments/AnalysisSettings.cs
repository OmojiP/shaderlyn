using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Configuration;

namespace Shaderlyn.Cli.Environments;

/// <summary>
/// 解析設定。同じ設定ファイルと同じ環境で解析するファイルを、その設定と一緒に持つ。
/// </summary>
/// <param name="Options">この解析設定のコマンドラインの指定 (設定ファイルと環境を決めた写し)。</param>
/// <param name="Settings">この解析設定で使う設定 (設定ファイルとコマンドラインの指定を合わせたもの)。</param>
/// <param name="Environment">この解析設定の環境。</param>
/// <param name="Files">解析するファイル。</param>
internal sealed record AnalysisSetting(
    CommandLineOptions Options,
    EffectiveSettings Settings,
    IAnalysisEnvironment Environment,
    ImmutableArray<string> Files);

/// <summary>
/// 解析するファイルを、解析設定 (設定ファイルと環境の組。<see cref="AnalysisSetting"/>) ごとに分ける。
/// </summary>
/// <remarks>
/// <para>
/// <b>ファイルごとに、一番近い設定ファイルを使う</b> (言語サーバーと同じ決まり)。
/// <c>--config</c> を指定すれば、すべてのファイルにその設定ファイルを使う。<c>--no-config</c> なら使わない。
/// </para>
/// <para>
/// <b>環境は設定ファイル (かコマンドライン) の指定に従う。</b> ただし、その環境が集める拡張子でないファイルのうち、
/// HLSL 以外の言語 (GLSL・Godot・Slang・WGSL) の拡張子のものは、その言語の環境で読む。
/// Unity のプロジェクトにある <c>.wgsl</c> は WGSL として読み、<c>.hlsl</c> (Unity では取り込まれる側) は今までどおり単体では読まない。
/// </para>
/// </remarks>
internal static class AnalysisSettings
{
    /// <summary>
    /// ほかの用途と重なる拡張子。拡張子だけでは言語を決めず、その言語の環境を指定したときだけ読む。
    /// </summary>
    /// <remarks>
    /// <c>.mesh</c> は GLSL のメッシュシェーダーの拡張子だが、Unity のメッシュのアセット (中身は YAML) と同じである。
    /// Unity の組み込みのパッケージにもあり、GLSL として読むと構文エラーを並べてしまう。
    /// <see cref="LanguageByExtension"/> より前に置く (静的なフィールドは書いた順に作られる)。
    /// </remarks>
    private static readonly FrozenSet<string> AmbiguousExtensions =
        new[] { ".mesh" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>拡張子から、HLSL 以外の言語の環境の名前への対応。</summary>
    private static readonly FrozenDictionary<string, string> LanguageByExtension =
        ((SyntaxOnlyAnalysisEnvironment[])
        [
            SyntaxOnlyAnalysisEnvironment.CreateGlsl(),
            SyntaxOnlyAnalysisEnvironment.CreateGodot(),
            SyntaxOnlyAnalysisEnvironment.CreateSlang(),
            SyntaxOnlyAnalysisEnvironment.CreateWgsl(),
        ])
        .SelectMany(e => e.DefaultExtensions.Select(x => (Extension: x, e.Name)))
        .Where(p => !AmbiguousExtensions.Contains(p.Extension))
        .ToFrozenDictionary(p => p.Extension, p => p.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>拡張子から、その言語の環境の名前を引く (HLSL 以外の言語だけ)。</summary>
    /// <param name="extension">拡張子 (<c>.vert</c> など)。</param>
    /// <returns>環境の名前。HLSL の拡張子や、ほかの用途と重なる拡張子 (<c>.mesh</c>) なら <see langword="null"/>。</returns>
    /// <remarks>VS Code 拡張も、CLI と同じくこれで GLSL などのファイルの環境を決める。</remarks>
    public static string? LanguageFor(string extension)
        => LanguageByExtension.TryGetValue(extension, out string? language) ? language : null;

    /// <summary>フォルダーから集める拡張子。どの設定ファイルがどの環境を指していても取りこぼさないよう、すべての環境の拡張子を集める。</summary>
    /// <param name="hosts">足した環境。</param>
    /// <returns>拡張子。</returns>
    public static ImmutableArray<string> DiscoveryExtensions(ImmutableArray<HostEnvironment> hosts)
        =>
        [
            .. new[] { ".shader", ".compute", ".hlsl", ".usf" }
                .Concat(LanguageByExtension.Keys)
                .Concat(AmbiguousExtensions)
                .Concat(hosts.IsDefault ? [] : hosts.SelectMany(h => h.DefaultExtensions))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

    /// <summary>ファイルを解析設定に分ける。</summary>
    /// <param name="original">設定を作る前のコマンドラインの指定 (写しの元)。</param>
    /// <param name="primary">最初の解析対象から探した設定ファイルで作った解析設定 (今までの 1 つの設定と環境)。<see cref="AnalysisSetting.Files"/> は使わない。</param>
    /// <param name="hosts">足した環境。</param>
    /// <param name="files">集めたファイル。</param>
    /// <param name="explicitFiles">コマンドラインで直接指定したファイル。拡張子に関わらず、設定の環境で読む。</param>
    /// <param name="unusableConfigurations">
    /// 環境を作れなかった設定ファイル (知らない環境の名前を書いたものなど) の問題。その設定ファイルの下の HLSL のファイルは解析しないので、理由を伝えるために返す。
    /// </param>
    /// <returns>解析設定。最初の解析設定の環境のものが先頭に来る。</returns>
    public static ImmutableArray<AnalysisSetting> Build(
        CommandLineOptions original,
        AnalysisSetting primary,
        ImmutableArray<HostEnvironment> hosts,
        ImmutableArray<string> files,
        IReadOnlySet<string> explicitFiles,
        out ImmutableArray<Core.Diagnostics.Diagnostic> unusableConfigurations)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(primary);

        List<Core.Diagnostics.Diagnostic> problems = [];
        string primaryConfig = FullPathOrEmpty(primary.Settings.Configuration.FilePath);

        // 設定ファイルごとの、設定の指定する環境。最初の解析設定の設定ファイルは、作ったものをそのまま使う。
        Dictionary<string, AnalysisSetting?> byConfig = new(StringComparer.OrdinalIgnoreCase) { [primaryConfig] = primary };
        Dictionary<(string Config, string Environment), List<string>> assigned = [];
        Dictionary<string, string?> configurationByDirectory = new(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            string config = FullPathOrEmpty(ConfigurationFor(original, primary, file, configurationByDirectory));

            if (!byConfig.TryGetValue(config, out AnalysisSetting? configured))
            {
                configured = byConfig[config] = Create(original.For(config.Length == 0 ? null : config, environment: null), hosts, problems);
            }

            string extension = Path.GetExtension(file);
            string? environment =
                configured is not null && (configured.Environment.DefaultExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) || explicitFiles.Contains(file))
                    ? configured.Environment.Name
                    : LanguageByExtension.TryGetValue(extension, out string? language) ? language : null;

            // どの環境も読まない拡張子 (Unity のプロジェクトの .hlsl など)。今までどおり単体では解析しない。
            if (environment is null)
            {
                continue;
            }

            if (!assigned.TryGetValue((config, environment), out List<string>? list))
            {
                assigned[(config, environment)] = list = [];
            }

            list.Add(file);
        }

        ImmutableArray<AnalysisSetting>.Builder groups = ImmutableArray.CreateBuilder<AnalysisSetting>();

        foreach (((string config, string environment), List<string> list) in assigned
                     .OrderBy(p => p.Key.Config == primaryConfig && p.Key.Environment == primary.Environment.Name ? 0 : 1)
                     .ThenBy(p => p.Key.Config, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(p => p.Key.Environment, StringComparer.Ordinal))
        {
            AnalysisSetting? group = byConfig[config] is { } configured && configured.Environment.Name == environment
                ? configured
                : Create(original.For(config.Length == 0 ? null : config, environment), hosts, problems);

            if (group is not null)
            {
                groups.Add(group with { Files = [.. list] });
            }
        }

        unusableConfigurations = [.. problems];
        return groups.ToImmutable();
    }

    /// <summary>設定ファイルのパスを、比べられる形 (絶対パス) にする。</summary>
    /// <param name="path">パス。</param>
    /// <returns>絶対パス。無ければ空文字列。</returns>
    private static string FullPathOrEmpty(string? path) => path is null ? string.Empty : Path.GetFullPath(path);

    /// <summary>そのファイルに使う設定ファイルを決める。</summary>
    /// <param name="original">コマンドラインの指定。</param>
    /// <param name="primary">最初の解析設定。</param>
    /// <param name="file">ファイル。</param>
    /// <param name="byDirectory">フォルダーごとに見つけた設定ファイル (覚えておく先)。同じフォルダーのファイルで上へ辿り直さない。</param>
    /// <returns>設定ファイルのパス。使わないなら <see langword="null"/>。</returns>
    private static string? ConfigurationFor(CommandLineOptions original, AnalysisSetting primary, string file, Dictionary<string, string?> byDirectory)
    {
        if (original.NoConfig || original.ConfigPath is not null)
        {
            return primary.Settings.Configuration.FilePath;
        }

        if (Path.GetDirectoryName(file) is not { } directory)
        {
            return null;
        }

        if (!byDirectory.TryGetValue(directory, out string? found))
        {
            byDirectory[directory] = found = ConfigurationLoader.FindConfigurationFile(directory);
        }

        return found;
    }

    /// <summary>指定から設定と環境を作る。</summary>
    /// <param name="options">設定ファイルと環境を決めた指定。</param>
    /// <param name="hosts">足した環境。</param>
    /// <param name="problems">環境を作れなかったとき、その設定ファイルの問題を足す先。</param>
    /// <returns>解析設定 (ファイルは空)。環境を作れなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// 環境を作れないのは、設定ファイルに知らない環境の名前を書いたときである。
    /// 設定の問題 (<see cref="EffectiveSettings.ConfigurationDiagnostics"/>) に、書いた行への報告が入っている。
    /// 黙って捨てると、その設定ファイルの下のファイルが理由も無く解析されなくなる。
    /// </remarks>
    private static AnalysisSetting? Create(CommandLineOptions options, ImmutableArray<HostEnvironment> hosts, List<Core.Diagnostics.Diagnostic> problems)
    {
        EffectiveSettings settings = EffectiveSettings.Create(options, hosts);

        if (AnalysisEnvironments.TryCreate(settings, out IAnalysisEnvironment? environment))
        {
            return new AnalysisSetting(options, settings, environment, []);
        }

        problems.AddRange(settings.ConfigurationDiagnostics);
        return null;
    }
}
