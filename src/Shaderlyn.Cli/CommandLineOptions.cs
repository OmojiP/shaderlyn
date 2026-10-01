using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics.Profiles;

namespace Shaderlyn.Cli;

/// <summary>
/// 診断の出力形式。
/// </summary>
internal enum OutputFormat
{
    /// <summary>人間が読むための整形テキスト。既定値。</summary>
    Text,

    /// <summary>機械処理向けの JSON。</summary>
    Json,

    /// <summary>GitHub Code Scanning へアップロードするための SARIF 2.1.0。</summary>
    Sarif,

    /// <summary>
    /// GitHub Actions のワークフローコマンド形式。
    /// Code Scanning を有効化していないリポジトリでも PR にインライン注釈が出る。
    /// </summary>
    GitHub,
}

/// <summary>
/// コマンドライン引数の解析結果。
/// </summary>
/// <remarks>
/// <para>
/// 解析は失敗しうるので、コンストラクタではなく <see cref="TryParse"/> を通す。
/// 引数の誤りは例外ではなくエラーメッセージとして返し、呼び出し側が
/// <see cref="ExitCode.ToolError"/> で終了できるようにしている。
/// </para>
/// </remarks>
internal sealed class CommandLineOptions
{
    /// <summary>
    /// 解析対象として既定で探索するファイル拡張子。
    /// </summary>
    /// <remarks>
    /// 第一マイルストーンでは ShaderLab のファイルのみを対象とする。
    /// HLSL の単独ファイル (.hlsl / .cginc) は、include 解決を通じて
    /// ShaderLab 側から辿られる形で解析対象になる。
    /// </remarks>
    public static ImmutableArray<string> DefaultExtensions { get; } = [".shader", ".compute"];

    /// <summary>既定の設定で作る。外からは <see cref="TryParse"/> を通す。</summary>
    private CommandLineOptions()
    {
    }

    /// <summary>
    /// 適用するレンダーパイプラインプロファイルの名前。
    /// 指定が無い場合は <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 指定が無い場合は設定ファイルの値を使い、それも無ければ既定値を使う。
    /// </remarks>
    public string? Profile { get; private set; }

    /// <summary>
    /// 解析の中身を書き出すビューアーの出力先。指定が無い場合は <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 構文木・トークン・解決した型を 1 つの HTML にまとめて書き出す。
    /// </remarks>
    public string? InspectOutputPath { get; private set; }

    /// <summary>設定ファイルのパス。指定が無い場合は <see langword="null"/>。</summary>
    public string? ConfigPath { get; private set; }

    /// <summary>設定ファイルを探さない場合 <see langword="true"/>。</summary>
    public bool NoConfig { get; private set; }

    /// <summary>
    /// 出力するファイルパスを相対化する基点。
    /// </summary>
    /// <remarks>
    /// <b>SARIF と GitHub 形式では、これが正しくないと指摘が表示されない。</b>
    /// GitHub はリポジトリのルートからの相対パスでファイルを特定するため、
    /// 絶対パスのままではリポジトリ内のファイルへ対応づけられない。
    /// 対応づかない指摘は何も伝えられずに捨てられ、
    /// 「アップロードは成功したのに何も出ない」という分かりにくい失敗になる。
    /// <para>
    /// 省略時は現在の作業フォルダーを使う。
    /// GitHub Actions では既定でリポジトリのルートになる。
    /// </para>
    /// </remarks>
    public string? BasePath { get; private set; }

    /// <summary>
    /// 出力形式とは別に、ワークフローコマンドによる注釈も標準出力へ書くかどうか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--format sarif --output &lt;パス&gt; --annotate</c> という使い方を想定している。
    /// SARIF をファイルへ書きつつ、PR には即座に注釈を出す、という 2 つの目的を
    /// <b>1 回の解析で</b>果たすためのものである。
    /// </para>
    /// </remarks>
    public bool Annotate { get; private set; }

    /// <summary>
    /// 解析の内訳を標準エラーへ出すかどうか。
    /// </summary>
    /// <remarks>
    /// <b>速さを測るためのもの。</b>
    /// 標準出力ではなく標準エラーへ出すのは、
    /// JSON や SARIF の出力へ混ぜないためである。
    /// </remarks>
    public bool ReportTimings { get; private set; }

