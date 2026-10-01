using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Rules;

/// <summary>
/// HLSL のコードを対象にするルールの定義。
/// </summary>
/// <remarks>
/// 字句解析・プリプロセス・構文解析が報告するもの (HL0001 / HL0002 / HL0320 / HL0321) は
/// <c>Shaderlyn.Hlsl</c> 側にある。ここにあるのはアナライザが報告するものだけである。
/// </remarks>
internal static class HlslRuleDescriptors
{
    private const string CorrectnessCategory = "Correctness";

    /// <summary>構造体内でセマンティクスが重複している。</summary>
    /// <remarks>
    /// 構造体の中でしか検査しない。
    /// 別々の構造体が同じセマンティクスを使うのは正常であり、
    /// 頂点入力と頂点出力で同じ <c>TEXCOORD0</c> を使うのはむしろ普通である。
    /// </remarks>
    public static DiagnosticDescriptor DuplicateSemantic { get; } = new(
        id: "HL0210",
        title: "セマンティクスが重複しています",
        messageFormat: "セマンティクス '{0}' が構造体 '{1}' の中で重複しています。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "同じ構造体の複数のメンバーに同じセマンティクスが付いています。"
            + "どのメンバーに値が入るかは保証されず、"
            + "シェーダーコンパイラによってはコンパイルエラーになります。"
            + "TEXCOORD の番号の付け間違いで起きることがほとんどです。",
        helpLinkUri: DocumentationLinks.For("HL0210"));

    /// <summary><c>#pragma</c> で指定したエントリポイントが存在しない。</summary>
    public static DiagnosticDescriptor EntryPointNotFound { get; } = new(
        id: "HL0301",
        title: "エントリポイントが見つかりません",
        messageFormat: "#pragma {0} が指定する関数 '{1}' が見つかりません。{2}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "#pragma vertex や #pragma fragment に書かれた名前の関数が定義されていません。"
            + "関数名のスペルミスか、共通コード片の取り込み漏れです。"
            + "この Pass はコンパイルできず、シェーダー全体が使えなくなります。",
        helpLinkUri: DocumentationLinks.For("HL0301"));

    /// <summary>呼んでいる関数の定義が、その構成には存在しない。</summary>
    /// <remarks>
    /// <b>「定義があるか」ではなく「その構成に定義があるか」を見る。</b>
    /// 定義が <c>#ifdef</c> の中にあると、宣言だけを見れば揃っているように見え、
    /// シンボルを 1 つずつ有効にした木を見ても、どれかには必ず定義がある。
    /// 食い違うのは「呼ぶ側の条件」と「定義がある条件」の組み合わせであり、
    /// これは出現条件を見なければ判定できない。
    /// </remarks>
    public static DiagnosticDescriptor FunctionDefinitionNotFound { get; } = new(
        id: "HL0311",
        title: "関数の定義が見つかりません",
        messageFormat: "関数 '{0}' の定義がありません ({1})。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "呼んでいる関数の定義が、その構成には存在しません。"
            + "宣言だけを書いて定義を #ifdef の中に置き、"
            + "条件の付け方が呼ぶ側とずれている場合に起きます。"
            + "その構成ではリンクに失敗し、シェーダーが使えなくなります。",
        helpLinkUri: DocumentationLinks.For("HL0311"));

    /// <summary>使っている変数の宣言が、その構成には存在しない。</summary>
    /// <remarks>
    /// <b>関数についての <see cref="FunctionDefinitionNotFound"/> と同じことを、グローバル変数について言う。</b>
    /// 宣言が <c>#ifdef</c> の中にだけあると、名前はどこかの構成で宣言されているので、
    /// 「宣言されていない識別子」にはならない。
    /// </remarks>
    public static DiagnosticDescriptor VariableDeclarationNotPresent { get; } = new(
        id: "HL0315",
        title: "変数の宣言がその構成にありません",
        messageFormat: "変数 '{0}' の宣言がありません ({1})。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "使っている変数の宣言が、その構成には存在しません。"
            + "宣言を #ifdef の中に置き、使う側をその条件の外に書いた場合に起きます。"
            + "その構成ではコンパイルに失敗し、シェーダーが使えなくなります。",
        helpLinkUri: DocumentationLinks.For("HL0315"));

