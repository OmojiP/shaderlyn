using System.Collections.Immutable;
using Shaderlyn.Configuration;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Unity.Profiles;
using Shaderlyn.Cli.Environments;

namespace Shaderlyn.Cli;

/// <summary>
/// コマンドライン引数と設定ファイルを突き合わせた、実行時の設定一式。
/// </summary>
/// <remarks>
/// <b>コマンドラインの指定が設定ファイルより優先される。</b>
/// 設定ファイルはプロジェクトの既定を書く場所であり、
/// コマンドラインはその場限りの上書きだからである。
/// 逆にすると、一時的に別の設定で試すことができなくなる。
/// </remarks>
internal sealed class EffectiveSettings
{
    private EffectiveSettings(
        ShaderlynConfiguration configuration,
        string environment,
        string profile,
        ImmutableArray<string> includePaths,
        ImmutableArray<string> defines,
        AnalyzerOptions analyzerOptions,
        ImmutableArray<Diagnostic> configurationDiagnostics)
    {
        Configuration = configuration;
        Environment = environment;
        Profile = profile;
        IncludePaths = includePaths;
        Defines = defines;
        AnalyzerOptions = analyzerOptions;
        ConfigurationDiagnostics = configurationDiagnostics;
    }

    /// <summary>読み込んだ設定ファイルの内容。</summary>
    public ShaderlynConfiguration Configuration { get; }

    /// <summary>
    /// 設定ファイルを探し始めたフォルダー。探さなかった場合 (<c>--config</c> / <c>--no-config</c>) は <see langword="null"/>。
    /// </summary>
    public string? ConfigurationSearchDirectory { get; private init; }

    /// <summary>
    /// コマンドラインで指定した、Unity の環境だけのオプション (<c>--profile</c> など)。
    /// </summary>
    /// <remarks>Unity 以外の環境で指定していればエラーにする。黙って無視すると、効いていると思い込ませる。</remarks>
    public ImmutableArray<string> UnityOnlyCommandLineOptions { get; private init; } = [];

    /// <summary>
    /// Unity プロジェクトのルート (<c>--unity-project</c>、無ければ設定ファイルの <c>unity: project</c>)。Unity 以外の環境では <see langword="null"/>。
    /// </summary>
    public string? UnityProjectPath { get; private init; }

    /// <summary>自前のホストが足した環境。</summary>
    public ImmutableArray<HostEnvironment> HostEnvironments { get; private init; } = [];

    /// <summary>環境を指定しない場合に使う環境。</summary>
    public const string DefaultEnvironment = "unity";

    /// <summary>解析する環境の名前 (設定ファイルの <c>environment</c>、無ければ <see cref="DefaultEnvironment"/>)。</summary>
    public string Environment { get; }

    /// <summary>適用するレンダーパイプラインプロファイルの名前。</summary>
    public string Profile { get; }

    /// <summary><c>#include</c> の探索パス。</summary>
    public ImmutableArray<string> IncludePaths { get; }

    /// <summary>定義済みとするマクロ。</summary>
    public ImmutableArray<string> Defines { get; }

    /// <summary>解析の実行時設定。</summary>
    public AnalyzerOptions AnalyzerOptions { get; }

    /// <summary>
    /// 設定ファイルの問題を表す診断。
    /// </summary>
    /// <remarks>
    /// 通常の指摘と同じ形で出力へ混ぜる。
    /// 設定の誤りは、解析結果と同じ場所に同じ形式で出てはじめて気づかれる。
    /// </remarks>
    public ImmutableArray<Diagnostic> ConfigurationDiagnostics { get; }

    /// <summary>
    /// コマンドライン引数から実行時の設定を組み立てる。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <returns>組み立てた設定。</returns>
    public static EffectiveSettings Create(CommandLineOptions options) => Create(options, []);

