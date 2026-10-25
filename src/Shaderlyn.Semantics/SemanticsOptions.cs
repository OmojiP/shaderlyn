using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Analysis;

namespace Shaderlyn.Semantics;

/// <summary>
/// 意味解析の実行時設定。
/// </summary>
/// <remarks>
/// <para>
/// 設定ファイルの書式からは独立している。
/// <c>.shaderlyn.yaml</c> の読み込みは CLI 層の責務であり、
/// 読み込み結果をこの型へ詰め替えて渡す。
/// </para>
/// <para>
/// <b>record にしてあるのは、一部だけを差し替えた複製を <c>with</c> で作るためである。</b>
/// エディタでは、ファイルごとに定義済みマクロを足した設定を作る。
/// 項目を 1 つずつ書き写すと、項目が増えたときにそこだけ古いままになる。
/// </para>
/// </remarks>
public sealed record SemanticsOptions
{
    /// <summary>既定の設定。</summary>
    public static SemanticsOptions Default { get; } = new();

    /// <summary>
    /// <c>#include</c> を解決する仕組み。<see langword="null"/> の場合、include は解決されない。
    /// </summary>
    /// <remarks>
    /// 解決できない include があると uniform の一覧が不完全になる。
    /// その場合に何が起きるかは <see cref="ShaderCompilation.HasCompleteDependencies"/> を参照。
    /// </remarks>
    public IIncludeResolver? IncludeResolver { get; init; }

    /// <summary>
    /// 解析開始時に定義済みとするマクロ。<see langword="null"/> (既定) なら環境の既定を使う。
    /// </summary>
    /// <remarks>
    /// Unity の環境は、D3D11 向けの代表的な構成 (<c>SHADER_API_D3D11</c> など) を既定にする。
    /// 指定した場合は、環境の既定に重ねずにそのまま使う。
    /// </remarks>
    public ImmutableDictionary<string, string>? PredefinedMacros { get; init; }

    /// <summary>
    /// 構成ごとに展開し直すのではなく、両方の分岐を 1 つの木に残すシンボル。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ここに入れたシンボルは<b>バリアントとして展開し直さない</b>。
    /// 代わりに、その <c>#ifdef</c> の両方の分岐を 1 本のトークン列へ並べ、
    /// どちらの条件のもとにあるかを構文木の脇に記録する。
    /// </para>
    /// <para>
    /// <b>並べられない領域では、今までどおりバリアントとして展開する。</b>
    /// マクロや取り込みを切り替えている領域、構文の単位で閉じていない領域、
    /// 入れ子の中は並べられない。
    /// その場合このシンボルはバリアントの対象に戻る。
    /// </para>
    /// <para>
    /// 3 つの状態を表す。
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see langword="null"/> (既定) … 各ブロックの <c>#pragma</c> から自動で拾う
    ///   </description></item>
    ///   <item><description>空 … 1 つも残さない。構成ごとの展開だけで解析する</description></item>
    ///   <item><description>指定あり … そのシンボルだけを対象にする</description></item>
    /// </list>
    /// </remarks>
    public ImmutableHashSet<string>? BothBranchSymbols { get; init; }

    /// <summary>
    /// 取り込んだファイルが、利用者が書いて直せるファイルかを、そのパスから判定する。
    /// <see langword="null"/> (既定) なら、解析しているファイルだけを利用者のファイルとする。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>利用者のファイルは、解析しているファイルと同じく指摘の対象になる。</b>
    /// 共通の <c>.hlsl</c> を取り込むのはよくある書き方であり、その中身も利用者が直すコードである。
    /// 関数の中身まで構文木にし、取り込む側の文脈で検査する (<see cref="ShaderCompilation.IsReportable(CFamily.Syntax.ShaderSyntaxNode)"/>)。
    /// </para>
    /// <para>
    /// 直せないのは Unity や外部パッケージのヘッダ (<c>Library/PackageCache</c> や Unity Editor に同梱のもの) である。
    /// どれがそうかは、探索パスを組み立てた呼び出し側が知っている。CLI と言語サーバーはそれらの外を利用者のファイルとする。
    /// </para>
    /// </remarks>
    public Func<string, bool>? IsUserInclude { get; init; }

    /// <summary>
    /// 解決できなかった <c>#include</c> について、理由と直し方を述べる文を作る。<see langword="null"/> (既定) か、
    /// 返した文が <see langword="null"/> なら、ルールが既定の文を使う。
    /// </summary>
    /// <remarks>
    /// <b>理由を知っているのは、探索パスと読んでよい範囲を組み立てた呼び出し側である。</b>
    /// CLI は、<c>--unity-project</c> を付けていないために引けない <c>Assets/...</c> などに、見つけたプロジェクトを添える。
    /// </remarks>
    public Func<IncludeReference, string?>? DescribeUnresolvedInclude { get; init; }

    /// <summary>
    /// 字句解析の結果を共有するキャッシュ。
    /// </summary>
    /// <remarks>
    /// <b>複数のファイルを解析する場合は必ず指定すること。</b>
    /// 指定しないと、同じ URP のヘッダ群を Pass ごと・ファイルごとに字句解析し直すことになる。
    /// </remarks>
    public TokenCache? TokenCache { get; init; }

    /// <summary>
    /// 取り込みの展開結果を共有するキャッシュ。
    /// </summary>
    /// <remarks>
    /// <b>複数のファイルを解析する場合は必ず指定すること。</b>
    /// 指定しないと、同じヘッダを同じ状態で Pass ごと・ファイルごとに展開し直すことになる。
    /// </remarks>
    public IncludeCache? IncludeCache { get; init; }

