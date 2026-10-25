using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Rules;

/// <summary>
/// 意味解析の結果 (どこまで検査できたか) を伝えるルールの定義。
/// </summary>
/// <remarks>
/// ここに含まれるルールは、いずれも意味解析の結果 (<c>ShaderCompilation</c>) を必要とする。
/// 意味解析が行われていない場合、これらのルールは<b>何も報告しない</b>。
/// 「検査した結果、問題が無かった」のではなく「検査していない」ため、
/// 何も伝えずに通すと利用者に誤解を与える。その事実は <see cref="IncompleteAnalysis"/> が報告する。
/// </remarks>
internal static class SemanticRuleDescriptors
{
    private const string CorrectnessCategory = "Correctness";
    private const string UsageCategory = "Usage";

    /// <summary>依存関係を解決できず、対応検査を実行していない。</summary>
    /// <remarks>
    /// <para>
    /// <b>これは「検査していない」ことを利用者へ伝えるためのルールである。</b>
    /// 検査していないことを伝えないリンタは、
    /// 「指摘が無い＝問題が無い」という誤った安心を与える。
    /// 検査が素通りしていることに気づけないまま CI が緑になり続けるのは、
    /// 誤検出よりも高くつく失敗である。
    /// </para>
    /// <para>
    /// <b>原因を決め打ちしたメッセージにしてはならない。</b>
    /// 多いのは <c>--unity-project</c> の指定漏れだが、
    /// シェーダー自身の構文エラーで検査を見送ることもある。
    /// 後者に「依存関係を解決できないため」と言うと、
    /// 利用者は既に指定してあるオプションを疑って時間を溶かす。
    /// 具体的な原因は報告の際にメッセージへ埋め込む。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor IncompleteAnalysis { get; } = new(
        id: "HL0005",
        title: "一部の検査を行っていません",
        messageFormat: "{0}を行っていません。{1}",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "include を解決できなかった、あるいは HLSL の解析に失敗したため、"
            + "uniform の一覧が不完全です。この状態で「対応する宣言が無い」と報告すると、"
            + "正しく書かれたシェーダーを誤りとして指摘することになるため、"
            + "対応検査 (SL1001 / SL1002 / SL1004 / URP0001 / URP0002) は実行していません。"
            + "include を解決できていない場合は、Unity プロジェクトのパスを --unity-project で"
            + "指定すると解決できる場合があります。"
            + "HLSL の解析に失敗している場合は、同じファイルに報告されている構文エラーを直してください。",
        helpLinkUri: DocumentationLinks.For("HL0005"));

    /// <summary>シンボルの経路を調べきれていない。</summary>
    /// <remarks>
    /// <para>
    /// <b>これも「検査していない」ことを利用者へ伝えるためのルールである。</b>
    /// <c>#pragma shader_feature</c> で宣言したシンボルは C# からも切り替えられるため、
    /// 宣言されたシンボルの経路はいずれも「いつか通るコード」である。
    /// 解析はそのうち <see cref="Shaderlyn.Semantics.SemanticsOptions.MaxSymbolVariants"/> 件までを展開する。
    /// </para>
    /// <para>
    /// 展開しなかった経路のコードは<b>一度も読まれていない</b>。
    /// そこにある構文エラーも、そこでしか使われていないプロパティも見えていない。
    /// 何も伝えずに一部だけ調べると、調べていない箇所を「問題なし」と受け取られる。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor UnexploredSymbols { get; } = new(
        id: "HL0006",
        title: "未解析のシンボル構成が存在します",
        messageFormat: "シンボル {0} を{1}有効にした構成は未解析です。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "#pragma shader_feature / multi_compile で宣言されたシンボルは、"
            + "マテリアルからも C# からも切り替えられるため、どの経路もいつか通るコードです。"
            + "解析はこのシェーダー自身の条件が見ているシンボルを 1 つずつ有効にして展開し、"
            + "入れ子や論理積の条件でしか通らない分岐は、シンボルの組を同時に有効にして展開します。"
            + "ただし展開の件数には上限があります (--max-symbol-variants)。"
            + "上限を超えた分の経路にあるコードは一度も読んでいないため、"
            + "そこにある構文エラーや、そこでしか使われていないプロパティは報告されません。"
            + "上限を上げると調べられますが、解析時間はシンボルの数に比例して伸びます。",
        helpLinkUri: DocumentationLinks.For("HL0006"));

    /// <summary>展開が #error に到達した構成。その構成はコンパイルされない。</summary>
    public static DiagnosticDescriptor ErrorConfiguration { get; } = new(
        id: "HL0009",
        title: "#error に到達する構成があります",
        messageFormat: "{0}#error ({1} の {2} 行目) に到達するため、コンパイルされません。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "その構成の展開が #error に到達しました。#error に到達する構成はコンパイルできないので、エンジンもその構成をコンパイルしないはずです "
            + "(Unreal Engine は C++ の ShouldCompilePermutation で除きます)。その構成のコードは、宣言の有無を見る検査 (HL0310 / HL0315 など) で考えません。"
            + "どの構成でも到達するなら、解析のマクロの状態 (設定の defines、Unreal の生成したファイル) が実際と違う見込みがあります。"
            + "Unity では、宣言したキーワードの構成はコンパイルされるので、UN0103 (エラー) が伝えます。",
        helpLinkUri: DocumentationLinks.For("HL0009"));

    /// <summary>条件を追えなかった箇所がある。</summary>
    /// <remarks>
    /// <para>
    /// <b>これも「検査していない」ことを伝えるためのルールである。</b>
    /// <c>#if</c> が構文の単位をまたいでいると、
    /// 構成ごとに組み立てた木の対応が取れず、その箇所の出現条件が分からない。
    /// </para>
    /// <para>
    /// 出現条件を根拠にするルール (HL0311 / HL0313 / HL0340) は、
    /// 条件が分からない箇所では報告しない。
    /// <b>報告を見送ったことを、伝えないままにしてはならない。</b>
    /// 指摘が出ないことを「問題が無い」と受け取られる。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor UnresolvedCondition { get; } = new(
        id: "HL0007",
        title: "構成によって文の形が変わる箇所があります",
        messageFormat: "この箇所は構成によって別の種類の文になるため、構成をまたいだ検査をしていません。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "#if が構文の単位をまたいでいるため、"
            + "構成ごとに組み立てた構文木の対応が取れませんでした。"
            + "この箇所については、どのシンボルの組み合わせで存在するコードなのかが分かっていません。"
            + "出現条件を根拠にするルール (HL0311 / HL0313 / HL0340) は、"
            + "誤った指摘を出さないためにこの箇所では何も報告しません。"
            + "#if を関数や文の単位で閉じるように書き換えると、条件を追えるようになります。",
        helpLinkUri: DocumentationLinks.For("HL0007"));
}
