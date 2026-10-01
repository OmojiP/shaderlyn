using System.Collections.Immutable;
using Shaderlyn.Configuration;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

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
        string profile,
        ImmutableArray<string> includePaths,
        ImmutableArray<string> defines,
        AnalyzerOptions analyzerOptions,
        ImmutableArray<Diagnostic> configurationDiagnostics)
    {
        Configuration = configuration;
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
    public static EffectiveSettings Create(CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ShaderlynConfiguration configuration = LoadConfiguration(options, out string? searchDirectory);

        // 探索パスと定義は積み上げる。設定ファイルの内容を先に置くことで、
        // 同じマクロを両方で定義した場合にコマンドラインの指定が勝つ。
        ImmutableArray<string> includePaths = [.. configuration.IncludePaths, .. options.IncludePaths];
        ImmutableArray<string> defines = [.. configuration.Defines, .. options.Defines];

        AnalyzerOptions analyzerOptions =
            new(configuration.RuleSeverities, configuration.RuleOptions);

        return new EffectiveSettings(
            configuration,
            options.Profile ?? configuration.Profile ?? Semantics.Profiles.RenderPipelineProfiles.Default.Name,
            includePaths,
            defines,
            analyzerOptions,
            CreateConfigurationDiagnostics(configuration))
        {
            ConfigurationSearchDirectory = searchDirectory,
        };
    }

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
            text = SourceText.From(File.ReadAllText(filePath), filePath);
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