    /// <summary>
    /// コマンドライン引数と、自前のホストが足した環境から実行時の設定を組み立てる。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="hostEnvironments">自前のホストが足した環境。</param>
    /// <returns>組み立てた設定。</returns>
    public static EffectiveSettings Create(CommandLineOptions options, ImmutableArray<HostEnvironment> hostEnvironments)
    {
        ArgumentNullException.ThrowIfNull(options);

        hostEnvironments = hostEnvironments.IsDefault ? [] : hostEnvironments;

        ShaderlynConfiguration configuration = LoadConfiguration(options, out string? searchDirectory);
        string environment = options.Environment ?? configuration.Environment ?? DefaultEnvironment;
        bool isUnity = environment == UnityAnalysisEnvironment.EnvironmentName;

        // Unity だけの指定は、コマンドラインに書かれたものを覚えておく。他の環境で指定していればエラーにする (Program)。
        // 設定ファイルの unity: の節は、他の環境では読まない。1 つの設定を環境の切り替えで使い回せるように。
        ImmutableArray<string> unityOnlyOptions =
        [
            .. options.Profile is null ? [] : new[] { "--profile" },
            .. options.UnityProjectPath is null ? [] : new[] { "--unity-project" },
            .. options.UnityEditorDataPath is null ? [] : new[] { "--unity-editor" },
        ];

        if (isUnity)
        {
            options.UseConfigurationDefaults(configuration.UnityProjectPath, configuration.UnityEditorPath);
        }
        else
        {
            options.ClearUnityOptions();
        }

        if (isUnity &&(!configuration.Symbols.IsEmpty || !configuration.SymbolGroups.IsEmpty || !configuration.Units.IsEmpty))
        {
            configuration = configuration with
            {
                Problems =
                [
                    .. configuration.Problems,
                    new ConfigurationProblem(
                        0,
                        "symbols: と units: は Unity の環境では使いません。Unity の構成の軸は #pragma multi_compile などから、"
                        + "コンパイル単位は ShaderLab から読みます。ほかの環境で使うなら environment を指定してください。"),
                ],
            };
        }

        // 環境の名前を知っているのは CLI である。設定ファイルに書かれた知らない名前は、書いた行に報告する。
        // コマンドラインの指定はその場でエラーにする (Program)。
        if (options.Environment is null
            && configuration.Environment is { } environmentName
            && !AnalysisEnvironments.Names(hostEnvironments).Contains(environmentName))
        {
            configuration = configuration with
            {
                Problems =
                [
                    .. configuration.Problems,
                    new ConfigurationProblem(configuration.EnvironmentLine, AnalysisEnvironments.DescribeUnknown(environmentName, hostEnvironments)),
                ],
            };
        }

        // 探索パスと定義は積み上げる。設定ファイルの内容を先に置くことで、
        // 同じマクロを両方で定義した場合にコマンドラインの指定が勝つ。
        ImmutableArray<string> includePaths = [.. configuration.IncludePaths, .. options.IncludePaths];
        ImmutableArray<string> defines = [.. configuration.Defines, .. options.Defines];

        AnalyzerOptions analyzerOptions =
            new(configuration.RuleSeverities, configuration.RuleOptions, configuration.RuleSeverityPatterns);

        return new EffectiveSettings(
            configuration,
            environment,
            options.Profile ?? configuration.Profile ?? RenderPipelineProfiles.Default.Name,
            includePaths,
            defines,
            analyzerOptions,
            CreateConfigurationDiagnostics(configuration))
        {
            ConfigurationSearchDirectory = searchDirectory,
            UnityOnlyCommandLineOptions = unityOnlyOptions,
            HostEnvironments = hostEnvironments,
            UnityProjectPath = isUnity ? options.UnityProjectPath : null,
        };
    }

    /// <summary>
    /// 出力に出す指摘の最低の重要度を決める。
    /// </summary>
    /// <param name="format">出力形式。</param>
    /// <param name="commandLine">コマンドラインの <c>--min-severity</c>。指定が無ければ <see langword="null"/>。</param>
    /// <returns>
    /// コマンドラインの指定、設定ファイルのその形式の値、設定ファイルのすべての形式の値の順に、最初にあるもの。
    /// どれも無ければ <see cref="DiagnosticSeverity.Info"/> (すべて出す)。
    /// </returns>
    public DiagnosticSeverity GetMinimumReportedSeverity(OutputFormat format, DiagnosticSeverity? commandLine)
    {
        if (commandLine is { } severity)
        {
            return severity;
        }

        ImmutableDictionary<string, DiagnosticSeverity> configured = Configuration.ReportMinSeverities;

        return configured.TryGetValue(format.ToString(), out DiagnosticSeverity forFormat) ? forFormat
            : configured.TryGetValue(string.Empty, out DiagnosticSeverity forAll) ? forAll
            : DiagnosticSeverity.Info;
    }

