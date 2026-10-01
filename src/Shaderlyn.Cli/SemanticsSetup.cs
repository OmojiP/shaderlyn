using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Profiles;

namespace Shaderlyn.Cli;

/// <summary>
/// コマンドライン引数から意味解析の設定を組み立てる。
/// </summary>
/// <remarks>
/// <b>include の探索順序を決めるのがこの型の主な仕事である。</b>
/// 順序を誤ると、プロジェクトが上書きしたはずのヘッダではなく
/// Unity 同梱のヘッダが読まれ、解析結果が実際のビルドと食い違う。
/// </remarks>
internal static class SemanticsSetup
{
    /// <summary>
    /// コマンドライン引数と設定ファイルから意味解析の設定を作る。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    /// <param name="syntaxTimings">
    /// 構文木を作る段ごとの時間を受け取る先。<see langword="null"/> の場合は測らない。
    /// </param>
    /// <returns>意味解析の設定。</returns>
    public static SemanticsOptions Create(
        CommandLineOptions options,
        EffectiveSettings settings,
        ISyntaxTimingRecorder? syntaxTimings = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        string? editorData = ResolveEditorDataPath(options);

        return new SemanticsOptions
        {
            Profile = RenderPipelineProfiles.TryGet(settings.Profile, out IRenderPipelineProfile? profile)
                ? profile
                : RenderPipelineProfiles.Default,

            IncludeResolver = CreateIncludeResolver(options, settings, editorData),
            IsUserInclude = CreateUserIncludePredicate(options, editorData),
            PredefinedMacros = BuildPredefinedMacros(settings.Defines),

            // 指定が無ければ既定値のままにする。
            // ここで 0 を渡すと、既定値を持たせた意味が消える。
            MaxSymbolVariants = options.MaxSymbolVariants ?? SemanticsOptions.DefaultMaxSymbolVariants,

            // 複数ファイルを解析するので必ず共有する。
            // 同じ URP のヘッダ群を Pass ごと・ファイルごとに字句解析し直すと、
            // 解析時間の大半がそこに消える。
            TokenCache = new HlslTokenCache(),

            // 同じヘッダを同じマクロの状態で取り込んだら使い回す。
            // ブロックごと・ファイルごとの展開の大半がこれに当たる。
            IncludeCache = new HlslIncludeCache(),

            // 段ごとの内訳を測る先。--timings のときだけ渡される。
            SyntaxTimings = syntaxTimings,
        };
    }

    /// <summary>
    /// Unity Editor のデータフォルダーを決める。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <returns>データフォルダー。見つからない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 明示的な指定を最優先し、無ければプロジェクトから推定する。
    /// プロジェクトの指定も無い場合は、インストール済みのもので代用する。
    /// ローカルでの実行を「引数なしでもそれなりに動く」状態にしておくためである。
    /// </para>
    /// <para>
    /// <b>公開しているのは、結果のキャッシュの鍵に混ぜるためである。</b>
    /// 推定の結果が変われば読まれるヘッダが変わるので、
    /// 指定を変えていなくても解析し直さなければならない。
    /// </para>
    /// </remarks>
    public static string? ResolveEditorDataPath(CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.UnityEditorDataPath
               ?? UnityInstallation.Lookup(null, options.UnityProjectPath).DataPath;
    }

    /// <summary>
    /// <c>#include</c> を探すフォルダーを、優先する順に返す。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    /// <param name="editorData">Unity Editor のデータフォルダー。</param>
    /// <returns>探すフォルダー。書いているファイルからの相対は含まない (解決器の既定の挙動)。</returns>
    /// <remarks>順序の理由は <see cref="CreateIncludeResolver"/> を見ること。</remarks>
    public static ImmutableArray<string> GetIncludeSearchPaths(
        CommandLineOptions options,
        EffectiveSettings settings,
        string? editorData)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        List<string> searchPaths = [.. settings.IncludePaths];

        if (options.UnityProjectPath is { } projectPath)
        {
            searchPaths.Add(projectPath);
            searchPaths.Add(Path.Combine(projectPath, "Assets"));
        }

        if (editorData is not null)
        {
            searchPaths.Add(Path.Combine(editorData, "CGIncludes"));
            searchPaths.Add(Path.Combine(editorData, "Resources", "PackageManager", "BuiltInPackages"));
        }

