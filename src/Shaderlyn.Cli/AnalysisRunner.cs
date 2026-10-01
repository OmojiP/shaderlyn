using System.Collections.Immutable;
using Shaderlyn.Cli.Caching;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Cli;

/// <summary>
/// 解析 1 回分の結果。
/// </summary>
/// <param name="Diagnostics">収集した診断。ソースコード上の出現順に整列済み。</param>
/// <param name="AnalyzedFileCount">実際に解析できたファイル数。</param>
internal sealed record AnalysisResult(ImmutableArray<Diagnostic> Diagnostics, int AnalyzedFileCount)
{
    /// <summary>
    /// 指定した重要度以上の診断が含まれるかを判定する。
    /// </summary>
    /// <param name="threshold">閾値となる重要度。</param>
    /// <returns>閾値以上の診断があれば <see langword="true"/>。</returns>
    /// <remarks>終了コードを <see cref="ExitCode.DiagnosticsFound"/> にするかの判定に使う。</remarks>
    public bool HasDiagnosticsAtOrAbove(DiagnosticSeverity threshold)
        => Diagnostics.Any(d => d.Severity >= threshold);

    /// <summary>
    /// 解析以外の経路で得られた診断を加える。
    /// </summary>
    /// <param name="additional">加える診断。</param>
    /// <returns>診断を加えた結果。整列済み。</returns>
    /// <remarks>
    /// 設定ファイルの問題のように、ファイルの解析からは出てこない診断を混ぜるために使う。
    /// 出力の順序を保つため、加えたあとで並べ直す。
    /// </remarks>
    public AnalysisResult WithAdditionalDiagnostics(ImmutableArray<Diagnostic> additional)
    {
        if (additional.IsDefaultOrEmpty)
        {
            return this;
        }

        ImmutableArray<Diagnostic>.Builder merged = ImmutableArray.CreateBuilder<Diagnostic>();
        merged.AddRange(Diagnostics);
        merged.AddRange(additional);
        merged.Sort(Diagnostic.DocumentOrderComparer);

        return this with { Diagnostics = merged.ToImmutable() };
    }
}

/// <summary>
/// ファイルの読み込みから解析、診断の収集までを取りまとめる。
/// </summary>
/// <remarks>
/// 出力形式や終了コードの決定は含まない。それらは <see cref="Program"/> の責務であり、
/// この型はテストから「どう出力するか」を気にせず解析結果だけを検証できるようにしてある。
/// </remarks>
internal sealed class AnalysisRunner
{
    private readonly AnalyzerDriver _driver;
    private readonly SemanticsOptions? _semanticsOptions;
    private readonly AnalysisTimings? _timings;
    private readonly AnalysisCache? _cache;

    /// <summary>
    /// 実行器を生成する。
    /// </summary>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <param name="options">実行時設定。</param>
    /// <param name="semanticsOptions">
    /// 意味解析の設定。<see langword="null"/> の場合、意味解析を行わない。
    /// </param>
    /// <remarks>
    /// 意味解析を省略できるようにしているのは、
    /// ShaderLab 単体で完結するルールだけを高速に回したい場合があるためである。
    /// 省略すると、セマンティックモデルを必要とするルールは<b>何も報告しなくなる</b>。
    /// 検査していないことは <c>SL0002</c> でも報告されないので、
    /// 意図して省略する場合以外は指定すること。
    /// </remarks>
    /// <param name="timings">
    /// 解析の内訳を測る先。<see langword="null"/> の場合は測らない。
    /// </param>
    /// <param name="cache">
    /// 実行のあいだで結果を持ち越すキャッシュ。<see langword="null"/> の場合は持ち越さない。
    /// </param>
    public AnalysisRunner(
        IEnumerable<DiagnosticAnalyzer> analyzers,
        AnalyzerOptions? options = null,
        SemanticsOptions? semanticsOptions = null,
        AnalysisTimings? timings = null,
        AnalysisCache? cache = null)
    {
        _driver = new AnalyzerDriver(analyzers, options, timings);
        _semanticsOptions = semanticsOptions;
        _timings = timings;
        _cache = cache;
    }

