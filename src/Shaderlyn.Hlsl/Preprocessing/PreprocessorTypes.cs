using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// プリプロセッサの実行時設定。
/// </summary>
/// <remarks>
/// <para>
/// <b>単一構成での展開である。</b>
/// <c>#pragma multi_compile</c> が生む組み合わせをすべて解析するのではなく、
/// <see cref="PredefinedMacros"/> で与えられた 1 つの構成だけを展開する。
/// 組み合わせはすぐに数百・数千になる。キーワードの分岐は <see cref="BothBranchSymbols"/> で両方を並べ、
/// 並べられなかった分は呼び出し側がキーワードを有効にした構成として展開し直す。
/// </para>
/// </remarks>
public sealed record PreprocessorOptions
{
    /// <summary>include の入れ子の上限。</summary>
    /// <remarks>
    /// 循環 include は個別に検出するが、それとは別に深さの上限を設ける。
    /// 循環していなくても病的に深い include 連鎖があれば解析が終わらなくなるためである。
    /// </remarks>
    public const int DefaultMaxIncludeDepth = 64;

    /// <summary>マクロ展開の上限段数。</summary>
    /// <remarks>
    /// 相互再帰するマクロを完全に検出するのは難しいため、
    /// 段数の上限で最終的な歯止めをかける。
    /// </remarks>
    public const int DefaultMaxExpansionDepth = 128;

    /// <summary>既定の設定。</summary>
    public static PreprocessorOptions Default { get; } = new();