    /// <summary>参照している構造体のメンバーが、その構成には存在しない。</summary>
    /// <remarks>
    /// <b>メンバーが「あるか」ではなく「その構成にあるか」を見る。</b>
    /// <c>#ifdef</c> で囲まれたメンバーは、条件を外れた場所からは参照できない。
    /// 1 本の木にはメンバーも参照も両方あるので、
    /// 出現条件を見なければ食い違いに気づけない。
    /// </remarks>
    public static DiagnosticDescriptor StructMemberNotPresent { get; } = new(
        id: "HL0313",
        title: "その構成に存在しない構造体メンバー",
        messageFormat: "構造体 '{0}' のメンバー '{1}' は、{2}存在しません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "参照している構造体のメンバーが、その構成には宣言されていません。"
            + "メンバーを #ifdef で囲んだのに、"
            + "参照する側に同じ条件を付け忘れた場合に起きます。"
            + "その構成ではコンパイルに失敗し、シェーダーが使えなくなります。",
        helpLinkUri: DocumentationLinks.For("HL0313"));

    /// <summary>実引数を仮引数の型へ変換できない。</summary>
    /// <remarks>
    /// <b>成分が増える向きの変換だけを見る。</b>
    /// HLSL は成分を減らす変換 (<c>float3</c> → <c>float2</c>) を警告付きで許すが、
    /// 増やす変換は許さない。スカラーからの複製は別で、これは許される。
    /// 確実に誤りと言えるのは増える向きだけである。
    /// </remarks>
    public static DiagnosticDescriptor ArgumentNotConvertible { get; } = new(
        id: "HL0340",
        title: "実引数を仮引数の型へ変換できません",
        messageFormat: "関数 '{0}' の第 {1} 引数に {2} は渡せません (仮引数は {3})。{4}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "渡している値の成分が、仮引数の型より少なくなっています。"
            + "HLSL は成分を減らす変換は許しますが、増やす変換は許しません。"
            + "#ifdef で仮引数の型が切り替わる関数を呼んでいる場合、"
            + "片方の構成でだけ成り立たないことがあります。"
            + "その構成ではコンパイルに失敗します。",
        helpLinkUri: DocumentationLinks.For("HL0340"));

    /// <summary>引数の個数が、その構成に存在するどの宣言とも合わない。</summary>
    /// <remarks>
    /// 個数が合う宣言が 1 つも無いということは、その呼び出しは解決できない。
    /// 定義そのものが無い場合 (HL0311) とは区別する。名前はあるが形が合っていない。
    /// </remarks>
    public static DiagnosticDescriptor ArgumentCountMismatch { get; } = new(
        id: "HL0341",
        title: "引数の個数が合いません",
        messageFormat: "関数 '{0}' に引数を {1} 個渡していますが、{2}。{3}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "呼び出しの引数の個数が、その構成に存在するどの宣言とも合っていません。"
            + "既定値のある仮引数は省略できるものとして数えています。"
            + "その構成ではコンパイルに失敗します。",
        helpLinkUri: DocumentationLinks.For("HL0341"));

    /// <summary>参照しているメンバーが存在しない。</summary>
    /// <remarks>
    /// <b>存在しないことを言い切れる場合だけ報告する。</b>
    /// 対象がこのシェーダー自身が書いた構造体か、成分の数が分かる数値型のときに限る。
    /// 取り込んだヘッダの構造体やテクスチャのメソッドには踏み込まない。
    /// </remarks>
    public static DiagnosticDescriptor MemberNotFound { get; } = new(
        id: "HL0312",
        title: "存在しないメンバー",
        messageFormat: "{0} に '{1}' はありません。{2}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "参照しているメンバーが存在しません。"
            + "構造体のメンバー名のスペルミスか、"
            + "成分の数を超えたスウィズル (float2 に対する .z など) です。"
            + "位置 (xyzw) と色 (rgba) の成分名を混ぜることもできません。"
            + "いずれもコンパイルに失敗します。",
        helpLinkUri: DocumentationLinks.For("HL0312"));

