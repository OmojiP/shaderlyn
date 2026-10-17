using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using Shaderlyn.Core.IO;
using Shaderlyn.Cli.Baseline;
using Shaderlyn.Cli.Caching;
using Shaderlyn.Cli.Inspection;
using Shaderlyn.Cli.Reporting;
using Shaderlyn.Configuration;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;
using Shaderlyn.Unity;
using Shaderlyn.Cli.Environments;

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
    public static Task<ExitCode> RunAsync(
        string[] args,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        TextWriter output,
        TextWriter errorOutput,
        CancellationToken cancellationToken = default)
        => RunAsync(args, analyzers, [], output, errorOutput, cancellationToken);

    /// <summary>
    /// 独自エンジンの環境を足して、解析を実行する。
    /// </summary>
    /// <param name="args">コマンドライン引数。</param>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <param name="environments">
    /// 足す環境。設定ファイルの <c>environment</c> か <c>--environment</c> に名前を書くと選ばれる。
    /// </param>
    /// <param name="output">通常の出力先。</param>
    /// <param name="errorOutput">エラーメッセージの出力先。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>終了コード。</returns>
    /// <exception cref="ArgumentException">環境の名前が組み込みの環境やほかの環境と重なっている。</exception>
    public static async Task<ExitCode> RunAsync(
        string[] args,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        ImmutableArray<HostEnvironment> environments,
        TextWriter output,
        TextWriter errorOutput,
        CancellationToken cancellationToken = default)
    {
        AnalysisEnvironments.Validate(environments);
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

        // 設定を作る処理は指定を書き換える。ファイルごとの設定 (AnalysisSettings) は、書き換える前の指定から作る。
        CommandLineOptions original = options.Clone();

        // 設定ファイルは最初の解析対象から上へ探す。これが既定の解析設定 (今までの 1 つの設定と環境) になる。
        EffectiveSettings settings = EffectiveSettings.Create(options, environments);

        if (options.UnrealGenerateOutputPath is { } generateOutput)
        {
            return await GenerateUnrealAsync(settings, generateOutput, output, errorOutput).ConfigureAwait(false);
        }

        if (!AnalysisEnvironments.TryCreate(settings, out IAnalysisEnvironment? environment))
        {
            await errorOutput.WriteLineAsync(AnalysisEnvironments.DescribeUnknown(settings.Environment, environments)).ConfigureAwait(false);
            return ExitCode.ToolError;
        }

        if (environment.Name != UnityAnalysisEnvironment.EnvironmentName && !settings.UnityOnlyCommandLineOptions.IsEmpty)
        {
            await errorOutput.WriteLineAsync(
                $"{string.Join(", ", settings.UnityOnlyCommandLineOptions)} は Unity の環境でだけ使えます (環境: {environment.Name})。")
                .ConfigureAwait(false);
            return ExitCode.ToolError;
        }

        ImmutableArray<string> discovered = ShaderFileDiscovery.Discover(
            options.InputPaths,
            AnalysisSettings.DiscoveryExtensions(environments),
            out ImmutableArray<string> missingPaths,
            out ImmutableArray<string> unsupportedFiles);

        // ファイルごとに、一番近い設定ファイルとその環境 (HLSL 以外の言語は拡張子の環境) で解析する。
        AnalysisSetting primary = new(options, settings, environment, []);
        ImmutableArray<AnalysisSetting> groups = AnalysisSettings.Build(
            original,
            primary,
            environments,
            discovered,
            options.InputPaths.Where(File.Exists).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase),
            out ImmutableArray<Diagnostic> unusableConfigurations);

        // 解析するファイルが無くても、既定の解析設定は残す (環境の表示と「0 ファイルを解析」のため)。
        if (groups.IsEmpty)
        {
            groups = [primary];
        }

        ImmutableArray<string> files = [.. groups.SelectMany(g => g.Files)];

        // 環境の表示は、対象のパスが間違っていてもそれを含めて見せる。止めると何が違うのかが分からない。
        if (options.ShowEnvironment)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                AnalysisSetting group = groups[i];
                await output.WriteAsync(
                    EnvironmentReport.Build(
                        group.Options, group.Settings, group.Environment, group.Files, missingPaths, GetVersion(),
                        groups.Length == 1 ? null : (i + 1, groups.Length)))
                    .ConfigureAwait(false);
            }

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

        if (options.InspectOutputPath is { } inspectOutputPath)
        {
            // 調べる出力は 1 つの環境のもの。先頭の解析設定 (既定の設定と環境のもの) を書く。
            AnalysisSetting inspected = groups[0];

            // 外した解析設定は黙って捨てない。どのファイルが出力に入っていないかを伝える。
            foreach (AnalysisSetting skipped in groups.Skip(1))
            {
                await errorOutput.WriteLineAsync(
                    $"--inspect は 1 つの解析設定のファイルだけを書きます。次の解析設定のファイルは書きません: 環境 {skipped.Environment.DisplayName}、"
                    + $"設定ファイル {skipped.Settings.Configuration.FilePath ?? "(なし)"}、{skipped.Files.Length} ファイル")
                    .ConfigureAwait(false);
            }

            return await WriteInspectionAsync(
                inspected.Options, inspected.Settings, inspected.Environment, analyzers, inspected.Files, inspectOutputPath, output, errorOutput, cancellationToken)
                .ConfigureAwait(false);
        }

        AnalysisTimings? timings = options.ReportTimings ? new AnalysisTimings() : null;
        SemanticsOptions semanticsOptions = SemanticsSetup.Create(options, settings, timings);
        AnalysisCache? cache = await LoadCacheAsync(options, groups, analyzers, errorOutput).ConfigureAwait(false);
        bool cacheWritable = await CheckCacheWritableAsync(options, errorOutput).ConfigureAwait(false);

        List<AnalysisResult> perGroup = [];

        foreach (AnalysisSetting group in groups)
        {
            // 既定の解析設定は、上で作った解析の設定をそのまま使う (解析設定が 1 つなら今までと同じ)。
            SemanticsOptions groupSemantics = ReferenceEquals(group, primary) || ReferenceEquals(group.Settings, settings)
                ? semanticsOptions
                : SemanticsSetup.Create(group.Options, group.Settings, timings);

            AnalysisRunner runner = new(
                analyzers, group.Settings.AnalyzerOptions, groupSemantics, timings, cache, group.Environment);

            AnalysisResult groupResult = await runner.RunAsync(group.Files, cancellationToken).ConfigureAwait(false);
            perGroup.Add(groupResult);
        }

        // 解析設定をまたいでも、同じ指摘は 1 件にまとめて並べ直す (同じヘッダを 2 つの解析設定が取り込むことがある)。
        // 解析設定が 1 つならそのまま使う。並べ直しは同じ位置の指摘の順を保たないので、2 度並べると順が変わる。
        AnalysisResult result = perGroup.Count == 1
            ? perGroup[0]
            : AnalysisResult.Merge(perGroup.Select(r => r.Diagnostics), perGroup.Sum(r => r.AnalyzedFileCount));

        if (cacheWritable)
        {
            await SaveCacheAsync(options, cache, errorOutput).ConfigureAwait(false);
        }

        if (timings is not null)
        {
            // 標準エラーへ出す。JSON や SARIF の出力へ混ぜない。
            await errorOutput.WriteAsync(timings.Describe(semanticsOptions.IncludeCache, cache))
                .ConfigureAwait(false);
        }
        // 設定ファイルの問題は、設定ファイルごとに 1 回だけ出す (同じ設定ファイルを複数の解析設定が使う)。
        ImmutableArray<Diagnostic> configurationDiagnostics =
        [
            .. groups.SelectMany(g => g.Settings.ConfigurationDiagnostics)
                .Concat(unusableConfigurations)
                .DistinctBy(d => (d.Id, d.Location.FilePath, d.Location.Span, d.GetMessage())),
        ];
        result = result.WithAdditionalDiagnostics(configurationDiagnostics);

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

        // 出力は形式ごとの重要度で絞る。終了コードは絞る前のすべての指摘で決める (表示を絞って CI の合否が変わらないように)。
        AnalysisResult shown = result.OnlyAtOrAbove(
            settings.GetMinimumReportedSeverity(options.Format, options.MinSeverity), configurationDiagnostics);
        IncludeTraceMode includeTrace = settings.GetIncludeTrace(options.IncludeTrace);
        string report = FormatReport(options, analyzers, shown, output, result.Diagnostics.Length - shown.Diagnostics.Length, includeTrace);
        await WriteReportAsync(options, report, output, cancellationToken).ConfigureAwait(false);

        // 注釈は主たる出力とは別に書く。SARIF をファイルへ書きながら
        // PR には即座に注釈を出す、という 2 つの目的を 1 回の解析で果たすため。
        if (options.Annotate && options.Format != OutputFormat.GitHub)
        {
            GitHubDiagnosticReporter annotator =
                new(options.BasePath ?? Directory.GetCurrentDirectory(), includeTrace);

            AnalysisResult annotated =
                result.OnlyAtOrAbove(
                    settings.GetMinimumReportedSeverity(OutputFormat.GitHub, options.MinSeverity), configurationDiagnostics);

            await output.WriteAsync(annotator.Format(annotated.Diagnostics, annotated.AnalyzedFileCount))
                .ConfigureAwait(false);
        }

        return result.HasDiagnosticsAtOrAbove(options.ErrorOn) ? ExitCode.DiagnosticsFound : ExitCode.Success;
    }

    /// <summary>
    /// 結果のキャッシュを読み込む。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="groups">設定ファイルと環境ごとの解析設定。</param>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <param name="errorOutput">エラーメッセージの出力先。</param>
    /// <returns>読み込んだキャッシュ。<c>--cache</c> の指定が無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>読み込みの失敗はツールエラーにしない。</b>
    /// キャッシュは速さのための仕組みでしかなく、
    /// 捨てて全件を解析し直せば正しい結果になる。
    /// ただし何も伝えずに捨てると「なぜか毎回遅い」ことに気づけないので、
    /// 読めなかったことは標準エラーへ伝える。
    /// </para>
    /// <para>
    /// 解析設定が複数あれば、すべての解析設定の鍵をまとめた鍵にする。どの設定ファイルを変えても使い回さない。
    /// 解析設定が 1 つなら、その解析設定の鍵そのもの (今までのキャッシュを使い回せる)。
    /// </para>
    /// </remarks>
    private static async Task<AnalysisCache?> LoadCacheAsync(
        CommandLineOptions options,
        ImmutableArray<AnalysisSetting> groups,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        TextWriter errorOutput)
    {
        if (options.CachePath is not { } cachePath)
        {
            return null;
        }

        string key = groups.Length == 1
            ? AnalysisCacheKey.Compute(groups[0].Options, groups[0].Settings, analyzers)
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                string.Join('\n', groups.Select(g => $"{g.Environment.Name}:{AnalysisCacheKey.Compute(g.Options, g.Settings, analyzers)}")))));

        AnalysisCache cache = AnalysisCache.Load(cachePath, key, out string? warning);

        if (warning is not null)
        {
            await errorOutput.WriteLineAsync(
                $"キャッシュを読み込めませんでした ({cachePath}): {warning}").ConfigureAwait(false);
        }

        return cache;
    }

    /// <summary>
    /// キャッシュを書き出せるかを、解析の前に確かめる。書き出せなければ、理由をすぐに伝える。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="errorOutput">エラーメッセージの出力先。</param>
    /// <returns>書き出せるか (<c>--cache</c> が無ければ <see langword="true"/>)。</returns>
    /// <remarks>
    /// 解析の後に書き出すので、確かめずにいると、全件の解析が終わってから書けないことが分かる。
    /// 書き出せなくても解析結果は正しいので、ツールエラーにはせずに解析を続ける。
    /// </remarks>
    private static async Task<bool> CheckCacheWritableAsync(CommandLineOptions options, TextWriter errorOutput)
    {
        if (options.CachePath is not { } cachePath || AnalysisCache.CanWrite(cachePath, out string? error))
        {
            return true;
        }

        await errorOutput.WriteLineAsync(
            $"キャッシュを書き出せません ({cachePath}): {error} 解析は続けますが、キャッシュは残りません。").ConfigureAwait(false);
        return false;
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
    /// <param name="environment">解析する環境。</param>
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
        IAnalysisEnvironment environment,
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
            await errorOutput.WriteLineAsync($"ファイルを読み込めません ({files[0]}): {FileErrorDescriber.Describe(ex, files[0])}")
                .ConfigureAwait(false);
            return ExitCode.ToolError;
        }

        PreparedFile prepared = environment.Prepare(text, SemanticsSetup.Create(options, settings), timings: null);

        // 中身を見せる画面は HLSL のセマンティックモデルを描く。HLSL ではない言語の環境では、まだ描けない。
        if (prepared.Compilation is not { } compilation)
        {
            await errorOutput.WriteLineAsync($"--inspect は環境 '{environment.Name}' のファイルにはまだ使えません。")
                .ConfigureAwait(false);
            return ExitCode.ToolError;
        }

        ImmutableArray<Diagnostic> diagnostics = new AnalyzerDriver(analyzers, settings.AnalyzerOptions)
            .Analyze(prepared.Target, cancellationToken);

        await File.WriteAllTextAsync(
            outputPath, AnalysisInspector.BuildHtml(compilation, diagnostics), cancellationToken)
            .ConfigureAwait(false);

        await output.WriteLineAsync($"解析の中身を書き出しました: {outputPath}").ConfigureAwait(false);
        return ExitCode.Success;
    }

    /// <summary>
    /// Unreal のエンジンとプロジェクトの C++ から、生成される取り込みを作って書き出す (<c>--unreal-generate</c>)。
    /// </summary>
    /// <param name="settings">設定ファイルとコマンドラインを合わせた設定。</param>
    /// <param name="outputDirectory">書き出す先。</param>
    /// <param name="output">通常の出力先。</param>
    /// <param name="errorOutput">エラーメッセージの出力先。</param>
    /// <returns>終了コード。</returns>
    private static async Task<ExitCode> GenerateUnrealAsync(
        EffectiveSettings settings,
        string outputDirectory,
        TextWriter output,
        TextWriter errorOutput)
    {
        ShaderlynConfiguration configuration = settings.Configuration;

        if (configuration.UnrealEngineDirectory is not { } engine || !Directory.Exists(engine))
        {
            await errorOutput.WriteLineAsync(
                "--unreal-generate には、設定ファイルの unreal: engine にエンジンのフォルダー (…/UE_5.5/Engine) を書いてください。")
                .ConfigureAwait(false);
            return ExitCode.ToolError;
        }

        List<string> roots = [Path.Combine(engine, "Source"), Path.Combine(engine, "Plugins")];

        if (configuration.UnrealProjectDirectory is { } project)
        {
            roots.Add(Path.Combine(project, "Source"));
            roots.Add(Path.Combine(project, "Plugins"));
        }

        string fullOutput = Path.GetFullPath(outputDirectory);
        Unreal.UnrealUniformBufferGeneration generation;

        try
        {
            generation = Unreal.UnrealUniformBufferGenerator.Generate(roots, fullOutput);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await errorOutput.WriteLineAsync($"書き出せませんでした: {FileErrorDescriber.Describe(exception, fullOutput)}").ConfigureAwait(false);
            return ExitCode.ToolError;
        }

        StringBuilder report = new();
        report.AppendLine(CultureInfo.InvariantCulture, $"C++ のファイル {generation.SourceFileCount} 個から、uniform buffer {generation.UniformBufferCount} 個と、エンジンが定義するマクロ {generation.EngineDefineCount} 個を読みました。");
        report.AppendLine(CultureInfo.InvariantCulture, $"シェーダーの登録 (エントリポイントとステージ) を {generation.ShaderRegistrationCount} 個読みました。");
        report.AppendLine(CultureInfo.InvariantCulture, $"書き出し先: {fullOutput}");

        if (!generation.UnreadShaderRegistrations.IsEmpty)
        {
            // 独自のマクロで登録したシェーダーは、設定の units: に書けば解析できる。
            report.AppendLine(CultureInfo.InvariantCulture, $"読めなかったシェーダーの登録 ({generation.UnreadShaderRegistrations.Values.Sum()} 個。これらの .usf はエントリポイントが分からないので、必要なら設定の units: に書いてください):");

            foreach ((string macro, int count) in generation.UnreadShaderRegistrations.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {macro}: {count} 個");
            }
        }

        if (!generation.Incomplete.IsEmpty)
        {
            report.AppendLine("メンバーを読み切れなかった uniform buffer:");

            foreach (string incomplete in generation.Incomplete)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {incomplete}");
            }
        }

        if (!generation.UnknownTypes.IsEmpty)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"知らない C++ の型 (float4 として書いた): {string.Join(", ", generation.UnknownTypes)}");
        }

        if (!string.Equals(configuration.UnrealGeneratedDirectory is { } configured ? Path.GetFullPath(configured) : null, fullOutput, StringComparison.OrdinalIgnoreCase))
        {
            report.AppendLine("解析に使うには、設定ファイルの unreal: に次を書いてください:");
            report.AppendLine(CultureInfo.InvariantCulture, $"  generated: {fullOutput.Replace('\\', '/')}");
        }

        await output.WriteAsync(report.ToString()).ConfigureAwait(false);
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
    /// <param name="hiddenCount">重要度で絞って出力しなかった指摘の数。テキストの要約に添える。</param>
    /// <param name="includeTrace">ヘッダの中の指摘に、取り込んだ経路をどこまで出すか。</param>
    /// <returns>整形済みのレポート。</returns>
    private static string FormatReport(
        CommandLineOptions options,
        ImmutableArray<DiagnosticAnalyzer> analyzers,
        AnalysisResult result,
        TextWriter output,
        int hiddenCount,
        IncludeTraceMode includeTrace)
    {
        string basePath = options.BasePath ?? Directory.GetCurrentDirectory();

        return options.Format switch
        {
            OutputFormat.Json =>
                new JsonDiagnosticReporter(basePath, includeTrace).Format(result.Diagnostics, result.AnalyzedFileCount),

            OutputFormat.Sarif =>
                new SarifDiagnosticReporter(RuleCatalog.GetAllRules(analyzers), basePath, GetVersion(), includeTrace)
                    .Format(result.Diagnostics),

            OutputFormat.GitHub =>
                new GitHubDiagnosticReporter(basePath, includeTrace).Format(result.Diagnostics, result.AnalyzedFileCount),

            // 出力をファイルへリダイレクトする場合は色を付けない。
            // ANSI エスケープがそのまま混入したファイルは、後段のツールで扱えなくなるため。
            _ => new TextDiagnosticReporter(
                    useColor: options.OutputPath is null
                        && ReferenceEquals(output, Console.Out)
                        && !Console.IsOutputRedirected,
                    includeTrace)
                .Format(result.Diagnostics, result.AnalyzedFileCount, hiddenCount),
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
            await errorOutput.WriteLineAsync($"解析の対象外です: {file}").ConfigureAwait(false);
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
            await errorOutput.WriteLineAsync($"解析の対象外のファイルがあります: {string.Join(", ", counts)}")
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
