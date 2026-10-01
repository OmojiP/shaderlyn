using System.Collections.Concurrent;
using System.Collections.Immutable;
using Shaderlyn.Cli;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 解析の段。
/// </summary>
internal enum AnalysisDepth
{
    /// <summary>ShaderLab 単体で完結する検査だけ。入力のたびに走らせられる。</summary>
    Syntax,

    /// <summary>意味解析を伴う検査も含む。保存時に走らせる。</summary>
    Full,
}

/// <summary>
/// 1 つのワークスペースに対する解析の設定を保持する。
/// </summary>
/// <remarks>
/// <para>
/// <b>2 つの実行器を持ち分けるのがこの型の要点である。</b>
/// 意味解析を省いた検査は 0〜2ms で終わるが、
/// 意味解析を伴う検査は Pass の数に比例して数百 ms かかる。
/// 同じ頻度で走らせることはできない。
/// </para>
/// <para>
/// include の解決器とトークンキャッシュは実行器が抱えたまま生き続ける。
/// 取り込んだヘッダを解析のたびに読み直さないためであり、
/// これが常駐する意味そのものである。
/// </para>
/// </remarks>
internal sealed class AnalysisSession
{
    private readonly ImmutableArray<DiagnosticAnalyzer> _analyzers;
    private readonly AnalyzerOptions? _analyzerOptions;
    private readonly AnalysisRunner _syntaxOnly;
    private readonly AnalysisRunner _full;
    private readonly SemanticsOptions _semantics;

    /// <summary>
    /// 定義済みにするシンボルの組み合わせごとの、意味解析の設定と実行器。
    /// </summary>
    /// <remarks>
    /// 解析は受信とは別のスレッドで走るため、同時に引かれる。
    /// 組み合わせは利用者が選んだ数しか生まれないので、捨てずに持ち続ける。
    /// </remarks>
    private readonly ConcurrentDictionary<string, (SemanticsOptions Semantics, AnalysisRunner Full)> _withSymbols =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 組み込みルールで解析の設定を組み立てる。
    /// </summary>
    /// <param name="workspaceDirectory">ワークスペースのルート。設定ファイルの探索に使う。</param>
    public AnalysisSession(string? workspaceDirectory)
        : this(workspaceDirectory, BuiltInAnalyzers.All)
    {
    }

    /// <summary>
    /// 解析の設定を組み立てる。
    /// </summary>
    /// <param name="workspaceDirectory">ワークスペースのルート。設定ファイルの探索に使う。</param>
    /// <param name="analyzers">実行するアナライザ。</param>
    /// <remarks>
    /// <b>アナライザを受け取るのは、自作ルールをエディタでも働かせるためである。</b>
    /// CLI は <c>Program.RunAsync</c> で一覧を差し替えられる。
    /// ここを固定すると、同じルールが CI では落ちるのに編集中は出ないという
    /// 食い違いが残る (docs/custom-rules/tutorial.md)。
    /// </remarks>
    public AnalysisSession(string? workspaceDirectory, IEnumerable<DiagnosticAnalyzer> analyzers)
    {
        ArgumentNullException.ThrowIfNull(analyzers);

        WorkspaceDirectory = workspaceDirectory;

        ImmutableArray<DiagnosticAnalyzer> all = [.. analyzers];

        CommandLineOptions options = BuildOptions(workspaceDirectory);
        EffectiveSettings settings = EffectiveSettings.Create(options);

        ConfigurationDiagnostics = settings.ConfigurationDiagnostics;
        UnityProjectPath = options.UnityProjectPath;

        _analyzers = all;
        _analyzerOptions = settings.AnalyzerOptions;
        _semantics = SemanticsSetup.Create(options, settings);
        _syntaxOnly = new AnalysisRunner(all, settings.AnalyzerOptions);
        _full = new AnalysisRunner(all, settings.AnalyzerOptions, _semantics);
    }

    /// <summary>
    /// セマンティックモデルを組み立てる。
    /// </summary>
    /// <param name="text">対象のテキスト。</param>
    /// <returns>組み立てたセマンティックモデル。</returns>
    /// <remarks>
    /// カーソルの下の式の型を答えるために使う。
    /// 診断のために組み立てたものを使い回さないのは、
    /// 編集のたびにモデルが古くなり、位置が合わなくなるためである。
    /// 呼び出し側がバージョンとあわせて覚えておくこと。
    /// </remarks>
    public ShaderCompilation CreateModel(SourceText text) => CreateModel(text, []);