    /// <summary>
    /// ヘッダの中の指摘に、取り込んだ経路をどこまで出すかを決める。
    /// </summary>
    /// <param name="commandLine">コマンドラインの <c>--include-trace</c>。指定が無ければ <see langword="null"/>。</param>
    /// <returns>コマンドラインの指定、設定ファイルの <c>report: include-trace</c> の順に、最初にあるもの。どちらも無ければ最初の 1 本。</returns>
    public Reporting.IncludeTraceMode GetIncludeTrace(Reporting.IncludeTraceMode? commandLine)
        => commandLine
           ?? Reporting.IncludeTraceModes.TryParse(Configuration.ReportIncludeTrace)
           ?? Reporting.IncludeTraceMode.First;

    /// <summary>
    /// フォルダーから上へ辿って、設定ファイルを探す。
    /// </summary>
    /// <param name="directory">探し始めるフォルダー。</param>
    /// <returns>見つかった設定ファイルのパス。無ければ <see langword="null"/>。</returns>
    /// <remarks>言語サーバーが、開いたファイルごとに使う設定を決めるのに使う。CLI と同じ探し方である。</remarks>
    public static string? FindConfigurationFile(string directory) => ConfigurationLoader.FindConfigurationFile(directory);

    /// <summary>
    /// 設定ファイルを読み込む。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="searchDirectory">探し始めたフォルダー。探さなかった場合は <see langword="null"/>。</param>
    /// <returns>読み込んだ設定。見つからない場合は空の設定。</returns>
    /// <remarks>
    /// 明示的な指定が無い場合は、最初の解析対象から上のフォルダーへ辿って探す。
    /// リポジトリのルートに置いておけば、どのフォルダーから実行しても同じ設定が使われる。
    /// </remarks>
    private static ShaderlynConfiguration LoadConfiguration(CommandLineOptions options, out string? searchDirectory)
    {
        searchDirectory = null;

        if (options.NoConfig)
        {
            return ShaderlynConfiguration.Empty;
        }

        if (options.ConfigPath is { } explicitPath)
        {
            return ConfigurationLoader.Load(explicitPath);
        }

        string startDirectory = options.InputPaths.Length > 0
            ? GetDirectory(options.InputPaths[0])
            : Directory.GetCurrentDirectory();

        searchDirectory = startDirectory;

        return ConfigurationLoader.FindConfigurationFile(startDirectory) is { } found
            ? ConfigurationLoader.Load(found)
            : ShaderlynConfiguration.Empty;
    }

    /// <summary>パスを含むフォルダーを求める。</summary>
    /// <param name="path">対象のパス。</param>
    /// <returns>フォルダーのパス。</returns>
    private static string GetDirectory(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return Directory.GetCurrentDirectory();
        }
    }

    /// <summary>
    /// 設定ファイルの問題を診断へ変換する。
    /// </summary>
    /// <param name="configuration">読み込んだ設定。</param>
    /// <returns>変換した診断。</returns>
    /// <remarks>
    /// 設定ファイル自身の位置を指す。行番号を持たせるために、
    /// 設定ファイルの内容からソーステキストを作り直している。
    /// </remarks>
    private static ImmutableArray<Diagnostic> CreateConfigurationDiagnostics(
        ShaderlynConfiguration configuration)
    {
        if (configuration.Problems.IsEmpty || configuration.FilePath is not { } filePath)
        {
            return [];
        }

        SourceText text;
        try
        {
            text = SourceText.Load(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = SourceText.From(string.Empty, filePath);
        }

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach (ConfigurationProblem problem in configuration.Problems)
        {
            TextSpan span = problem.Line < text.LineCount ? text.GetLineSpan(problem.Line) : new TextSpan(0, 0);

            diagnostics.Add(Diagnostic.Create(
                WellKnownDescriptors.ConfigurationProblem,
                Location.Create(text, span),
                problem.Message));
        }

        return diagnostics.ToImmutable();
    }
}