    /// <summary>
    /// 実行のあいだで結果を持ち越すキャッシュファイルのパス。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>指定されたときだけ働く。</b>
    /// </para>
    /// </remarks>
    public string? CachePath { get; private set; }

    /// <summary>照合に使うベースラインファイルのパス。</summary>
    public string? BaselinePath { get; private set; }

    /// <summary>ベースラインを書き出す先のパス。</summary>
    /// <remarks>
    /// 指定された場合、解析結果をベースラインとして書き出す。
    /// 既存の指摘を凍結して導入するための操作であり、
    /// このとき指摘があっても終了コードは 0 になる。
    /// </remarks>
    public string? WriteBaselinePath { get; private set; }

    /// <summary>
    /// Unity プロジェクトのルートフォルダー。
    /// </summary>
    /// <remarks>
    /// <b>利用者に明示的に指定してもらうものである。</b>
    /// URP のシェーダーは <c>Packages/com.unity.render-pipelines.universal/...</c> の形で
    /// ヘッダを参照しており、パッケージの実体はプロジェクト配下にある。
    /// 指定が無い場合に引けるのは、インストール済みの Editor に同梱されたパッケージだけになる。
    /// どちらからも引けなければ uniform の一覧が不完全になり、
    /// 誤検出を避けるためにルールが自ら検査を見送る (<c>SL0002</c>)。
    /// <c>#include</c> を 1 つも解決できなかった場合は <c>TOOL0004</c> のエラーになる。
    /// </remarks>
    public string? UnityProjectPath { get; private set; }

    /// <summary>
    /// Unity Editor のデータフォルダー。
    /// </summary>
    /// <remarks>
    /// Unity 同梱のヘッダ (<c>CGIncludes</c>) と、Editor に同梱された
    /// URP / HDRP のパッケージ (<c>BuiltInPackages</c>) はここから引く。
    /// 省略した場合、<see cref="UnityProjectPath"/> の
    /// <c>ProjectSettings/ProjectVersion.txt</c> から推定を試み、
    /// プロジェクトの指定も無ければインストール済みで最も新しいものを使う。
    /// </remarks>
    public string? UnityEditorDataPath { get; private set; }

    /// <summary><c>#include</c> の探索パス。</summary>
    public ImmutableArray<string> IncludePaths { get; private set; } = [];

    /// <summary>
    /// 解析時に定義済みとするマクロ。<c>NAME</c> または <c>NAME=VALUE</c> の形。
    /// </summary>
    /// <remarks>
    /// 展開は単一構成でしか行わないため、どの構成で解析するかをここで選ぶ。
    /// </remarks>
    public ImmutableArray<string> Defines { get; private set; } = [];

    /// <summary>
    /// 展開するシンボルバリアントの上限。省略した場合は既定値。
    /// </summary>
    /// <remarks>
    /// <b>上げると見落としは減り、解析時間は伸びる。</b>
    /// 宣言されたシンボルはどれも C# から切り替えられるため、
    /// 展開しなかった経路は一度も読まれない。
    /// 調べきれなかったことは <c>SL0003</c> として報告する。
    /// </remarks>
    public int? MaxSymbolVariants { get; private set; }

    /// <summary>解析対象のファイルまたはフォルダーのパス。</summary>
    public ImmutableArray<string> InputPaths { get; private set; } = [];

    /// <summary>診断の出力形式。</summary>
    public OutputFormat Format { get; private set; }

    /// <summary>
    /// この重要度以上の指摘があれば <see cref="ExitCode.DiagnosticsFound"/> を返す、という閾値。
    /// </summary>
    public DiagnosticSeverity ErrorOn { get; private set; } = DiagnosticSeverity.Warning;

    /// <summary>出力先ファイルのパス。<see langword="null"/> の場合は標準出力へ書く。</summary>
    public string? OutputPath { get; private set; }

    /// <summary>実装されている全ルールを一覧表示するかどうか。</summary>
    public bool ListRules { get; private set; }

    /// <summary>
    /// 解析はせずに、解析が使う環境 (設定ファイル・Unity のインストール先・パッケージの解決先) を表示するかどうか。
    /// </summary>
    public bool ShowEnvironment { get; private set; }

    /// <summary>使い方を表示するかどうか。</summary>
    public bool ShowHelp { get; private set; }