    /// <summary>
    /// 指定されたファイル群を解析する。
    /// </summary>
    /// <param name="filePaths">解析対象のファイルパス。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>解析結果。</returns>
    /// <remarks>
    /// <para>
    /// 1 ファイルの読み込みに失敗しても解析全体は止めず、
    /// <see cref="WellKnownDescriptors.FileReadFailed"/> として報告して次のファイルへ進む。
    /// 大量のファイルを解析する CI で、1 つの権限エラーが全体を落とすのを避けるためである。
    /// </para>
    /// <para>
    /// <b>ファイルは並列に解析する。</b>
    /// 意味解析では 1 つの Pass ごとに include の連鎖をすべて展開し直すため、
    /// 1 ファイルあたりの処理は ShaderLab 単体の解析とは桁が違う。
    /// PR ごとに走らせる用途では、逐次処理だと現実的な時間に収まらない。
    /// </para>
    /// <para>
    /// <b>出力の順序は並列化の影響を受けない。</b>
    /// 診断は最後にパスと位置で整列するため、処理順とは無関係に同じ結果になる。
    /// リンタの出力が実行のたびに変わると、差分での比較もベースラインの照合も成り立たない。
    /// </para>
    /// </remarks>
    public async Task<AnalysisResult> RunAsync(
        ImmutableArray<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        // ファイルごとの結果を添字で受け取る。
        // 共有のコレクションへ書き込むより単純で、ロックも要らない。
        ImmutableArray<Diagnostic>[] perFile = new ImmutableArray<Diagnostic>[filePaths.Length];
        int analyzedCount = 0;

        ParallelOptions parallelOptions = new()
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };

        await Parallel.ForAsync(0, filePaths.Length, parallelOptions, async (index, token) =>
        {
            (ImmutableArray<Diagnostic> diagnostics, bool analyzed) =
                await AnalyzeFileAsync(filePaths[index], token).ConfigureAwait(false);

            perFile[index] = diagnostics;

            if (analyzed)
            {
                Interlocked.Increment(ref analyzedCount);
            }
        }).ConfigureAwait(false);

        ImmutableArray<Diagnostic>.Builder allDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        // 利用者が書いた共通のヘッダの指摘は、それを取り込むシェーダーの数だけ出る。
        // 同じ位置の同じ指摘は 1 件にまとめる。取り込む側によって中身が違うものは、別の指摘として残す。
        HashSet<(string Id, string FilePath, TextSpan Span, string Message)> seen = [];

        foreach (ImmutableArray<Diagnostic> diagnostics in perFile)
        {
            foreach (Diagnostic diagnostic in diagnostics)
            {
                if (seen.Add((diagnostic.Id, diagnostic.Location.FilePath, diagnostic.Location.Span, diagnostic.GetMessage())))
                {
                    allDiagnostics.Add(diagnostic);
                }
            }
        }