    /// <summary>解析開始時に定義済みとするマクロ。名前から値への対応。</summary>
    public ImmutableDictionary<string, string> PredefinedMacros { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// 定義済みか否かを決めつけず、両方の分岐を残すシンボル。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ここに入れたシンボルの <c>#ifdef</c> は、どちらの分岐も捨てない。</b>
    /// 両方の分岐のトークンを 1 本の列に並べ、
    /// どちらの条件のもとにあるかを <c>ConditionalRegions</c> に記録する。
    /// シェーダーのシンボルは C# からも切り替えられるため、
    /// どちらの経路もいつか通るコードである。
    /// </para>
    /// <para>
    /// <b>並べてよい形かは領域ごとに判定する。</b>
    /// ここに入れても、その領域がマクロを切り替えていたり
    /// 構文の単位で閉じていなければ、今までどおり片方だけを残す
    /// (<see cref="ConditionalRegionScanner"/>)。
    /// </para>
    /// <para>
    /// 既定は空である。空の場合の挙動は、この設定が無かったときと 1 か所も変わらない。
    /// </para>
    /// </remarks>
    public ImmutableHashSet<string> BothBranchSymbols { get; init; } =
        ImmutableHashSet<string>.Empty;

    /// <summary>
    /// 条件の中で、構成によって値が変わるものとして読むシンボル。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>同時に定義されている必要がある組を知るために使う</b>
    /// (<c>RequiredSymbolCombinations</c>)。
    /// <c>#ifdef _A</c> の中の <c>#ifdef _B</c> は、<c>_A</c> と <c>_B</c> の両方を有効にした構成でしか通らない。
    /// 外側を読み飛ばした場所でも、入れ子の条件を読んで組を記録する。
    /// </para>
    /// <para>
    /// <see cref="BothBranchSymbols"/> と分けているのは、並べるかどうかと関係なく組が要るためである。
    /// 並べない設定で展開しても、組の記録は変わらない。
    /// </para>
    /// <para>
    /// 解析しているファイル自身の条件にだけ効き、取り込んだファイルの展開結果は変えない。
    /// そのため <see cref="FingerprintForCache"/> には入れない。
    /// 既定は空で、組を記録しない。
    /// </para>
    /// </remarks>
    public ImmutableHashSet<string> DeclaredSymbols { get; init; } =
        ImmutableHashSet<string>.Empty;

    /// <summary>
    /// 取り込んだファイルに書かれた条件でも、構成によって値が変わるものとして読むシンボル。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ヘッダ自身が <c>#pragma multi_compile</c> で宣言したシンボルのためにある。</b>
    /// 共通の関数を <c>Common.hlsl</c> に切り出し、そこに宣言も置く書き方では、
    /// 解析しているファイルにそのシンボルの条件が 1 つも書かれていない。
    /// ヘッダの条件を読まなければ、そのシンボルで切り替わる宣言は既定の構成の側しか見えず、
    /// もう一方でしか宣言されていない関数の呼び出しを「どこにも無い」ことにしてしまう。
    /// </para>
    /// <para>
    /// <b>解析しているファイルが宣言したシンボルは入れない。</b>
    /// URP のシェーダーは 40 を超えるシンボルを宣言し、その条件はほとんどがヘッダ側に書かれている。
    /// すべてを読むと、構成として作る候補が 1 ブロックあたり数十件になり、
    /// 上限で落ちた分が <c>SL0003</c> として並ぶ。
    /// 解析しているファイルが宣言したシンボルは、そのファイルに書かれた条件から構成を決める
    /// (<see cref="DeclaredSymbols"/>)。
    /// </para>
    /// <para>
    /// ここに入れたシンボルは取り込んだファイルの展開結果を変えるので、
    /// <see cref="FingerprintForCache"/> にも入れる。既定は空で、ヘッダの条件は読まない。
    /// </para>
    /// </remarks>
    public ImmutableHashSet<string> IncludedDeclaredSymbols { get; init; } =
        ImmutableHashSet<string>.Empty;

    /// <summary>
    /// 構成によって定義が変わるシンボル。<c>#pragma multi_compile</c> などで宣言されたもの。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>条件が構成によって変わるかどうかを決めるのに使う。</b>
    /// この名前か、構成によって定義が変わるマクロを参照する条件は、構成によって結果が変わる。
    /// それ以外の条件 (インクルードガード、<c>SHADER_API_*</c>、<c>SHADER_TARGET</c>) は
    /// 単一構成での値がそのまま全構成の値である。
    /// </para>
    /// <para>
    /// <see cref="DeclaredSymbols"/>・<see cref="BothBranchSymbols"/>・<see cref="IncludedDeclaredSymbols"/> も
    /// あわせて構成によって変わるものとして扱う。ここに置くのは、それらから外れたシンボルのためである
    /// (構成ごとの展開では <see cref="DeclaredSymbols"/> を空にし、
    /// コードで使われたマクロのシンボルは <see cref="BothBranchSymbols"/> から外す)。
    /// </para>
    /// </remarks>
    public ImmutableHashSet<string> ConfigurationSymbols { get; init; } =
        ImmutableHashSet<string>.Empty;

    /// <summary>
    /// シンボルの宣言から分かる、構成の制約。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>決して成り立たない分岐は、並べず、組としても記録しない。</b>
    /// <c>#pragma multi_compile _ _X _Y</c> の <c>_X</c> と <c>_Y</c> は同時に有効にならないので、
    /// <c>#ifdef _X</c> の中の <c>#ifdef _Y</c> はどの構成でも通らない。
    /// <c>#pragma multi_compile MODE_A MODE_B</c> ではどちらかが必ず有効なので、
    /// <c>#if !defined(MODE_A) &amp;&amp; !defined(MODE_B)</c> の中も通らない。
    /// そこを検査すると、出荷されないコードの誤りを報告することになる。
    /// </para>
    /// <para>
    /// どれか 1 つが必ず有効な行の既定の構成は、ここではなく <see cref="PredefinedMacros"/> で与える。
    /// 既定は空で、制約を置かない。
    /// </para>
    /// </remarks>
    public SymbolConstraints SymbolConstraints { get; init; } = SymbolConstraints.Empty;

    /// <summary>
    /// 条件によって中身が変わるマクロをコードで使っている文を、定義ごとに複製するかどうか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// SuperC の条件の巻き上げ (hoisting) を、文・宣言の単位で行うものである
    /// (Gazzillo &amp; Grimm, PLDI 2012)。
    /// <c>#ifdef _A</c> で <c>#define CTYPE float3</c>、<c>#else</c> で <c>float4</c> と定義したマクロを
    /// <c>CTYPE color = ...;</c> と使っている場合、その文を定義ごとに展開し直し、
    /// それぞれに条件を付けて 1 本のトークン列に並べる。
    /// </para>
    /// <para>
    /// 文の切れ目の候補は字句で出し、採否は構文解析 (<c>HlslParser.IsCompleteUnits</c>) が決める。
    /// どの候補でも文として読めなければ複製しない。文の範囲を取り違えると構文木が壊れるためである。
    /// </para>
    /// <para>
    /// この型の既定値は無効である。セマンティックモデルを組むときは
    /// <c>SemanticsOptions.HoistConditionalMacros</c> (既定で有効) の値が入る。
    /// 有効でも、並べる相手がいないとき (<see cref="BothBranchSymbols"/> が空のとき) は複製しない。
    /// </para>
    /// </remarks>
    public bool HoistConditionalMacros { get; init; }

    /// <summary>
    /// 取り込みを切り替えている分岐も、両方の分岐を並べるかどうか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>両方のヘッダを、それぞれの条件のもとで処理する。</b>
    /// SuperC も、条件の中の <c>#include</c> はその条件のもとでヘッダを処理する
    /// (Gazzillo &amp; Grimm, PLDI 2012)。
    /// </para>
    /// <para>
    /// ヘッダが定義したマクロも、並べた分岐の定義として覚える。
    /// それがコードとして展開されたら、そのシンボルを並べずに展開し直す
    /// (<c>MergedMacroConflicts</c>)。
    /// </para>
    /// <para>
    /// この型の既定値は無効である。セマンティックモデルを組むときは
    /// <c>SemanticsOptions.MergeSwitchedIncludes</c> (既定で有効) の値が入る。
    /// </para>
    /// </remarks>
    public bool MergeSwitchedIncludes { get; init; }

    /// <summary>
    /// 並べてよい領域の位置。<see langword="null"/> なら展開しながら決める。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>バリアントは、既定の構成が並べた領域だけを並べる。</b>
    /// 並べてよいかはマクロの状態にも依存するため (<c>CallsConfigurationDependentMacro</c>)、
    /// 構成が違えば判断も変わりうる。変わると、既定の木とバリアントの違いが
    /// 「そのシンボルで変わる部分」だけではなくなり、突き合わせが無関係な条件を付ける。
    /// </para>
    /// <para>
    /// <b>取り込んだヘッダの領域も含める。</b>
    /// 並べるかの判断はマクロの状態で決まるので、ヘッダの中でも構成ごとに食い違いうる。
    /// 既定の構成が並べなかった領域をバリアントが並べると、その中身はバリアントの木にしか無いことになり、
    /// 有効にしたキーワードとは関係の無い条件が付く。
    /// </para>
    /// <para>
    /// 位置は <c>#if</c> 系の指令名のトークンの、ファイルと開始位置である
    /// (<c>PreprocessResult.MergedRegions</c>)。
    /// </para>
    /// </remarks>
    public MergedRegionSet? MergeOnlyRegions { get; init; }

    /// <summary>include を解決する仕組み。<see langword="null"/> の場合、include は解決されない。</summary>
    public IIncludeResolver? IncludeResolver { get; init; }

    /// <summary>
    /// 対象ファイルより先に処理するコード。<see langword="null"/> の場合は何も処理しない。
    /// </summary>
    /// <remarks>
    /// <para>
    /// C コンパイラの <c>-include</c> に相当する。
    /// <b>Unity をインストールしていない環境でも解析を成立させるための仕組みである。</b>
    /// <c>TEXTURE2D(_BaseMap)</c> や <c>CBUFFER_START(UnityPerMaterial)</c> は
    /// 展開されなければ宣言として成立せず、uniform を 1 つも取り出せない。
    /// </para>
    /// <para>
    /// 先頭に差し込むコードで定義したマクロは、後から本物のヘッダが同じ名前を定義すれば上書きされる。
    /// つまり<b>実物があるときは必ず実物が勝つ</b>ので、
    /// 先頭に差し込むコードは「実物が無かった場合の代替」としてのみ働く。
    /// </para>
    /// </remarks>
    public SourceText? Prelude { get; init; }

    /// <summary>
    /// 字句解析の結果を共有するキャッシュ。<see langword="null"/> の場合は毎回字句解析する。
    /// </summary>
    /// <remarks>
    /// 同じヘッダを何度も取り込む場合、解析時間が大きく変わる。
    /// 詳細は <see cref="HlslTokenCache"/> を参照。
    /// </remarks>
    public HlslTokenCache? TokenCache { get; init; }

    /// <summary>
    /// 取り込みの展開結果を共有するキャッシュ。<see langword="null"/> の場合は毎回展開する。
    /// </summary>
    /// <remarks>
    /// 同じヘッダを同じマクロの状態で取り込んだ場合に使い回す。
    /// 詳細は <see cref="HlslIncludeCache"/> を参照。
    /// </remarks>
    public HlslIncludeCache? IncludeCache { get; init; }

    /// <summary>include の入れ子の上限。</summary>
    public int MaxIncludeDepth { get; init; } = DefaultMaxIncludeDepth;

    /// <summary>マクロ展開の上限段数。</summary>
    public int MaxExpansionDepth { get; init; } = DefaultMaxExpansionDepth;

    /// <summary>
    /// 解決できなかった include を診断として報告するかどうか。
    /// </summary>
    /// <remarks>
    /// 既定で無効にしている。Unity をインストールしていない環境では
    /// パッケージ配下のヘッダが軒並み解決できず、報告すると出力が埋まってしまう。
    /// 解決できなかったことは <c>UnresolvedIncludes</c> から取得でき、
    /// 必要な層が必要な粒度で報告できる。
    /// </remarks>
    public bool ReportUnresolvedIncludes { get; init; }

    /// <summary>
    /// 取り込んだファイルが、利用者が書いて直せるファイルかを、そのパスから判定する。
    /// <see langword="null"/> なら、解析しているファイルだけを利用者のファイルとする。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 利用者のファイルで書いたマクロの本体と実引数には、書かれた位置を残す
    /// (<see cref="HlslSyntaxToken.MacroDefinitionSpan"/> / <see cref="HlslSyntaxToken.MacroArgumentSpan"/>)。
    /// 報告の位置と、報告してよいかの判定に使う。
    /// </para>
    /// <para>
    /// 共通の <c>.hlsl</c> を取り込むのはよくある書き方であり、その中身も利用者が直せるコードである。
    /// 直せないのは Unity や外部パッケージのヘッダで、どれがそうかは呼び出し側が知っている。
    /// </para>
    /// </remarks>
    public Func<string, bool>? IsUserInclude { get; init; }

    /// <summary>
    /// 展開のしかたを変える設定をまとめた指紋。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>取り込みの展開結果を使い回してよいかの鍵の一部である
    /// (<see cref="HlslIncludeCache"/>)。</b>
    /// 同じヘッダでも、両方の分岐を残すシンボルが違えば結果は変わる。
    /// </para>
    /// <para>
    /// 定義済みマクロと <c>Prelude</c> はマクロ表に入るので、
    /// マクロ表のハッシュ側で見ている。ここでは見ない。
    /// 解決器は指紋に入れず、キャッシュを解析 1 回分の寿命にすることで揃えている。
    /// </para>
    /// </remarks>
    public int FingerprintForCache
    {
        get
        {
            HashCode hash = default;

            hash.Add(MaxIncludeDepth);
            hash.Add(ReportUnresolvedIncludes);

            // 利用者のファイルかどうかで、マクロの本体と実引数に残す位置が変わる。
            hash.Add(IsUserInclude);
            hash.Add(BothBranchSymbols.Count);

            foreach (string keyword in BothBranchSymbols.OrderBy(k => k, StringComparer.Ordinal))
            {
                hash.Add(keyword, StringComparer.Ordinal);
            }

            // 構成によって変わる条件かどうかで、並べるかどうかが変わる。
            hash.Add(ConfigurationSymbols.Count);

            foreach (string symbol in ConfigurationSymbols.OrderBy(k => k, StringComparer.Ordinal))
            {
                hash.Add(symbol, StringComparer.Ordinal);
            }

            // 成り立たない分岐は並べないので、制約が違えばヘッダの展開結果も変わる。
            foreach ((string first, string second) in SymbolConstraints.ExclusivePairs
                         .OrderBy(p => p.First, StringComparer.Ordinal)
                         .ThenBy(p => p.Second, StringComparer.Ordinal))
            {
                hash.Add(first, StringComparer.Ordinal);
                hash.Add(second, StringComparer.Ordinal);
            }

            foreach (ImmutableArray<string> group in SymbolConstraints.RequiredGroups)
            {
                hash.Add(group.Length);

                foreach (string symbol in group)
                {
                    hash.Add(symbol, StringComparer.Ordinal);
                }
            }

            // ヘッダの条件を記号として読むかどうかで、そのヘッダの展開結果が変わる。
            hash.Add(IncludedDeclaredSymbols.Count);

            foreach (string symbol in IncludedDeclaredSymbols.OrderBy(s => s, StringComparer.Ordinal))
            {
                hash.Add(symbol, StringComparer.Ordinal);
            }

            // 並べてよい領域が決められていれば、ヘッダの中の判断もそれに従う。
            hash.Add(MergeOnlyRegions?.ContentHash ?? 0);

            return hash.ToHashCode();
        }
    }
}

/// <summary>
/// <c>#pragma</c> 指令 1 件。
/// </summary>
/// <param name="Tokens">指令の内容 (<c>#pragma</c> 自体を除く)。</param>
/// <param name="NameToken">最初のトークン。<c>vertex</c> や <c>multi_compile</c> にあたる。</param>
/// <remarks>
/// プリプロセッサは <c>#pragma</c> を解釈せず、そのまま記録する。
/// 意味はコンパイラごとに異なり、Unity 固有の指令
/// (<c>vertex</c> / <c>fragment</c> / <c>target</c> / <c>multi_compile</c>) も多い。
/// これらはルールが必要とするため、捨てずに残す必要がある。
/// </remarks>
public readonly record struct PragmaDirective(
    ImmutableArray<HlslSyntaxToken> Tokens,
    HlslSyntaxToken NameToken)
{
    /// <summary>指令名 (<c>vertex</c>、<c>multi_compile</c> など)。</summary>
    public string Name => NameToken.Text;

    /// <summary>指令名を除いた引数のトークン。</summary>
    public IEnumerable<HlslSyntaxToken> Arguments => Tokens.Skip(1);
}

/// <summary>
/// 条件付きで出力へ入れたトークンの範囲。
/// </summary>
/// <param name="Start">出力トークン列での開始位置。</param>
/// <param name="Length">トークンの数。</param>
/// <param name="Condition">この範囲が存在する条件。</param>
/// <remarks>
/// <c>BothBranchSymbols</c> に入れたシンボルの <c>#ifdef</c> では、
/// どちらの分岐も捨てずに並べる。
/// どちらの条件のもとにあるかはここに残す。
/// </remarks>
public readonly record struct ConditionalTokenRange(
    int Start,
    int Length,
    SymbolCondition Condition)
{
    /// <summary>
    /// 条件で中身が変わるマクロを使う文を、定義ごとに複製した範囲かどうか (条件の巻き上げ)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>巻き上げた範囲は、位置で条件を引いてはならない。</b>
    /// 複製はどれも同じ位置から作られるので、ソース上の範囲に直すと
    /// <c>_A</c> の複製と <c>!_A</c> の複製が同じ場所に重なる。
    /// </para>
    /// <para>
    /// 複製ごとに別のトークンのインスタンスを持たせてあるので、トークンで引く。
    /// </para>
    /// </remarks>
    public bool IsHoisted { get; init; }
}

/// <summary>
/// 現れた <c>#include</c> 1 件分。
/// </summary>
/// <param name="Path">書かれていたパス。</param>
/// <param name="Location">パスが書かれている位置。<c>"..."</c> や <c>&lt;...&gt;</c> の全体を指す。</param>
/// <param name="ResolvedFilePath">解決できたファイルの実際のパス。解決できなかった場合は <see langword="null"/>。</param>
/// <remarks>
/// <para>
/// <b>書かれたパスと、実際に読んだファイルは別物である。</b>
/// <c>Packages/com.unity.render-pipelines.universal/...</c> は実在するフォルダーではなく、
/// パッケージの実体は <c>Library/PackageCache</c> の下にある。
/// 「どこへ飛べばよいか」に答えるには、解決後のパスが要る。
/// </para>
/// <para>
/// 位置も要る。どの <c>#include</c> の話かを行で示せなければ、
/// 利用者は自分のファイルのどこを直せばよいか分からない。
/// </para>
/// </remarks>
public readonly record struct IncludeReference(
    string Path,
    Location Location,
    string? ResolvedFilePath)
{
    /// <summary>解決できたかどうか。</summary>
    public bool IsResolved => ResolvedFilePath is not null;
}

/// <summary>
/// 条件が外れて読み飛ばした分岐 1 つ分。
/// </summary>
/// <param name="Location">
/// 分岐を始めた指令の名前の終わりから、分岐を閉じた指令の名前の始まりまで。
/// 指令の行そのものは非活性ではないので、行で表すときは始まりの次の行から数える。
/// </param>
/// <param name="ConditionSymbols">この <c>#if</c> の連なりの条件に書かれた名前。</param>
/// <remarks>
/// <para>
/// <b>解析しているファイルに書かれた分岐だけを残す。</b>
/// 取り込んだヘッダの分岐はエディタに表示する先が無く、
/// URP のヘッダには数千の分岐がある。
/// </para>
/// <para>
/// <b>外側が非活性なら、内側の分岐は残さない。</b>
/// 外側の範囲がそれを丸ごと含んでいる。
/// </para>
/// <para>
/// 条件の名前を持つのは、この構成で外れたことが「どの構成でも外れる」ことを意味しない場合があるためである。
/// 展開しなかったシンボルで守られた分岐は、別の構成では通る。
/// </para>
/// </remarks>
public readonly record struct InactiveRegion(Location Location, ImmutableArray<string> ConditionSymbols);

/// <summary>
/// 構成によって結果が変わる <c>#if</c> の連なり 1 つの範囲。
/// </summary>
/// <param name="FilePath">指令が書かれたファイルのパス。</param>
/// <param name="Start"><c>#if</c> 系の指令名のトークンの開始位置。</param>
/// <param name="End"><c>#endif</c> の指令名のトークンの終了位置。</param>
/// <param name="Symbols">連なりの条件が見ているキーワード。条件付きで覚えたマクロを通して見ているものも含む。</param>
/// <param name="DependsOnMacros">
/// 条件が、どのキーワードで変わるのか分からないマクロ (構成で変わる分岐の中で定義されたもの) を見ているかどうか。
/// </param>
/// <remarks>
/// <b>木の違いを、どのキーワードが起こしたのかを知るために持つ。</b>
/// 互いに関係しないキーワードを 1 つの構成でまとめて有効にしたとき、
/// 既定の木との違いがどのキーワードの領域の中にあるかで、そのキーワードの条件を付ける。
/// 解析しているファイルだけでなく、取り込んだヘッダの連なりも持つ。外側が読み飛ばされた連なりは持たない。
/// </remarks>
public readonly record struct KeywordRegion(
    string FilePath,
    int Start,
    int End,
    ImmutableArray<string> Symbols,
    bool DependsOnMacros);

/// <summary>
/// 両方の分岐を並べた領域 1 つの位置。
/// </summary>
/// <param name="FilePath">指令が書かれたファイルのパス。</param>
/// <param name="Start">指令名のトークンの開始位置。</param>
public readonly record struct MergedRegion(string FilePath, int Start);

/// <summary>
/// 並べてよい領域の集まり。
/// </summary>
/// <remarks>
/// 中身のハッシュを 1 度だけ求めて持つ。取り込みの展開結果を使い回す鍵に入れるが
/// (<see cref="PreprocessorOptions.FingerprintForCache"/>)、鍵は取り込みのたびに作る。
/// </remarks>
public sealed class MergedRegionSet
{
    private readonly FrozenSet<MergedRegion> _regions;