    /// <summary><c>out</c> の引数に、書き戻せない式を渡している。</summary>
    /// <remarks>
    /// <c>out</c> / <c>inout</c> は呼び出し側の変数へ書き戻す。
    /// リテラルや関数の戻り値には書き戻す先が無い。
    /// </remarks>
    public static DiagnosticDescriptor ArgumentNotAssignable { get; } = new(
        id: "HL0342",
        title: "out の引数に書き戻せません",
        messageFormat: "関数 '{0}' の第 {1} 引数は {2} です。'{3}' には書き戻せません。{4}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "out / inout の仮引数は、呼び出し側の変数へ値を書き戻します。"
            + "リテラルや関数の戻り値のように、書き戻す先が無い式は渡せません。"
            + "その構成ではコンパイルに失敗します。",
        helpLinkUri: DocumentationLinks.For("HL0342"));

    /// <summary>代入や初期化で、値を代入先の型へ変換できない。</summary>
    /// <remarks>
    /// 判定は引数の受け渡し (HL0340) と同じである。
    /// 成分が増える向きだけを誤りとする。
    /// </remarks>
    public static DiagnosticDescriptor ValueNotConvertible { get; } = new(
        id: "HL0350",
        title: "値を代入先の型へ変換できません",

        // {2} は「どの構成でそうなるか」の注記。構成によらず型が決まる場合は空になる。
        messageFormat: "{0} に {1} は入りません。{2}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "代入や初期化において、右辺の成分数が左辺より不足しています"
            + "（例: float2 を float3 に代入）。"
            + "HLSL ではスカラー（1成分）から複数成分への拡張（全成分への複製）のみ許されており、"
            + "それ以外の成分不足はコンパイルエラーになります。",
        helpLinkUri: DocumentationLinks.For("HL0350"));

    /// <summary><c>return</c> の値が関数の戻り値の型と合わない。</summary>
    public static DiagnosticDescriptor ReturnNotConvertible { get; } = new(
        id: "HL0351",
        title: "return の値が戻り値の型と合いません",

        // {3} は「どの構成でそうなるか」の注記。構成によらず型が決まる場合は空になる。
        messageFormat: "関数 '{0}' の戻り値は {1} ですが、{2}。{3}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "return が返している値が、関数の戻り値の型と噛み合っていません。"
            + "成分が足りない、void の関数が値を返している、"
            + "値を返す関数が値なしで return している、のいずれかです。"
            + "いずれもコンパイルに失敗します。",
        helpLinkUri: DocumentationLinks.For("HL0351"));

