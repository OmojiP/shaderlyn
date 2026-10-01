using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Shaderlyn.Cli.Baseline;
using Shaderlyn.Cli.Caching;
using Shaderlyn.Cli.Inspection;
using Shaderlyn.Cli.Reporting;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Cli;

/// <summary>
/// CLI の本体。配布する実行ファイル (Shaderlyn.Cli.App) も、自作ルール入りの CLI も、ここを呼ぶ。
/// </summary>
public static class Program
{
    /// <summary>
    /// 解析を実行する。
    /// </summary>
    /// <param name="args">コマンドライン引数。</param>
    /// <param name="analyzers">
    /// 実行するアナライザ。組み込みのものに加え、利用者が自作したアナライザを渡せる。
    /// </param>
    /// <param name="output">通常の出力先。</param>
    /// <param name="errorOutput">エラーメッセージの出力先。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>終了コード。</returns>
    /// <remarks>
    /// <para>
    /// アナライザと出力先を引数で受け取れるようにしているのには 2 つの理由がある。
    /// 1 つはテストが標準出力を奪わずに検証できるようにするため。
    /// もう 1 つは、Native AOT ではプラグイン DLL を動的に読み込めないため、
    /// 自作ルールを持つ利用者が「このメソッドを呼ぶだけの自前 CLI」を作って
    /// AOT publish できるようにするためである (Roslyn Analyzer と同じ考え方)。
    /// </para>
    /// </remarks>
    public static async Task<ExitCode> RunAsync(
        string[] args,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        TextWriter output,
        TextWriter errorOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(errorOutput);

        if (!CommandLineOptions.TryParse(args, out CommandLineOptions? options, out string? parseError))
        {
            await errorOutput.WriteLineAsync(parseError).ConfigureAwait(false);
            return ExitCode.ToolError;
        }

        if (options.ShowHelp)
        {
            await output.WriteLineAsync(CommandLineOptions.GetUsage()).ConfigureAwait(false);
            return ExitCode.Success;
        }

        if (options.ShowVersion)
        {
            await output.WriteLineAsync(GetVersion()).ConfigureAwait(false);
            return ExitCode.Success;
        }

        if (options.ListRules)
        {
            await output.WriteAsync(FormatRuleList(analyzers)).ConfigureAwait(false);
            return ExitCode.Success;
        }

        if (options.BasePath is { } basePath && !Directory.Exists(basePath))
        {
            await errorOutput.WriteLineAsync($"--base-path に指定されたフォルダーが存在しません: {basePath}")
                .ConfigureAwait(false);
            return ExitCode.ToolError;
        }

        ImmutableArray<string> files = ShaderFileDiscovery.Discover(
            options.InputPaths,
            CommandLineOptions.DefaultExtensions,
            out ImmutableArray<string> missingPaths,
            out ImmutableArray<string> unsupportedFiles);

        // 環境の表示は、対象のパスが間違っていてもそれを含めて見せる。止めると何が違うのかが分からない。
        if (options.ShowEnvironment)
        {
            await output.WriteAsync(
                EnvironmentReport.Build(options, EffectiveSettings.Create(options), files, missingPaths, GetVersion()))
                .ConfigureAwait(false);
            return ExitCode.Success;
        }

        if (missingPaths.Length > 0)
        {
            foreach (string missing in missingPaths)
            {
                await errorOutput.WriteLineAsync($"指定されたパスが存在しません: {missing}").ConfigureAwait(false);
            }

            return ExitCode.ToolError;
        }

        await ReportUnsupportedFilesAsync(options, unsupportedFiles, errorOutput).ConfigureAwait(false);

        EffectiveSettings settings = EffectiveSettings.Create(options);

        if (options.InspectOutputPath is { } inspectOutputPath)
        {
            return await WriteInspectionAsync(
                options, settings, analyzers, files, inspectOutputPath, output, errorOutput, cancellationToken)
                .ConfigureAwait(false);
        }

        AnalysisTimings? timings = options.ReportTimings ? new AnalysisTimings() : null;
        SemanticsOptions semanticsOptions = SemanticsSetup.Create(options, settings, timings);
        AnalysisCache? cache = await LoadCacheAsync(options, settings, analyzers, errorOutput).ConfigureAwait(false);

        AnalysisRunner runner = new(
            analyzers, settings.AnalyzerOptions, semanticsOptions, timings, cache);

        AnalysisResult result = await runner.RunAsync(files, cancellationToken).ConfigureAwait(false);

        await SaveCacheAsync(options, cache, errorOutput).ConfigureAwait(false);

        if (timings is not null)
        {
            // 標準エラーへ出す。JSON や SARIF の出力へ混ぜない。
            await errorOutput.WriteAsync(timings.Describe(semanticsOptions.IncludeCache, cache))
                .ConfigureAwait(false);
        }
        result = result.WithAdditionalDiagnostics(settings.ConfigurationDiagnostics);

        if (options.BaselinePath is { } baselinePath)
        {
            if (!TryApplyBaseline(baselinePath, ref result, out string? baselineError))
            {
                await errorOutput.WriteLineAsync(
                    $"ベースラインを読み込めません ({baselinePath}): {baselineError}").ConfigureAwait(false);
                return ExitCode.ToolError;
            }
        }

        if (options.WriteBaselinePath is { } writeBaselinePath)
        {
            await WriteBaselineAsync(writeBaselinePath, result, output, cancellationToken).ConfigureAwait(false);
            return ExitCode.Success;
        }

        string report = FormatReport(options, analyzers, result, output);
        await WriteReportAsync(options, report, output, cancellationToken).ConfigureAwait(false);

        // 注釈は主たる出力とは別に書く。SARIF をファイルへ書きながら
        // PR には即座に注釈を出す、という 2 つの目的を 1 回の解析で果たすため。
        if (options.Annotate && options.Format != OutputFormat.GitHub)
        {
            GitHubDiagnosticReporter annotator =
                new(options.BasePath ?? Directory.GetCurrentDirectory());

            await output.WriteAsync(annotator.Format(result.Diagnostics, result.AnalyzedFileCount))
                .ConfigureAwait(false);
        }

        return result.HasDiagnosticsAtOrAbove(options.ErrorOn) ? ExitCode.DiagnosticsFound : ExitCode.Success;
    }

