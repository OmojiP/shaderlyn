using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Rules;

/// <summary>
/// ShaderLab と HLSL を突き合わせて初めて検査できるルールの定義。
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
        id: "SL0002",
        title: "一部の検査を行っていません",
        messageFormat: "プロパティと uniform の対応検査を行っていません。{0}",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "include を解決できなかった、あるいは HLSL の解析に失敗したため、"
            + "uniform の一覧が不完全です。この状態で「対応する宣言が無い」と報告すると、"
            + "正しく書かれたシェーダーを誤りとして指摘することになるため、"
            + "対応検査 (SL1001 / SL1002 / SL1004 / URP0001 / URP0002) は実行していません。"
            + "include を解決できていない場合は、Unity プロジェクトのパスを --unity-project で"
            + "指定すると解決できる場合があります。"
            + "HLSL の解析に失敗している場合は、同じファイルに報告されている構文エラーを直してください。",
        helpLinkUri: DocumentationLinks.For("SL0002"));

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
        id: "SL0003",
        title: "調べていないシンボルの経路があります",
        messageFormat: "シンボル {0} を{1}有効にした構成は調べていません。{2}で分かれる分岐の中は読んでいません。",
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
        helpLinkUri: DocumentationLinks.For("SL0003"));

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
        id: "SL0004",
        title: "条件を追えなかった箇所があります",
        messageFormat: "この箇所の #ifdef の条件を追えなかったため、構成をまたいだ検査を行っていません。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "#if が構文の単位をまたいでいるため、"
            + "構成ごとに組み立てた構文木の対応が取れませんでした。"
            + "この箇所については、どのシンボルの組み合わせで存在するコードなのかが分かっていません。"
            + "出現条件を根拠にするルール (HL0311 / HL0313 / HL0340) は、"
            + "誤った指摘を出さないためにこの箇所では何も報告しません。"
            + "#if を関数や文の単位で閉じるように書き換えると、条件を追えるようになります。",
        helpLinkUri: DocumentationLinks.For("SL0004"));

    /// <summary>プロパティに対応する uniform 宣言が無い。</summary>
    public static DiagnosticDescriptor MissingUniformDeclaration { get; } = new(
        id: "SL1001",
        title: "プロパティに対応する HLSL の宣言がありません",
        messageFormat: "プロパティ '{0}' は HLSL コードで未使用です。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "Properties に宣言されているのに、HLSL 側に対応する uniform が無く、"
            + "コード中にその名前が一度も現れていません。"
            + "マテリアルのインスペクタには表示され、値も保存されますが、描画には一切影響しません。"
            + "プロパティ名のスペルミスか、使わなくなったプロパティの消し忘れです。",
        helpLinkUri: DocumentationLinks.For("SL1001"));

    /// <summary>マテリアル定数バッファの uniform が Properties に公開されていない。</summary>
    /// <remarks>
    /// 検査対象をマテリアル用の定数バッファに限っている。
    /// HLSL の大域変数一般を対象にすると、スクリプトから設定する大域 uniform が
    /// すべて指摘され、誤検出の山になる。
    /// </remarks>
    public static DiagnosticDescriptor UniformNotExposed { get; } = new(
        id: "SL1002",
        title: "定数バッファの uniform が Properties に公開されていません",
        messageFormat: "'{1}' の '{0}' に対応するプロパティが Properties にありません。マテリアルからは値を設定できません。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "マテリアルごとの値を収める定数バッファに、Properties へ公開されていない uniform があります。"
            + "マテリアルのインスペクタからは設定できず、スクリプトから明示的に設定しない限り既定値のままになります。"
            + "スクリプトから設定する意図的な内部状態であれば、この指摘は抑制して構いません。",
        helpLinkUri: DocumentationLinks.For("SL1002"));

    /// <summary>プロパティの型と uniform の型が食い違っている。</summary>
    public static DiagnosticDescriptor PropertyTypeMismatch { get; } = new(
        id: "SL1003",
        title: "プロパティの型と HLSL の型が一致しません",
        messageFormat: "プロパティ '{0}' は {1} ですが、HLSL では '{2}' として宣言されています。{3}が必要です。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "Properties に宣言された型と、HLSL 側の uniform の型が噛み合っていません。"
            + "Unity はこれをエラーにせず、値の一部だけを転送するか、まったく転送しません。"
            + "「マテリアルで設定した色が反映されない」「スライダーを動かしても何も起きない」"
            + "という形で現れます。"
            + "成分数の違い (Color を half3 で受けるなど) は正常な書き方なので報告しません。",
        helpLinkUri: DocumentationLinks.For("SL1003"));

    /// <summary>宣言されているが一度も使われていないプロパティ。</summary>
    public static DiagnosticDescriptor UnusedProperty { get; } = new(
        id: "SL1004",
        title: "プロパティが宣言されているだけで使われていません",
        messageFormat: "プロパティ '{0}' は uniform として宣言されていますが、シェーダーのコードから一度も読まれていません。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "uniform の宣言はあるものの、その値をどこからも読んでいません。"
            + "機能を削除した際の消し残しであることが多く、"
            + "定数バッファの容量とマテリアルの表示を無駄に占有します。"
            + "スクリプトから読む用途や、将来使う予定がある場合は抑制して構いません。",
        helpLinkUri: DocumentationLinks.For("SL1004"));
}