    /// <summary>集まりを作る。</summary>
    /// <param name="regions">並べてよい領域。</param>
    public MergedRegionSet(IEnumerable<MergedRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);

        _regions = regions.ToFrozenSet();

        // 順序によらないように、要素ごとのハッシュを足し合わせる。
        int hash = _regions.Count;

        foreach (MergedRegion region in _regions)
        {
            hash = unchecked(hash + HashCode.Combine(StringComparer.Ordinal.GetHashCode(region.FilePath), region.Start));
        }

        ContentHash = hash;
    }

    /// <summary>中身のハッシュ。</summary>
    public int ContentHash { get; }

    /// <summary>領域の数。</summary>
    public int Count => _regions.Count;

    /// <summary>その位置の領域を並べてよいかを判定する。</summary>
    /// <param name="region">領域の位置。</param>
    /// <returns>並べてよければ <see langword="true"/>。</returns>
    public bool Contains(MergedRegion region) => _regions.Contains(region);
}

/// <summary>
/// 同時に定義されている必要があるシンボルの組。
/// </summary>
/// <param name="Symbols">組のシンボル。名前順に並ぶ。</param>
/// <param name="Location">
/// その組でしか通らない分岐を始めた指令の名前の位置。
/// 同じ組でしか通らない分岐が何か所もあれば、最初の 1 か所。
/// </param>
/// <remarks>
/// 位置を持つのは、その構成を展開しなかったときに、どこが読まれていないのかを指すためである。
/// 組のシンボルの名前だけでは、入れ子の内側と外側のどちらを見ればよいかが分からない。
/// </remarks>
public readonly record struct SymbolCombination(ImmutableArray<string> Symbols, Location Location);