        allDiagnostics.Sort(Diagnostic.DocumentOrderComparer);
        return new AnalysisResult(allDiagnostics.ToImmutable(), analyzedCount);
    }

    /// <summary>
    /// 1 ファイルを解析する。
    /// </summary>
    /// <param name="filePath">解析対象のファイルパス。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>
    /// 検出した診断と、実際に解析できたかどうか。
    /// 読み込みに失敗した場合は解析できなかったものとして数え、
    /// 読み込み失敗を表す診断 1 件を返す。
    /// </returns>
    private async Task<(ImmutableArray<Diagnostic> Diagnostics, bool Analyzed)> AnalyzeFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        SourceText text;
        try
        {
            text = await SourceText.LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読み込めなかったファイル自身を位置として指すため、空のテキストを作って位置を作る。
            SourceText placeholder = SourceText.From(string.Empty, filePath);
            return
            ([
                Diagnostic.Create(
                    WellKnownDescriptors.FileReadFailed,
                    Location.Create(placeholder, new TextSpan(0, 0)),
                    filePath,
                    ex.Message),
            ], false);
        }

        // 前回と内容が変わっていなければ、前回の指摘をそのまま返す。
        // 取り込むファイル一式まで含めて突き合わせるのはキャッシュ側の責務である。
        if (_cache is { } cache && cache.TryGetDiagnostics(text, out ImmutableArray<Diagnostic> reused))
        {
            return (reused, true);
        }

        ImmutableArray<Diagnostic> diagnostics =
            Analyze(text, out ImmutableArray<string> includePaths, cancellationToken);

        _cache?.Store(text, includePaths, diagnostics);
        return (diagnostics, true);
    }

    /// <summary>
    /// 読み込み済みのソーステキストを解析する。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>検出した診断。ソースコード上の出現順に整列済み。</returns>
    /// <remarks>
    /// <para>
    /// <b>ディスクを読まない入口である。</b>
    /// エディタに常駐して解析する場合、対象は保存前のテキストになる。
    /// パスを受け取る入口しか無いと、解析のたびに一時ファイルへ書き出すことになり、
    /// 保存していない編集を解析できない。
    /// </para>
    /// <para>
    /// 意味解析を行うかどうかは生成時の設定で決まる。
    /// 省略した場合、ShaderLab 単体で完結するルールだけが走る。
    /// これは<b>入力のたびに走らせても間に合う</b>速さであり、
    /// include の展開を伴う意味解析とは 1 桁以上の差がある。
    /// </para>
    /// </remarks>
    public ImmutableArray<Diagnostic> Analyze(SourceText text, CancellationToken cancellationToken = default)
        => Analyze(text, out _, cancellationToken);

    /// <summary>
    /// 読み込み済みのソーステキストを解析し、取り込むファイル一式も返す。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="includePaths">
    /// 解析中に解決した <c>#include</c> のパス。解決できなかったパスも含む。
    /// 意味解析を行わない場合は空になる。
    /// </param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>検出した診断。ソースコード上の出現順に整列済み。</returns>
    /// <remarks>
    /// <b>取り込むファイル一式は結果のキャッシュのためだけに要る。</b>
    /// このファイル自身が変わっていなくても、
    /// 取り込んでいるヘッダが変われば結果は変わる。
    /// </remarks>
    private ImmutableArray<Diagnostic> Analyze(
        SourceText text,
        out ImmutableArray<string> includePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        // 構文木を歩く処理は木の深さだけ再帰する。長い式では既定のスタックが尽き、
        // 捕まえる手立てが無いままプロセスが終了する (Shaderlyn.Core.Analysis.DeepStack)。
        ImmutableArray<string> resolved = [];
        ImmutableArray<Diagnostic> diagnostics = DeepStack.Run(
            () => AnalyzeCore(text, out resolved, cancellationToken));

        includePaths = resolved;
        return diagnostics;
    }

    /// <summary>
    /// 解析の本体。大きなスタックを持つスレッドから呼ばれる。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="includePaths">解析中に解決した <c>#include</c> のパス。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>検出した診断。</returns>
    private ImmutableArray<Diagnostic> AnalyzeCore(
        SourceText text,
        out ImmutableArray<string> includePaths,
        CancellationToken cancellationToken)
    {
        includePaths = [];

        // どの言語として読むかは拡張子だけで決める。
        // .compute を ShaderLab として読むと、正しいファイルに SL0001 が出る。
        bool isHlsl = ShaderSourceKinds.FromPath(text.FilePath) == ShaderSourceKind.Hlsl;

        long start = AnalysisTimings.Now();
        ShaderLabSyntaxTree? tree = isHlsl ? null : ShaderLabSyntaxTree.Parse(text);
        _timings?.Add(AnalysisStep.ShaderLab, start);

        // 意味解析を行う場合、解析単位の組み立てはセマンティックモデル側に任せる。
        // 埋め込み HLSL の構文エラーは、そこで初めて分かる解析基盤の診断であり、
        // ShaderLab の構文診断とあわせて渡さなければ出力に現れない。
        long expandStart = AnalysisTimings.Now();
        ShaderCompilation? compilation = _semanticsOptions is null
            ? null
            : tree is null
                ? ShaderCompilation.CreateForHlsl(text, _semanticsOptions)
                : ShaderCompilation.Create(text, tree, _semanticsOptions);
        _timings?.Add(AnalysisStep.Expand, expandStart);

        if (compilation is not null)
        {
            includePaths = compilation.AllIncludePaths;
        }

        // 意味解析を省いた場合、HLSL 単体ファイルには見るものが無い。
        // ShaderLab 単体で完結するルールに、対応する構文が 1 つも無いためである。
        long unitStart = AnalysisTimings.Now();
        AnalysisTarget unit = compilation is not null
            ? compilation.CreateAnalysisTarget()
            : tree is null
                ? new AnalysisTarget(text, null, [])
                : new AnalysisTarget(text, tree.Root, tree.Diagnostics);
        _timings?.Add(AnalysisStep.Target, unitStart);

        if (_timings is not null && compilation is not null)
        {
            _timings.AddScale(
                compilation.Programs.Length,
                compilation.SymbolVariants.Length,
                compilation.Programs.Sum(p => p.Tree.PreprocessResult.Tokens.Length)
                + compilation.SymbolVariants.Sum(p => p.Tree.PreprocessResult.Tokens.Length));
        }

        long ruleStart = AnalysisTimings.Now();
        ImmutableArray<Diagnostic> diagnostics = _driver.Analyze(unit, cancellationToken);
        _timings?.Add(AnalysisStep.Rules, ruleStart);

        return diagnostics;
    }
}