    /// <summary>構成によっては、渡すときに成分が落ちる。</summary>
    /// <remarks>
    /// <para>
    /// <b>条件ごとに違う型で宣言した名前だけを見る。</b>
    /// 成分を減らす変換そのものは HLSL が警告付きで通すものであり、
    /// 意図して書く場面も多い。どこでも報告すると、意図した切り捨てに埋もれて読まれなくなる。
    /// </para>
    /// <para>
    /// 条件ごとに宣言を書き分けた場合は事情が違う。
    /// 書いた本人は両方の構成を意識しているのに、
    /// 片方の構成でだけ値が黙って落ちる形になっている。
    /// 「どちらの構成でも同じように動く」という思い込みは、
    /// その構成でのみ現れる不具合として後から出てくる。
    /// </para>
    /// <para>
    /// 通るコードなので警告にとどめる。エラーにすると、
    /// 意図して切り捨てている構成を持つシェーダーがビルドできなくなる。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor ConditionalTruncation { get; } = new(
        id: "HL0353",
        title: "構成によっては成分が切り捨てられます",
        messageFormat: "{0}、{1} は {2} です。{3} へ渡すと成分が切り捨てられます。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "同じ名前を条件ごとに違う型で宣言していて、"
            + "一部の構成でだけ成分の落ちる変換になっています。"
            + "HLSL は成分を減らす変換を警告 (X3206) 付きで通すため、コンパイルは成功します。"
            + "書き分けた構成の片方でだけ値が落ちるため、"
            + "その構成でのみ現れる不具合になります。",
        helpLinkUri: DocumentationLinks.For("HL0353"));

    /// <summary>値を返さずに関数を抜ける経路がある。</summary>
    /// <remarks>
    /// <b>「抜けない経路がある」ことを言い切れる場合だけ報告する。</b>
    /// 知らない形の文は「抜ける」に倒して数える。
    /// そうしないと、実際には必ず返している関数を誤って指摘することになる。
    /// </remarks>
    public static DiagnosticDescriptor MissingReturn { get; } = new(
        id: "HL0360",
        title: "値を返さずに終わる経路があります",
        messageFormat: "関数 '{0}' は {1} を返しますが、値を返さずに終わる経路があります。{2}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "値を返す関数に、return を通らずに末尾へ到達する経路があります。",
        helpLinkUri: DocumentationLinks.For("HL0360"));

    /// <summary>波括弧による初期化の要素の個数が合わない。</summary>
    public static DiagnosticDescriptor InitializerCountMismatch { get; } = new(
        id: "HL0352",
        title: "初期化の要素の個数が合いません",
        messageFormat: "{0} の初期化に要素を {1} 個書いていますが、{2} 個必要です。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "波括弧による初期化で、書いた要素の個数が代入先と合っていません。"
            + "ベクトルや行列では成分の数と、配列では要素の数と一致していなければなりません。"
            + "数えるのは書いた項目の個数ではなく、展開した成分の個数です。"
            + "float4 v = { float2(1, 2), 3, 4 }; は項目 3 個ですが成分 4 個なので正しい書き方です。",
        helpLinkUri: DocumentationLinks.For("HL0352"));

    /// <summary>宣言されていない識別子を使っている。</summary>
    /// <remarks>
    /// <b>宣言をすべて把握できているときだけ働く。</b>
    /// 取り込めなかったヘッダがあると、そこにある宣言が見えないまま
    /// 「宣言されていない」と言うことになる。
    /// </remarks>
    public static DiagnosticDescriptor UndeclaredIdentifier { get; } = new(
        id: "HL0310",
        title: "宣言されていない識別子です",
        messageFormat: "'{0}' は{1}宣言されていません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "使っている名前が、変数・仮引数・関数・構造体・マクロ・"
            + "シェーダーシンボルのいずれとしても宣言されていません。"
            + "一部の構成でだけ宣言されていない場合は、その構成を添えます。"
            + "スペルミスか、宣言しているヘッダの取り込み漏れです。"
            + "取り込めなかったヘッダがある場合、この検査は行いません。",
        helpLinkUri: DocumentationLinks.For("HL0310"));

    /// <summary>同じ名前を 2 回宣言している。</summary>
    /// <remarks>
    /// <para>
    /// <b>同時に成り立たない条件で分けて宣言している形は報告しない。</b>
    /// <c>#ifdef</c> の両方の分岐に同じ名前を書くのは普通の書き方であり、
    /// 1 本の木に並べたからといって二重宣言ではない。
    /// 2 つの出現条件を掛け合わせて、成り立つ構成があるときだけ報告する。
    /// </para>
    /// <para>
    /// <b>関数は形まで見る。</b>
    /// HLSL は多重定義を許すので、名前が同じでも仮引数の型が違えば誤りではない。
    /// 宣言だけ (プロトタイプ) を先に書く形も正しい。
    /// 同じ形の実装が 2 つあるときだけ報告する。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor Redeclaration { get; } = new(
        id: "HL0314",
        title: "シンボル宣言の重複",
        messageFormat: "'{0}' は既に {1} 行目で宣言されています。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "同じ範囲に同じ名前の宣言が 2 つあります (FXC の X3003)。"
            + "#ifdef で分けて宣言している形は、同時に成り立つ構成があるときだけ報告します。"
            + "関数は仮引数の型まで同じで、どちらも実装を持つ場合だけ報告します。"
            + "関数の中で宣言された名前は、名前の範囲を追っていないため対象にしません。",
        helpLinkUri: DocumentationLinks.For("HL0314"));

    /// <summary>数値型コンストラクタの引数の個数が合わない。</summary>
    /// <remarks>
    /// <c>float3(1, 2)</c> のように、成分の数と書いた値の数が合わない形。
    /// 1 つだけ渡す形はスカラーの複製と切り捨てが許されるので別に扱う。
    /// </remarks>
    public static DiagnosticDescriptor ConstructorArgumentMismatch { get; } = new(
        id: "HL0343",
        title: "コンストラクタの引数が合いません",
        messageFormat: "{0} は成分が {1} 個ですが、{2}。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "数値型のコンストラクタに渡した値の成分の数が、その型と合っていません。"
            + "入れ子のベクトルは平らにして数えます。"
            + "値を 1 つだけ渡す場合は、スカラーの複製と成分の切り捨てが許されます。",
        helpLinkUri: DocumentationLinks.For("HL0343"));

    /// <summary>同じレジスタを 2 つ以上の宣言が使っている。</summary>
    /// <remarks>
    /// レジスタの種類 (<c>b</c> / <c>t</c> / <c>s</c> / <c>u</c>) ごとに別の空間である。
    /// <c>b0</c> と <c>t0</c> は衝突しない。
    /// </remarks>
    public static DiagnosticDescriptor DuplicateRegister { get; } = new(
        id: "HL0211",
        title: "レジスタが重複しています",
        messageFormat: "レジスタ '{0}' は '{1}' でも使われています。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "同じレジスタを 2 つ以上の宣言が指定しています。"
            + "どちらが使われるかは保証されず、コンパイルに失敗します。"
            + "レジスタの番号を振り直してください。",
        helpLinkUri: DocumentationLinks.For("HL0211"));

    /// <summary>配列の添字が範囲を超えている。</summary>
    /// <remarks>
    /// 添字も長さも数字で書かれている場合だけ判定する。
    /// 変数の添字は実行時に決まるので、ここでは何も言えない。
    /// </remarks>
    public static DiagnosticDescriptor ArrayIndexOutOfBounds { get; } = new(
        id: "HL0370",
        title: "配列の添字が範囲を超えています",
        messageFormat: "'{0}' の長さは {1} ですが、{2} 番目を読もうとしています。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "配列の長さを超えた添字を書いています。"
            + "添字は 0 始まりであることに注意してください。",
        helpLinkUri: DocumentationLinks.For("HL0370"));

    /// <summary>整数のリテラルが 32 ビットに収まらない。</summary>
    /// <remarks>
    /// <b>字句解析ツールは値を検査しない。</b>
    /// リテラルとして読めることだけを保証し、範囲の検査はここへ委ねている。
    /// </remarks>
    public static DiagnosticDescriptor LiteralTruncated { get; } = new(
        id: "HL0371",
        title: "整数のリテラルが 32 ビットに収まりません",
        messageFormat: "'{0}' は 32 ビットに収まらないため、切り詰められます。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "整数のリテラルが 32 ビットで表せる範囲を超えています。"
            + "上位の桁が捨てられ、書いた値とは違う値になります。"
            + "16 進・8 進・10 進のいずれも同じです。",
        helpLinkUri: DocumentationLinks.For("HL0371"));

    /// <summary>配列の仮引数に、形の合わない実引数を渡している。</summary>
    /// <remarks>
    /// 配列は要素の型と長さの両方が一致していなければならない。
    /// 成分を減らす変換も、長さの違う配列の受け渡しも許されない。
    /// </remarks>
    public static DiagnosticDescriptor ArrayArgumentMismatch { get; } = new(
        id: "HL0344",
        title: "配列の引数が合いません",
        messageFormat: "関数 '{0}' の第 {1} 引数は {2} ですが、{3}。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "配列の仮引数に、形の合わない実引数を渡しています。"
            + "配列は要素の型と長さの両方が一致していなければなりません。"
            + "配列でない値を渡すこともできません。",
        helpLinkUri: DocumentationLinks.For("HL0344"));

    /// <summary>シェーダーステージの指定が無い。</summary>
    public static DiagnosticDescriptor MissingShaderStage { get; } = new(
        id: "HL0302",
        title: "シェーダーステージが指定されていません",
        messageFormat: "このコードブロックに #pragma vertex / #pragma fragment がありません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "どの関数を頂点シェーダー・フラグメントシェーダーとして使うかが指定されていません。"
            + "この Pass はコンパイルできません。"
            + "共通コード片 (HLSLINCLUDE) に書いたつもりの #pragma が、"
            + "別の SubShader に属していて届いていない場合によく起きます。",
        helpLinkUri: DocumentationLinks.For("HL0302"));
}