    /// <summary>
    /// 指定したシンボルを定義済みにして、セマンティックモデルを組み立てる。
    /// </summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="definedSymbols">定義済みにするシンボル。</param>
    /// <returns>組み立てたセマンティックモデル。</returns>
    public ShaderCompilation CreateModel(SourceText text, IReadOnlyCollection<string> definedSymbols)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(definedSymbols);

        return Build(text, ForSymbols(definedSymbols).Semantics);
    }

    /// <summary>
    /// 選んだシンボルだけを有効にした、1 つの構成のセマンティックモデルを組み立てる。
    /// </summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="enabledSymbols">有効にするシンボル。シンボルも含む。</param>
    /// <returns>組み立てたセマンティックモデル。</returns>
    /// <remarks>
    /// <para>
    /// 「効いていない範囲」を利用者が選んだ構成で表示するためだけに使う。
    /// 指摘は出さない。
    /// </para>
    /// <para>
    /// <b>シンボルの両方の分岐を 1 つの木に並べない。</b>
    /// 並べると、選ばなかったシンボルの分岐も効いている側に残る。
    /// バリアントも展開しない。選ばなかったシンボルを有効にした構成は、利用者が見たいものではない。
    /// </para>
    /// <para>
    /// 取り込みのキャッシュは両方の分岐を並べるシンボルも鍵に含むので、
    /// 通常の解析で作った展開結果がここへ混ざることはない。
    /// </para>
    /// </remarks>
    public ShaderCompilation CreateConfigurationModel(SourceText text, IReadOnlyCollection<string> enabledSymbols)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(enabledSymbols);

        SemanticsOptions semantics = DefineSymbols(enabledSymbols) with
        {
            BothBranchSymbols = ImmutableHashSet<string>.Empty,
            MaxSymbolVariants = 0,
            FixedSymbolConfiguration = true,
        };

        return Build(text, semantics);
    }

    /// <summary>テキストを、拡張子に合った言語としてセマンティックモデルにする。</summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="semantics">意味解析の設定。</param>
    /// <returns>組み立てたセマンティックモデル。</returns>
    private static ShaderCompilation Build(SourceText text, SemanticsOptions semantics)
    {
        // どの言語として読むかは拡張子だけで決める。
        // CLI と同じ判断を使う。片方だけ別に判定すると、
        // 「CLI では見るのにエディタでは見ない」という食い違いが生まれる。
        return ShaderSourceKinds.FromPath(text.FilePath) == ShaderSourceKind.Hlsl
            ? ShaderCompilation.CreateForHlsl(text, semantics)
            : ShaderCompilation.Create(text, ShaderLabSyntaxTree.Parse(text), semantics);
    }

    /// <summary>ワークスペースのルート。</summary>
    public string? WorkspaceDirectory { get; }

    /// <summary>見つかった Unity プロジェクトのルート。無い場合は <see langword="null"/>。</summary>
    public string? UnityProjectPath { get; }

    /// <summary>設定ファイルの読み込みで見つかった問題。</summary>
    /// <remarks>
    /// キーのスペルミスで設定が効かないまま運用されるのが最も避けたい失敗であるため、
    /// エディタでも、何も伝えずに捨てることはしない。
    /// </remarks>
    public ImmutableArray<Diagnostic> ConfigurationDiagnostics { get; }

    /// <summary>
    /// スクリプトを解析する。
    /// </summary>
    /// <param name="text">解析対象のテキスト。</param>
    /// <param name="stage">走らせる段。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>検出した診断。</returns>
    public ImmutableArray<Diagnostic> Analyze(
        SourceText text,
        AnalysisDepth stage,
        CancellationToken cancellationToken = default)
        => Analyze(text, stage, [], cancellationToken);

    /// <summary>
    /// 指定したシンボルを定義済みにして、スクリプトを解析する。
    /// </summary>
    /// <param name="text">解析対象のテキスト。</param>
    /// <param name="stage">走らせる段。</param>
    /// <param name="definedSymbols">
    /// 定義済みにするシンボル。ShaderLab 単体の段は展開を行わないので使わない。
    /// </param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    /// <returns>検出した診断。</returns>
    public ImmutableArray<Diagnostic> Analyze(
        SourceText text,
        AnalysisDepth stage,
        IReadOnlyCollection<string> definedSymbols,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(definedSymbols);

        AnalysisRunner runner = stage == AnalysisDepth.Full ? ForSymbols(definedSymbols).Full : _syntaxOnly;
        return runner.Analyze(text, cancellationToken);
    }

    /// <summary>
    /// シンボルの組み合わせに対応する設定と実行器を返す。
    /// </summary>
    /// <param name="definedSymbols">定義済みにするシンボル。</param>
    /// <returns>設定と、意味解析まで行う実行器。</returns>
    /// <remarks>
    /// <para>
    /// <b>シンボルは <c>#define 名前 1</c> と同じ扱いにする。</b>
    /// CLI の <c>--define</c> と同じ意味である。
    /// </para>
    /// <para>
    /// 字句解析と取り込みのキャッシュは元の設定と共有する。
    /// 取り込みのキャッシュはマクロの表の状態を鍵に含むので、
    /// 定義を足した構成の結果が、足していない構成へ混ざることはない。
    /// </para>
    /// </remarks>
    private (SemanticsOptions Semantics, AnalysisRunner Full) ForSymbols(IReadOnlyCollection<string> definedSymbols)
    {
        if (definedSymbols.Count == 0)
        {
            return (_semantics, _full);
        }

        string key = string.Join("\n", definedSymbols.Order(StringComparer.Ordinal));

        return _withSymbols.GetOrAdd(key, _ =>
        {
            SemanticsOptions semantics = DefineSymbols(definedSymbols);
            return (semantics, new AnalysisRunner(_analyzers, _analyzerOptions, semantics));
        });
    }

    /// <summary>シンボルを <c>#define 名前 1</c> として足した設定を返す。</summary>
    /// <param name="symbols">足すシンボル。</param>
    /// <returns>足した設定。</returns>
    private SemanticsOptions DefineSymbols(IReadOnlyCollection<string> symbols)
    {
        ImmutableDictionary<string, string> macros = _semantics.PredefinedMacros;

        foreach (string symbol in symbols)
        {
            macros = macros.SetItem(symbol, "1");
        }

        return _semantics with { PredefinedMacros = macros };
    }

    /// <summary>
    /// ワークスペースから解析の設定を組み立てる。
    /// </summary>
    /// <param name="workspaceDirectory">ワークスペースのルート。</param>
    /// <returns>組み立てた設定。</returns>
    /// <remarks>
    /// Unity プロジェクトのルートは <c>Assets</c> と <c>ProjectSettings</c> の
    /// 両方があるフォルダーを上へ辿って探す。
    /// 指定が無いと対応検査が働かず SL0002 だらけになるため、推定を試みる価値がある。
    /// </remarks>
    private static CommandLineOptions BuildOptions(string? workspaceDirectory)
    {
        List<string> arguments = [];

        if (FindUnityProject(workspaceDirectory) is { } unityProject)
        {
            arguments.Add("--unity-project");
            arguments.Add(unityProject);
        }

        if (workspaceDirectory is not null)
        {
            arguments.Add(workspaceDirectory);
        }

        return CommandLineOptions.TryParse([.. arguments], out CommandLineOptions? options, out _)
            ? options
            : CommandLineOptions.TryParse([], out CommandLineOptions? fallback, out _)
                ? fallback
                : throw new InvalidOperationException("引数の解析に失敗しました。");
    }

    /// <summary>
    /// Unity プロジェクトのルートを探す。
    /// </summary>
    /// <param name="startDirectory">探索を始めるフォルダー。</param>
    /// <returns>見つかったルート。無い場合は <see langword="null"/>。</returns>
    private static string? FindUnityProject(string? startDirectory)
    {
        for (DirectoryInfo? current = startDirectory is null ? null : new DirectoryInfo(startDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Assets"))
                && Directory.Exists(Path.Combine(current.FullName, "ProjectSettings")))
            {
                return current.FullName;
            }
        }

        return null;
    }
}