    /// <summary>
    /// 結果のキャッシュを読み込む。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <param name="errorOutput">エラーメッセージの出力先。</param>
    /// <returns>読み込んだキャッシュ。<c>--cache</c> の指定が無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>読み込みの失敗はツールエラーにしない。</b>
    /// キャッシュは速さのための仕組みでしかなく、
    /// 捨てて全件を解析し直せば正しい結果になる。
    /// ただし何も伝えずに捨てると「なぜか毎回遅い」ことに気づけないので、
    /// 読めなかったことは標準エラーへ伝える。
    /// </remarks>
    private static async Task<AnalysisCache?> LoadCacheAsync(
        CommandLineOptions options,
        EffectiveSettings settings,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        TextWriter errorOutput)
    {
        if (options.CachePath is not { } cachePath)
        {
            return null;
        }

        AnalysisCache cache = AnalysisCache.Load(
            cachePath, AnalysisCacheKey.Compute(options, settings, analyzers), out string? warning);

        if (warning is not null)
        {
            await errorOutput.WriteLineAsync(
                $"キャッシュを読み込めませんでした ({cachePath}): {warning}").ConfigureAwait(false);
        }

        return cache;
    }

    /// <summary>
    /// 結果のキャッシュを書き出す。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="cache">書き出すキャッシュ。</param>
    /// <param name="errorOutput">エラーメッセージの出力先。</param>
    /// <returns>書き出しの完了を表すタスク。</returns>
    /// <remarks>
    /// 書き出せなくても解析結果は正しいので、ツールエラーにはしない。
    /// </remarks>
    private static async Task SaveCacheAsync(
        CommandLineOptions options,
        AnalysisCache? cache,
        TextWriter errorOutput)
    {
        if (options.CachePath is not { } cachePath || cache is null)
        {
            return;
        }

        if (!cache.TrySave(cachePath, out string? error))
        {
            await errorOutput.WriteLineAsync(
                $"キャッシュを書き出せませんでした ({cachePath}): {error}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ベースラインを読み込んで、記録済みの指摘を除外する。
    /// </summary>
    /// <param name="baselinePath">ベースラインファイルのパス。</param>
    /// <param name="result">解析結果。除外後の結果で置き換える。</param>
    /// <param name="error">読み込みに失敗した場合の説明。</param>
    /// <returns>読み込めた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>読み込みの失敗はツールエラーとして扱う。</b>
    /// ベースラインが効いていない状態で解析を続けると、
    /// 大量の既存の指摘で CI が落ちるか、
    /// 逆に「新規の指摘だけを見ている」と誤解したまま運用されることになる。
    /// </remarks>
    private static bool TryApplyBaseline(
        string baselinePath,
        ref AnalysisResult result,
        out string? error)
    {
        DiagnosticBaseline? baseline = DiagnosticBaseline.Load(baselinePath, out error);

        if (baseline is null)
        {
            return false;
        }

        result = result with { Diagnostics = baseline.Filter(result.Diagnostics) };
        return true;
    }

    /// <summary>
    /// 解析の中身を表示する HTML を書き出す。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <param name="files">解析対象のファイル。</param>
    /// <param name="outputPath">出力先。</param>
    /// <param name="output">標準出力。</param>
    /// <param name="errorOutput">エラー出力。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>終了コード。</returns>
    /// <remarks>
    /// <b>対象は 1 ファイルに限る。</b>
    /// 中身を確かめるための機能であり、
    /// 複数ファイルを 1 つの画面に混ぜても目的の箇所を探しにくくなるだけである。
    /// </remarks>
    private static async Task<ExitCode> WriteInspectionAsync(
        CommandLineOptions options,
        EffectiveSettings settings,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        ImmutableArray<string> files,
        string outputPath,
        TextWriter output,
        TextWriter errorOutput,
        CancellationToken cancellationToken)
    {
        if (files.Length != 1)
        {
            await errorOutput.WriteLineAsync(
                $"--inspect は 1 つのシェーダーを対象にします ({files.Length} 件が指定されました)。")
                .ConfigureAwait(false);
            return ExitCode.ToolError;
        }

        SourceText text;
        try
        {
            text = await SourceText.LoadAsync(files[0], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await errorOutput.WriteLineAsync($"ファイルを読み込めません ({files[0]}): {ex.Message}")
                .ConfigureAwait(false);
            return ExitCode.ToolError;
        }

        SemanticsOptions semantics = SemanticsSetup.Create(options, settings);

        ShaderCompilation compilation =
            ShaderSourceKinds.FromPath(text.FilePath) == ShaderSourceKind.Hlsl
                ? ShaderCompilation.CreateForHlsl(text, semantics)
                : ShaderCompilation.Create(text, ShaderLabSyntaxTree.Parse(text), semantics);

        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(analyzers, settings.AnalyzerOptions)
            .Analyze(compilation.CreateAnalysisTarget(), cancellationToken);

        await File.WriteAllTextAsync(
            outputPath, AnalysisInspector.BuildHtml(compilation, diagnostics), cancellationToken)
            .ConfigureAwait(false);

        await output.WriteLineAsync($"解析の中身を書き出しました: {outputPath}").ConfigureAwait(false);
        return ExitCode.Success;
    }

    /// <summary>
    /// 現在の指摘をベースラインとして書き出す。
    /// </summary>
    /// <param name="baselinePath">書き出す先のパス。</param>
    /// <param name="result">解析結果。</param>
    /// <param name="output">結果を伝える出力先。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>書き出しの完了を表すタスク。</returns>
    private static async Task WriteBaselineAsync(
        string baselinePath,
        AnalysisResult result,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string content = DiagnosticBaseline.CreateContent(result.Diagnostics, baselinePath);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(baselinePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(baselinePath, content, cancellationToken).ConfigureAwait(false);

        await output.WriteLineAsync(
            $"{result.Diagnostics.Length} 件の指摘をベースラインへ記録しました: {baselinePath}")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 指定された形式で診断を整形する。
    /// </summary>
    /// <param name="options">実行時オプション。</param>
    /// <param name="analyzers">実行したアナライザ。SARIF のルール一覧に使う。</param>
    /// <param name="result">解析結果。</param>
    /// <param name="output">出力先。色付けの可否判定に使う。</param>
    /// <returns>整形済みのレポート。</returns>
    private static string FormatReport(
        CommandLineOptions options,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        AnalysisResult result,
        TextWriter output)
    {
        string basePath = options.BasePath ?? Directory.GetCurrentDirectory();

        return options.Format switch
        {
            OutputFormat.Json =>
                new JsonDiagnosticReporter(basePath).Format(result.Diagnostics, result.AnalyzedFileCount),

            OutputFormat.Sarif =>
                new SarifDiagnosticReporter(RuleCatalog.GetAllRules(analyzers), basePath, GetVersion())
                    .Format(result.Diagnostics),

            OutputFormat.GitHub =>
                new GitHubDiagnosticReporter(basePath).Format(result.Diagnostics, result.AnalyzedFileCount),

            // 出力をファイルへリダイレクトする場合は色を付けない。
            // ANSI エスケープがそのまま混入したファイルは、後段のツールで扱えなくなるため。
            _ => new TextDiagnosticReporter(
                    useColor: options.OutputPath is null
                        && ReferenceEquals(output, Console.Out)
                        && !Console.IsOutputRedirected)
                .Format(result.Diagnostics, result.AnalyzedFileCount),
        };
    }

    /// <summary>
    /// レポートを出力先へ書き出す。
    /// </summary>
    /// <param name="options">実行時オプション。</param>
    /// <param name="report">整形済みのレポート。</param>
    /// <param name="output">標準出力先。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>書き出しの完了を表すタスク。</returns>
    private static async Task WriteReportAsync(
        CommandLineOptions options,
        string report,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (options.OutputPath is null)
        {
            await output.WriteAsync(report).ConfigureAwait(false);
            return;
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(options.OutputPath, report, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 実装されている全ルールを一覧表示用に整形する。
    /// </summary>
    /// <param name="analyzers">対象のアナライザ。</param>
    /// <returns>ルール一覧のテキスト。</returns>
    /// <remarks>
    /// ルール ID 順に並べる。設定ファイルを書く利用者が、
    /// 有効にできるルールを網羅的に確認するための出力である。
    /// </remarks>
    private static string FormatRuleList(ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        ImmutableArray<DiagnosticDescriptor> descriptors = RuleCatalog.GetShaderRules(analyzers);

        System.Text.StringBuilder builder = new();
        int count = 0;

        foreach (DiagnosticDescriptor descriptor in descriptors)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"{descriptor.Id}  {descriptor.DefaultSeverity.ToString().ToLowerInvariant(),-7}  {descriptor.Category,-12}  {descriptor.Title}");
            builder.AppendLine();
            count++;
        }

        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"{count} 個のルールが実装されています。");
        builder.AppendLine();
        return builder.ToString();
    }

    /// <summary>現在は解析しないファイルを見つけたことを伝える。</summary>
    /// <param name="options">コマンドライン引数。</param>
    /// <param name="unsupported">解析しないファイル。</param>
    /// <param name="errorOutput">標準エラー。解析結果 (JSON や SARIF) には混ぜない。</param>
    /// <returns>非同期操作。</returns>
    /// <remarks>
    /// 直接指定されたファイルは 1 件ずつ、フォルダーの中で見つけたものは拡張子ごとの件数で伝える。
    /// フォルダーの中の Shader Graph は数が多くなりうるので、1 件ずつ並べると結果が読めなくなる。
    /// </remarks>
    private static async Task ReportUnsupportedFilesAsync(
        CommandLineOptions options,
        ImmutableArray<string> unsupported,
        TextWriter errorOutput)
    {
        if (unsupported.IsEmpty)
        {
            return;
        }

        HashSet<string> direct = new(
            options.InputPaths.Where(File.Exists).Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);

        foreach (string file in unsupported.Where(direct.Contains))
        {
            await errorOutput.WriteLineAsync($"現在解析の対象外です: {file}").ConfigureAwait(false);
        }

        string[] counts =
        [
            .. unsupported
                .Where(f => !direct.Contains(f))
                .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key} {g.Count()} 件"),
        ];

        if (counts.Length > 0)
        {
            await errorOutput.WriteLineAsync($"現在解析の対象外のファイルがあります: {string.Join(", ", counts)}")
                .ConfigureAwait(false);
        }
    }

    /// <summary>アセンブリのバージョンを表す文字列を返す。</summary>
    /// <returns>バージョン文字列。</returns>
    private static string GetVersion()
    {
        Assembly assembly = typeof(Program).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