        return [.. searchPaths];
    }

    /// <summary>
    /// <c>Packages/&lt;パッケージ名&gt;/...</c> 形式の参照を解決する解決器を作る。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="editorData">Unity Editor のデータフォルダー。</param>
    /// <returns>作った解決器。どちらの指定も無ければ <see langword="null"/>。</returns>
    public static UnityPackageIncludeResolver? CreatePackageResolver(CommandLineOptions options, string? editorData)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.UnityProjectPath is { } project)
        {
            return UnityPackageIncludeResolver.ForProject(project, editorData);
        }

        // プロジェクトの指定が無くても、Editor 同梱パッケージだけは解決できる。
        return editorData is not null
            ? new UnityPackageIncludeResolver([Path.Combine(editorData, "Resources", "PackageManager", "BuiltInPackages")])
            : null;
    }

    /// <summary>
    /// <c>#include</c> の解決器を組み立てる。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="editorData">Unity Editor のデータフォルダー。</param>
    /// <returns>組み立てた解決器。</returns>
    /// <remarks>
    /// <para>
    /// 探索順序は次のとおり。前にあるものが優先される。
    /// </para>
    /// <list type="number">
    ///   <item><description><c>#include</c> を書いているファイルからの相対 (解決器の既定の挙動)</description></item>
    ///   <item><description><c>--include-path</c> で指定されたパス</description></item>
    ///   <item><description>Unity プロジェクトのルートと <c>Assets</c></description></item>
    ///   <item><description>Unity Editor 同梱のヘッダ</description></item>
    ///   <item><description><c>Packages/&lt;パッケージ名&gt;/...</c> 形式の参照</description></item>
    /// </list>
    /// <para>
    /// プロジェクト側を Editor 同梱より先に置いているのは、
    /// 同名のヘッダをプロジェクトが用意している場合に、
    /// Unity 自身と同じくプロジェクト側が勝つようにするためである。
    /// </para>
    /// </remarks>
    /// <param name="settings">設定ファイルを反映した実行時の設定。</param>
    public static IIncludeResolver CreateIncludeResolver(
        CommandLineOptions options,
        EffectiveSettings settings,
        string? editorData)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        ImmutableArray<string> searchPaths = GetIncludeSearchPaths(options, settings, editorData);

        // 読んでよい範囲を決める。シェーダーは信頼できる入力とは限らず、
        // 制限が無いと #include の 1 行で任意のファイルを読める。
        //
        // 根には探索パスに加えて、解析対象そのものを入れる。
        // Assets/Shaders/A.shader から "../Common.hlsl" と書くのは正当だからである。
        //
        // 解析対象が分からない場合は制限しない。
        // 「どこまでが対象か」を決められないまま境界を引くと、
        // 対象のすぐ隣にあるヘッダまで読めなくなる。
        IncludeBoundary boundary = options.InputPaths.IsEmpty
            ? IncludeBoundary.Unrestricted
            : new IncludeBoundary(
            [
                .. searchPaths,
                .. options.InputPaths.Select(path =>
                    Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path),
            ]);

        List<IIncludeResolver> resolvers = [new FileSystemIncludeResolver(searchPaths, boundary)];

        // Packages/<パッケージ名>/... の形は実在するフォルダーではないため、
        // 通常のパス探索では解決できない。URP のシェーダーはこの形を多用する。
        if (CreatePackageResolver(options, editorData) is { } packages)
        {
            resolvers.Add(packages);
        }

        return new CompositeIncludeResolver([.. resolvers]);
    }

    /// <summary>
    /// 取り込んだファイルのうち、利用者が書いて直せるものを判定する関数を作る。
    /// </summary>
    /// <param name="options">コマンドライン引数の解析結果。</param>
    /// <param name="editorData">Unity Editor のデータフォルダー。</param>
    /// <returns>パスを受け取り、利用者のファイルなら <see langword="true"/> を返す関数。</returns>
    /// <remarks>
    /// <para>
    /// <b>直せないのは Unity と外部パッケージのヘッダだけである。</b>
    /// プロジェクトの <c>Library</c> (Package Manager が展開した <c>PackageCache</c>) と、
    /// Unity Editor に同梱のヘッダ (<c>CGIncludes</c> と同梱パッケージ) がそれに当たる。
    /// それ以外は利用者のファイルとする。<c>Assets</c> の下の共通の <c>.hlsl</c>、
    /// <c>Packages</c> に埋め込んだパッケージ、<c>file:</c> で参照するローカルのパッケージ、
    /// <c>--include-path</c> の下のヘッダである。
    /// </para>
    /// <para>
    /// 絶対パスでないもの (先に差し込むスタブなど) は利用者のファイルとしない。実在するファイルではない。
    /// </para>
    /// </remarks>
    public static Func<string, bool> CreateUserIncludePredicate(CommandLineOptions options, string? editorData)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> external = [];

        if (options.UnityProjectPath is { } project)
        {
            external.Add(Path.Combine(project, "Library"));
        }

        if (editorData is not null)
        {
            external.Add(editorData);
        }

        IncludeBoundary externalRoots = new(external);

        return path => Path.IsPathFullyQualified(path)
                       && !(externalRoots.IsRestricted && externalRoots.Allows(path));
    }

    /// <summary>
    /// <c>--define</c> の指定を既定のマクロへ重ねる。
    /// </summary>
    /// <param name="defines"><c>NAME</c> または <c>NAME=VALUE</c> 形式の指定。</param>
    /// <returns>定義済みマクロの表。</returns>
    /// <remarks>
    /// 値を省略した場合は <c>1</c> とみなす。C コンパイラの <c>-D</c> と同じ規則である。
    /// </remarks>
    public static ImmutableDictionary<string, string> BuildPredefinedMacros(ImmutableArray<string> defines)
    {
        ImmutableDictionary<string, string> macros = SemanticsOptions.DefaultPredefinedMacros;

        foreach (string define in defines)
        {
            int separator = define.IndexOf('=', StringComparison.Ordinal);

            (string name, string value) = separator < 0
                ? (define, "1")
                : (define[..separator], define[(separator + 1)..]);

            if (name.Length > 0)
            {
                macros = macros.SetItem(name, value);
            }
        }

        return macros;
    }
}