    /// <summary>
    /// 構文木を作る段ごとの時間を受け取る先。<see langword="null"/> の場合は測らない。
    /// </summary>
    /// <remarks>
    /// 展開・構文解析・親の設定のどれが重いかで、次に直す場所が変わる。
    /// </remarks>
    public ISyntaxTimingRecorder? SyntaxTimings { get; init; }

    /// <summary>
    /// 1 つのコードブロックについて、追加で展開するシンボル構成の上限。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>シンボルの組み合わせをすべて展開することはできない。</b>
    /// Unity 同梱の URP シェーダーは 1 ファイルで 40 を超えるシンボルを宣言しており、
    /// 組み合わせは 2 の 40 乗になる。
    /// </para>
    /// <para>
    /// 代わりに<b>そのファイル自身が条件で見ているシンボル</b>だけを対象にし、
    /// 1 つずつ有効にした構成を追加で展開する。
    /// URP 同梱の 70 件で数えると、自分の条件でシンボルを見ているのは 28 件だけで、
    /// そのうち 25 件は 3 個以下である。この絞り込みで現実的な回数に収まる。
    /// </para>
    /// <para>
    /// それでも上限は要る。上限に達したことは報告する (CF0102)。
    /// 何も伝えずに一部だけ調べると、調べていない箇所を「問題なし」と受け取られる。
    /// </para>
    /// </remarks>
    public int MaxSymbolVariants { get; init; } = DefaultMaxSymbolVariants;

    /// <summary>
    /// シンボルの構成を <see cref="PredefinedMacros"/> で 1 つに固定して解析するかどうか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>固定すると、シンボルで外れた分岐はこの構成ではコンパイルされないコードになる。</b>
    /// 既定では、どの構成でも解析しなかった分岐に名前があれば「見えていないだけかもしれない」として、
    /// 「無いこと」を根拠にする検査を見送る (<see cref="ShaderCompilation.UnanalyzedIdentifiers"/>)。
    /// 構成を固定した解析では、その分岐は別の構成の話なので、見送る理由にならない。
    /// </para>
    /// <para>
    /// 対象はシンボルだけで外れた分岐である。<c>SHADER_API_*</c> のような環境の条件で外れた分岐は、
    /// 構成を固定しても値が分からないので、今までどおり見送る。
    /// </para>
    /// <para>
    /// エディタの「選んだ構成で表示」と、すべての構成を 1 つずつ解析する検証が使う。
    /// 両方の分岐を並べず (<see cref="BothBranchSymbols"/> を空に)、
    /// バリアントも作らない (<see cref="MaxSymbolVariants"/> を 0 に) 設定と組み合わせる。
    /// </para>
    /// </remarks>
    public bool FixedSymbolConfiguration { get; init; }

    /// <summary>
    /// 条件によって中身が変わるマクロを使っている文と、文の途中で分かれる <c>#if</c> を含む文を、
    /// 定義・分岐ごとに複製するかどうか。既定は有効。
    /// </summary>
    /// <remarks>
    /// SuperC の条件の巻き上げを文・宣言の単位で行う
    /// (<see cref="PreprocessorOptions.HoistConditionalMacros"/>)。
    /// 無効にすると、その形は構成ごとの展開で扱う。
    /// </remarks>
    public bool HoistConditionalMacros { get; init; } = true;

    /// <summary>
    /// 取り込みを切り替えている分岐も、両方の分岐を並べるかどうか。既定は有効。
    /// </summary>
    /// <remarks>
    /// 両方のヘッダを、それぞれの条件のもとで処理する
    /// (<see cref="PreprocessorOptions.MergeSwitchedIncludes"/>)。
    /// 無効にすると、その形は構成ごとの展開で扱う。
    /// </remarks>
    public bool MergeSwitchedIncludes { get; init; } = true;

    /// <summary>
    /// 互いに関係しないキーワードを、1 つの構成でまとめて有効にして展開するかどうか。既定は有効。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>展開の回数を減らし、上限 (<see cref="MaxSymbolVariants"/>) に届きにくくする。</b>
    /// まとめた構成の木と既定の木との違いは、違いを囲む <c>#if</c> の連なりが見ているキーワードで、
    /// それぞれどのキーワードのものかを決める。
    /// </para>
    /// <para>
    /// まとめるのは、分岐の中でマクロを定義・削除せず、ヘッダも取り込まないキーワードだけである。
    /// そうでないキーワードは、有効にすると連なりの外の展開まで変わりうる。
    /// まとめた構成で 1 つでも違いのキーワードを決められなければ、その構成は捨てて 1 つずつ展開し直す。
    /// </para>
    /// <para>
    /// まとめた木は、まとめたキーワードをすべて有効にした 1 つの構成である。
    /// あるキーワードだけのときに壊れるコードを、ほかのキーワードが隠さないように、
    /// 違いが別々のトップレベルの宣言にあり、一方が宣言する名前をもう一方が使わない組だけをまとめる。
    /// </para>
    /// </remarks>
    public bool PackIndependentSymbols { get; init; } = true;

    /// <summary><see cref="MaxSymbolVariants"/> の既定値。</summary>
    /// <remarks>
    /// URP 同梱の 70 件では、条件でシンボルを見ているシェーダーの
    /// ほとんどが 3 個以下だった。8 はその裾を十分に覆う。
    /// </remarks>
    public const int DefaultMaxSymbolVariants = 8;
}