    /// <summary>バージョンを表示するかどうか。</summary>
    public bool ShowVersion { get; private set; }

    /// <summary>
    /// 値を取らないオプションと、それが立てる設定。
    /// </summary>
    private static readonly FrozenDictionary<string, Action<CommandLineOptions>> Flags =
        new Dictionary<string, Action<CommandLineOptions>>(StringComparer.Ordinal)
        {
            ["-h"] = o => o.ShowHelp = true,
            ["--help"] = o => o.ShowHelp = true,
            ["--version"] = o => o.ShowVersion = true,
            ["--list-rules"] = o => o.ListRules = true,
            ["--no-config"] = o => o.NoConfig = true,
            ["--annotate"] = o => o.Annotate = true,
            ["--timings"] = o => o.ReportTimings = true,
            ["--env"] = o => o.ShowEnvironment = true,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// 値を取るオプションと、その値の受け取り方。
    /// </summary>
    /// <remarks>
    /// 受け取り方は、値が不正ならエラーメッセージを、受け取れたら <see langword="null"/> を返す。
    /// 値が無いことは表を引く側で先に判定するので、ここに来る値は必ずある。
    /// </remarks>
    private static readonly FrozenDictionary<string, Func<CommandLineOptions, string, string?>> ValueOptions =
        new Dictionary<string, Func<CommandLineOptions, string, string?>>(StringComparer.Ordinal)
        {
            ["--format"] = ReadFormat,
            ["--error-on"] = ReadErrorOn,
            ["-o"] = (o, v) => Set(() => o.OutputPath = v),
            ["--output"] = (o, v) => Set(() => o.OutputPath = v),
            ["--profile"] = ReadProfile,
            ["--max-symbol-variants"] = ReadMaxSymbolVariants,
            ["--unity-project"] = (o, v) => Set(() => o.UnityProjectPath = v),
            ["--unity-editor"] = (o, v) => Set(() => o.UnityEditorDataPath = v),
            ["--include-path"] = (o, v) => Set(() => o.IncludePaths = o.IncludePaths.Add(v)),
            ["--define"] = (o, v) => Set(() => o.Defines = o.Defines.Add(v)),
            ["--config"] = (o, v) => Set(() => o.ConfigPath = v),
            ["--baseline"] = (o, v) => Set(() => o.BaselinePath = v),
            ["--write-baseline"] = (o, v) => Set(() => o.WriteBaselinePath = v),
            ["--base-path"] = (o, v) => Set(() => o.BasePath = v),
            ["--cache"] = (o, v) => Set(() => o.CachePath = v),
            ["--inspect"] = (o, v) => Set(() => o.InspectOutputPath = v),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// コマンドライン引数を解析する。
    /// </summary>
    /// <param name="args">解析する引数。</param>
    /// <param name="options">解析に成功した場合の結果。</param>
    /// <param name="error">解析に失敗した場合のエラーメッセージ。</param>
    /// <returns>解析に成功した場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>オプションは表 (<see cref="Flags"/> / <see cref="ValueOptions"/>) で引く。</b>
    /// 値の取り出しと失敗したときの後始末をオプションごとに書くと、
    /// 足すたびに同じ手順を書き写すことになり、書き忘れた 1 つだけが壊れる。
    /// </remarks>
    public static bool TryParse(
        string[] args,
        [NotNullWhen(true)] out CommandLineOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        CommandLineOptions parsed = new() { ShowHelp = args.Length == 0 };

        error = Read(parsed, args);

        if (error is null
            && parsed.InputPaths.IsEmpty
            && !parsed.ShowHelp
            && !parsed.ShowVersion
            && !parsed.ListRules
            && !parsed.ShowEnvironment)
        {
            error = "解析対象のパスが指定されていません。ファイルまたはフォルダーを 1 つ以上指定してください。";
        }

        options = error is null ? parsed : null;
        return error is null;
    }

    /// <summary>引数を先頭から読み、設定に反映する。</summary>
    /// <param name="parsed">反映する先。</param>
    /// <param name="args">全引数。</param>
    /// <returns>エラーメッセージ。読み切れた場合は <see langword="null"/>。</returns>
    private static string? Read(CommandLineOptions parsed, string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (Flags.TryGetValue(arg, out Action<CommandLineOptions>? flag))
            {
                flag(parsed);
                continue;
            }

            if (ValueOptions.TryGetValue(arg, out Func<CommandLineOptions, string, string?>? read))
            {
                if (i + 1 >= args.Length)
                {
                    return $"オプション '{arg}' には値が必要です。";
                }

                if (read(parsed, args[++i]) is { } invalid)
                {
                    return invalid;
                }

                continue;
            }

            if (arg.StartsWith('-'))
            {
                return $"不明なオプション '{arg}' が指定されました。--help で使い方を確認してください。";
            }

            parsed.InputPaths = parsed.InputPaths.Add(arg);
        }

        return null;
    }

    /// <summary>そのまま受け取る値を反映する。</summary>
    /// <param name="assign">反映する操作。</param>
    /// <returns>常に <see langword="null"/> (受け取れた)。</returns>
    private static string? Set(Action assign)
    {
        assign();
        return null;
    }

    /// <summary><c>--format</c> の値を読む。</summary>
    /// <param name="parsed">反映する先。</param>
    /// <param name="value">指定された値。</param>
    /// <returns>エラーメッセージ。受け取れた場合は <see langword="null"/>。</returns>
    private static string? ReadFormat(CommandLineOptions parsed, string value)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out OutputFormat format))
        {
            return $"--format の値 '{value}' は不正です。text, json, sarif, github のいずれかを指定してください。";
        }

        parsed.Format = format;
        return null;
    }

    /// <summary><c>--error-on</c> の値を読む。</summary>
    /// <param name="parsed">反映する先。</param>
    /// <param name="value">指定された値。</param>
    /// <returns>エラーメッセージ。受け取れた場合は <see langword="null"/>。</returns>
    private static string? ReadErrorOn(CommandLineOptions parsed, string value)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out DiagnosticSeverity severity)
            || severity == DiagnosticSeverity.None)
        {
            return $"--error-on の値 '{value}' は不正です。info, warning, error のいずれかを指定してください。";
        }

        parsed.ErrorOn = severity;
        return null;
    }

    /// <summary><c>--profile</c> の値を読む。</summary>
    /// <param name="parsed">反映する先。</param>
    /// <param name="value">指定された値。</param>
    /// <returns>エラーメッセージ。受け取れた場合は <see langword="null"/>。</returns>
    private static string? ReadProfile(CommandLineOptions parsed, string value)
    {
        if (!RenderPipelineProfiles.TryGet(value, out _))
        {
            return $"--profile の値 '{value}' は不正です。"
                + $"{RenderPipelineProfiles.GetAvailableNames()} のいずれかを指定してください。";
        }

        parsed.Profile = value;
        return null;
    }

    /// <summary><c>--max-symbol-variants</c> の値を読む。</summary>
    /// <param name="parsed">反映する先。</param>
    /// <param name="value">指定された値。</param>
    /// <returns>エラーメッセージ。受け取れた場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 負の数は「上限なし」に見えるが、そうは動かない。
    /// 意図と挙動がずれる指定は、何も伝えずに丸めることはせず断る。
    /// </remarks>
    private static string? ReadMaxSymbolVariants(CommandLineOptions parsed, string value)
    {
        if (!int.TryParse(value, out int count) || count < 0)
        {
            return $"--max-symbol-variants の値 '{value}' は不正です。0 以上の整数を指定してください。";
        }

        parsed.MaxSymbolVariants = count;
        return null;
    }

    /// <summary>使い方の説明文を返す。</summary>
    /// <returns>標準出力へ表示する説明文。</returns>
    public static string GetUsage() =>
        """
        shaderlyn - Unity ShaderLab / HLSL 静的解析ツール

        使い方:
          shaderlyn <パス...> [オプション]

        引数:
          <パス...>              解析対象のファイルまたはフォルダー。
                                 フォルダーを指定すると再帰的に .shader と .compute を探索します。
                                 .hlsl と .cginc は単体では成立しない断片であることが多いため
                                 探索しません。名前を挙げて指定すれば解析します。

        オプション:
          --format <形式>        出力形式: text (既定), json, sarif, github
          --error-on <重要度>    この重要度以上の指摘で終了コード 1 を返す: info, warning (既定), error
          -o, --output <パス>    出力先ファイル。省略時は標準出力へ書きます。
          --base-path <パス>     出力するパスを相対化する基点。省略時は現在のフォルダー。
                                 sarif / github 形式では、これがリポジトリのルートを指していないと
                                 GitHub 上で指摘がファイルに対応づきません。
          --annotate             出力形式とは別に、ワークフローコマンドによる注釈も標準出力へ書きます。
                                 --format sarif --output <パス> --annotate とすることで、
                                 1 回の解析で SARIF の生成と PR への注釈を同時に行えます。
          --timings              解析の内訳 (構文解析・展開・ルール) を標準エラーへ出します。
                                 どこに時間が行っているかを測るためのものです。
          --list-rules           実装されている全ルールを一覧表示します。
          --version              バージョンを表示します。
          -h, --help             このヘルプを表示します。

        意味解析のオプション:
          --unity-project <パス> Unity プロジェクトのルート。パッケージ配下のヘッダの解決に使います。
                                 明示的に指定してください。
          --unity-editor <パス>  Unity Editor のデータフォルダー。Unity 同梱のヘッダを引きます。
                                 省略時は ProjectSettings/ProjectVersion.txt から推定し、
                                 --unity-project も無ければインストール済みの最新バージョンを使います。
          --include-path <パス>  #include の追加探索パス。複数回指定できます。
          --profile <名前>       レンダーパイプライン: urp (既定), brp, hdrp
          --define <名前[=値]>   定義済みマクロ。複数回指定できます。
          --max-symbol-variants <数>
                                 #pragma shader_feature / multi_compile で宣言された
                                 シンボルを 1 つずつ有効にして解析する上限 (既定 8)。
                                 上げると見落としは減り、解析時間は伸びます。
                                 調べきれなかった経路は SL0003 として報告されます。

        設定ファイルとベースライン:
          --config <パス>        設定ファイル。省略時は解析対象から上へ .shaderlyn.yaml を探します。
          --no-config            設定ファイルを探しません。
          --baseline <パス>      ベースラインに記録済みの指摘を除外します。
          --write-baseline <パス>
                                 現在の指摘をベースラインとして書き出します。
                                 既存プロジェクトへ導入する際に、既存の指摘を凍結する操作です。

        2 回目以降を速くする:
          --cache <パス>         前回の結果をこのファイルへ残し、次回に使い回します。
                                 シェーダー自身と、それが取り込んだヘッダのすべてが
                                 前回と同じ内容のときだけ使い回します。
                                 設定・ルール・このツール自体が変われば全件を解析し直します。
                                 指定しなければ何も作りません。消せば必ず元の挙動に戻ります。
                                 マシンごとのものなので、バージョン管理へは入れないでください。

        解析の中身を見る:
          --env                  解析はせずに、解析が使う環境を表示します。
                                 見つけた設定ファイル、Unity Editor とその決め方、#include を探す順序、
                                 パッケージの解決先、解析対象に書かれた #include を解決できるかを確かめます。
                                 指摘が出ないときに、ヘッダが読めていないのかを切り分けるための機能です。
          --inspect <パス>       構文木・トークン・解決した型を 1 つの HTML に書き出します。
                                 対象は 1 ファイルに限ります。指摘は出力しません。
                                 誤検出の原因が構文解析・マクロ展開・型判定のどれかを
                                 切り分けるための機能です。

        プロパティと HLSL の対応検査 (SL1001 / SL1003 など) は、埋め込みコードの
        依存関係を解決できて初めて働きます。見送ったことは SL0002 として報告されます。
        --unity-project の指定が無く Unity Editor も見つからないなど、ヘッダを
        1 つも解決できない場合は TOOL0004 のエラーになります。

        指摘を個別に抑制するには、コードにコメントを書きます。
          // shaderlyn-disable-next-line SL1003
          // shaderlyn-disable-line SL1003
          // shaderlyn-disable SL1003  …  // shaderlyn-enable SL1003
          // shaderlyn-disable-file SL1003
        ルール ID を省略すると、すべてのルールが対象になります。

        終了コード:
          0  閾値以上の指摘なし
          1  閾値以上の指摘あり
          2  ツールの実行に失敗 (引数の誤り、ファイル読み込み失敗など)
        """;
}