/// <summary>両方の分岐を並べなかった理由。</summary>
public enum BothBranchDeclineReason
{
    /// <summary>既定の構成が並べなかった領域なので、構成ごとの展開でも並べない。</summary>
    OutsideDefaultMergedRegions,

    /// <summary>指令がファイルではなくマクロの展開から来た。</summary>
    NotInFile,

    /// <summary>外側の条件が両方の分岐を並べていない。</summary>
    OuterNotMerged,

    /// <summary>分岐の中身が構文の単位で閉じていない。</summary>
    RegionNotSelfContained,

    /// <summary>分岐の中でコードに使われるマクロを定義している。</summary>
    RegionDefinesMacros,

    /// <summary>分岐ごとに取り込むファイルを切り替えている。</summary>
    RegionSwitchesIncludes,

    /// <summary>分岐が閉じられていない。</summary>
    RegionUnterminated,

    /// <summary>分岐の中で、構成によって定義が変わるマクロを呼んでいる。</summary>
    CallsConfigurationDependentMacro,

    /// <summary>条件式をシンボルの条件として読めない。</summary>
    UnreadableCondition,

    /// <summary>並べなかった <c>#if</c> に続く <c>#elif</c>。</summary>
    FollowsUnmergedBranch,
}

/// <summary>両方の分岐を並べなかった記録 1 件。</summary>
/// <param name="Symbol">並べなかったシンボル。</param>
/// <param name="Reason">理由。</param>
/// <param name="FilePath">その指令が書かれたファイル。</param>
/// <remarks>
/// 並べなかったシンボルは構成ごとに展開し直す (バリアント)。
/// なぜバリアントが要ったのかを後から数えるために残す。挙動には使わない。
/// </remarks>
public readonly record struct BothBranchDecline(string Symbol, BothBranchDeclineReason Reason, string FilePath);

/// <summary>
/// プリプロセッサの実行結果。
/// </summary>
/// <param name="Tokens">
/// マクロを展開し、指令と非活性領域を取り除いたトークン列。構文解析はこれを入力とする。
/// </param>
/// <param name="Diagnostics">プリプロセッサが検出した問題。</param>
/// <param name="Pragmas">現れた <c>#pragma</c> 指令。</param>
/// <param name="ResolvedIncludes">解決できた include のパス。</param>
/// <param name="UnresolvedIncludes">解決できなかった include のパス。</param>
/// <param name="Macros">処理完了時点で定義されているマクロ。</param>
/// <param name="SkippedIdentifiers">
/// 条件分岐の非活性領域にのみ現れた識別子。
/// </param>
/// <param name="ConditionalIdentifiers">
/// <c>#if</c> / <c>#ifdef</c> の条件で参照された名前。
/// <c>#pragma shader_feature</c> で宣言されていないシンボルを見つけるために使う。
/// </param>
/// <param name="Includes">
/// 現れた <c>#include</c> と、それぞれが解決できたかどうか。
/// 行を指した報告と、取り込み先へ飛ぶ操作に使う。
/// </param>
/// <param name="ConditionalRegions">
/// 条件付きで残したトークンの範囲。
/// <see cref="PreprocessorOptions.BothBranchSymbols"/> を指定しない限り空である。
/// </param>
/// <param name="DeclinedBothBranchSymbols">
/// 両方の分岐を残すつもりだったが、領域の形のせいで残せなかったシンボル。
/// 1 か所でも残せなかったシンボルは、まだ構成ごとの展開が要る。
/// </param>
public readonly record struct PreprocessResult(
    ImmutableArray<HlslSyntaxToken> Tokens,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<PragmaDirective> Pragmas,
    ImmutableArray<string> ResolvedIncludes,
    ImmutableArray<string> UnresolvedIncludes,
    ImmutableDictionary<string, MacroDefinition> Macros,
    ImmutableArray<string> SkippedIdentifiers,
    ImmutableArray<HlslSyntaxToken> ConditionalIdentifiers,
    ImmutableArray<IncludeReference> Includes,
    ImmutableArray<ConditionalTokenRange> ConditionalRegions,
    ImmutableArray<string> DeclinedBothBranchSymbols)
{
    /// <summary>
    /// 解析しているファイルの中で、条件が外れて読み飛ばした分岐。
    /// </summary>
    /// <remarks>
    /// エディタで、効いていないコードを薄く表示するために使う。
    /// 既定値 (<c>default</c>) のままのこともあるので、<c>IsDefaultOrEmpty</c> で確かめること。
    /// </remarks>
    public ImmutableArray<InactiveRegion> InactiveRegions { get; init; }

    /// <summary>
    /// 両方の分岐を並べなかった記録。シンボル・理由・ファイルの組ごとに 1 件。
    /// </summary>
    /// <remarks>
    /// <see cref="DeclinedBothBranchSymbols"/> の理由の内訳である。計測のためにあり、挙動には使わない。
    /// 既定値 (<c>default</c>) のままのこともあるので、<c>IsDefaultOrEmpty</c> で確かめること。
    /// </remarks>
    public ImmutableArray<BothBranchDecline> BothBranchDeclines { get; init; }

    /// <summary>
    /// 取り込んだファイルの非活性領域に現れた識別子。
    /// </summary>
    /// <remarks>
    /// <see cref="SkippedIdentifiers"/> のうち、取り込んだファイルに現れたもの。
    /// 既定値 (<c>default</c>) のままのこともあるので、<c>IsDefaultOrEmpty</c> で確かめること。
    /// </remarks>
    public ImmutableArray<string> SkippedIncludedIdentifiers { get; init; }

    /// <summary>
    /// 解析しているファイル自身の非活性領域に現れた識別子。位置を持つ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>名前だけでなく位置を持つのは、別の構成がその場所を解析したかを確かめるためである。</b>
    /// <c>#ifdef _A</c> の中は、<c>_A</c> を有効にした構成では読まれている。
    /// そこに宣言があれば、その構成の木に載っている。
    /// 名前だけで「どこかで読み飛ばされた名前は宣言済み」と扱うと、
    /// 既定の構成で使っている未宣言の名前が、別の構成で読み飛ばされただけで見逃される。
    /// </para>
    /// <para>
    /// 既定値 (<c>default</c>) のままのこともあるので、<c>IsDefaultOrEmpty</c> で確かめること。
    /// </para>
    /// </remarks>
    public ImmutableArray<HlslSyntaxToken> SkippedRootIdentifiers { get; init; }

    /// <summary>
    /// 1 つずつ有効にしただけでは通らない、条件に書かれたシンボルの組。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>#if defined(_A) &amp;&amp; defined(_B)</c> のように論理積で守られた領域や、
    /// <c>#ifdef _A</c> の中の <c>#ifdef _B</c> のように入れ子で守られた領域を、
    /// 両方の分岐を並べる形で扱えなかった場合に入る。
    /// その領域の中のコードは、<c>_A</c> だけ・<c>_B</c> だけの構成には現れない。
    /// </para>
    /// <para>
    /// 入れ子は、外側の分岐の条件をすべて掛け合わせて組にする。
    /// 外側を読み飛ばしていても、入れ子の条件は読む。
    /// シンボルとして読むのは <see cref="PreprocessorOptions.DeclaredSymbols"/> にある名前だけである。
    /// </para>
    /// <para>
    /// 解析しているファイルに書かれた条件だけを入れる。
    /// 既定値 (<c>default</c>) のままのこともあるので、<c>IsDefaultOrEmpty</c> で確かめること。
    /// </para>
    /// </remarks>
    public ImmutableArray<SymbolCombination> RequiredSymbolCombinations { get; init; }

    /// <summary>
    /// 並べた分岐で定義したマクロがコードとして展開された、その分岐の条件が見ているシンボル。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>このシンボルを並べた結果は信用できない。</b>
    /// マクロ表は 1 つしかないので、並べた分岐の <c>#define</c> はどの構成でも効く。
    /// 条件の中でしか使われない前提で並べているが、コードとして展開されたらその前提は崩れている。
    /// </para>
    /// <para>
    /// 呼び出し側は、このシンボルを並べる対象から外して展開し直す
    /// (<c>ShaderCompilation.Build</c>)。取り込んだヘッダが使っている場合も、ここで分かる。
    /// </para>
    /// </remarks>
    public ImmutableArray<string> MergedMacroConflicts { get; init; }

    /// <summary>構成によって結果が変わった <c>#if</c> の連なりの範囲。取り込んだヘッダの分も含む。</summary>
    /// <remarks>既定値 (<c>default</c>) のままのこともあるので、<c>IsDefaultOrEmpty</c> で確かめること。</remarks>
    public ImmutableArray<KeywordRegion> KeywordRegions { get; init; }

    /// <summary>
    /// 分岐の中でマクロを定義・削除するか、ヘッダを取り込むキーワード。読み飛ばした分岐の分も含む。
    /// 名前がそのままコードに現れたキーワードも含む。
    /// </summary>
    /// <remarks>
    /// 有効にすると、そのキーワードを見ている連なりの外の展開まで変わりうる。
    /// 条件がマクロの本体を通してキーワードを見ている場合 (<c>#define REQUIRE_NORMAL defined(_A)</c> と <c>#if REQUIRE_NORMAL</c>) も、
    /// その中の <c>#define</c> で数える。既定値 (<c>default</c>) のままのこともある。
    /// </remarks>
    public ImmutableArray<string> MacroAffectingSymbols { get; init; }

    /// <summary>
    /// 解析しているファイルに書かれた <c>#define</c> と、その定義に通る条件。読み飛ばした分岐の分も含む。
    /// </summary>
    /// <remarks>
    /// <b>マクロ表 (<see cref="Macros"/>) には、既定の構成で最後に効いた定義しか残らない。</b>
    /// <c>#ifdef _A</c> と <c>#else</c> で書き分けた定義の、もう一方を説明するのに使う (エディタのホバー)。
    /// 定義の <see cref="MacroDefinition.NameToken"/> は書かれた位置を指す。
    /// </remarks>
    internal ImmutableArray<(MacroDefinition Definition, SymbolCondition Condition)> WrittenDefinitions { get; init; } = [];

    /// <summary>両方の分岐を並べた領域の、指令名のトークンの位置。取り込んだヘッダの分も含む。</summary>
    /// <remarks>バリアントに同じ判断をさせるために使う (<see cref="PreprocessorOptions.MergeOnlyRegions"/>)。</remarks>
    public ImmutableArray<MergedRegion> MergedRegions { get; init; }

    /// <summary>
    /// 両方の分岐を並べた条件に現れたシンボル。
    /// </summary>
    /// <remarks>
    /// 中身が <c>#define</c> だけでトークンを出さない分岐も含む。
    /// <see cref="ConditionalRegions"/> には、トークンを出した分岐しか現れない。
    /// 既定値 (<c>default</c>) のままのこともあるので、<c>IsDefaultOrEmpty</c> で確かめること。
    /// </remarks>
    public ImmutableArray<string> MergedSymbols { get; init; }

    /// <summary>
    /// 条件によって中身が変わるマクロを使う文を、定義ごとに複製した回数。
    /// </summary>
    /// <remarks>
    /// <b>この方式が実際に働いているかを測るためにある。</b>
    /// 指摘の数が変わらないことは「壊れていない」ことを示すが、
    /// 「使われている」ことも「安全である」ことも示さない
    /// (<see cref="HoistGiveUps"/> と合わせて見る)。
    /// </remarks>
    public int HoistedUnits { get; init; }

    /// <summary>文の切れ目を読み直した回数。</summary>
    /// <remarks>
    /// 記号を数えて決めた切れ目が、展開してみると文にならなかった回数である。
    /// 展開前のトークンだけでは切れ目が決まらないことの目安になる。
    /// </remarks>
    public int HoistRetries { get; init; }

    /// <summary>どの切れ目でも文にならず、複製を諦めた回数。</summary>
    /// <remarks>
    /// <b>0 であることは安全の証拠ではない。</b>
    /// 諦めた回数であって、取り違えたまま通した回数ではない。
    /// 取り違えたまま通っていないことは、構文解析で確かめている
    /// (<c>HlslParser.IsCompleteUnits</c>)。
    /// </remarks>
    public int HoistGiveUps { get; init; }

    /// <summary>
    /// 指定した名前が、非活性領域を含めてどこかに現れたかどうかを判定する。
    /// </summary>
    /// <param name="name">調べる識別子。</param>
    /// <returns>非活性領域に現れていた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>これは誤検出を防ぐために不可欠である。</b>
    /// 展開は単一構成でしか行わないため、
    /// <c>#ifdef _NORMALMAP</c> の中で宣言された uniform は
    /// 別の構成を選んだ解析からは「存在しない」ように見える。
    /// </para>
    /// <para>
    /// 「宣言が見つからない」ことを根拠に診断を出すルールは、
    /// 非活性領域に名前が現れていないことも確かめなければならない。
    /// そうしないと、単に別の構成を選んだだけのコードを誤りとして報告してしまう。
    /// </para>
    /// </remarks>
    public bool AppearsInInactiveRegion(string name)
        => !SkippedIdentifiers.IsDefaultOrEmpty && SkippedIdentifiers.Contains(name, StringComparer.Ordinal);
}
