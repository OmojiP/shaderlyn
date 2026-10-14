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

    /// <summary>環境が指定したエントリポイントが存在しない。</summary>
    /// <remarks>
    /// 文の <c>{0}</c> は指定した場所 (<see cref="Semantics.Environments.HlslEntryPoint.Origin"/>)。
    /// Unity なら <c>#pragma vertex</c> になり、v0.1 までと同じ文になる。
    /// </remarks>
    public static DiagnosticDescriptor EntryPointNotFound { get; } = new(
        id: "HL0301",
        title: "エントリポイントが見つかりません",
        messageFormat: "{0} が指定する関数 '{1}' が見つかりません。{2}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "エントリポイントに指定した名前の関数が定義されていません"
            + " (Unity なら #pragma vertex や #pragma fragment、設定ファイルでコンパイル単位を書く環境なら units の entry)。"
            + "関数名のスペルミスか、共通コード片の取り込み漏れです。"
            + "このコンパイル単位はコンパイルできず、シェーダー全体が使えなくなります。",
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
        messageFormat: "関数 '{0}' の定義が{1}存在しません。",
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
        title: "変数の宣言がその構成に存在しません",
        messageFormat: "{1}変数 '{0}' の宣言が{2}存在しません。",
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
        messageFormat: "関数 '{0}' の第 {1} 引数に {2} を指定できません (仮引数は {3})。{4}",
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
        title: "引数の個数が一致しません",
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
        messageFormat: "{0} に{1}は存在しません。{2}",
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
        messageFormat: "関数 '{0}' の第 {1} 引数は {2} です。{2} 引数には変数を指定してください。{3}",
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
        messageFormat: "{1} を {0} に変換できません。{2}",
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
        title: "return の値が戻り値の型と一致しません",

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
        title: "一部構成で成分が切り捨てられます",
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
        messageFormat: "関数 '{0}' は {1} を返しますが、値を返さない経路が存在します。{2}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "値を返す関数に、return を通らずに末尾へ到達する経路があります。",
        helpLinkUri: DocumentationLinks.For("HL0360"));

    /// <summary>波括弧による初期化の要素の個数が合わない。</summary>
    public static DiagnosticDescriptor InitializerCountMismatch { get; } = new(
        id: "HL0352",
        title: "初期化の要素の個数が一致しません",
        messageFormat: "{0} の初期化に要素を {1} 個書いていますが、{2} 個必要です。{3}",
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
        messageFormat: "'{0}' は {1} 行目にも宣言されています。",
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
        title: "コンストラクタに渡す成分の数が一致しません",
        messageFormat: "{0} のコンストラクタには成分を {1} 個渡してください (渡している成分は {2} 個です)。",
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
        messageFormat: "'{0}' の長さは {1} ですが、要素 {2} を指定しています。",
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
        messageFormat: "'{0}' は 32 bit に収まりません。下位 32 bit が使用されます。",
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
        title: "関数の配列型の引数が一致しません",
        messageFormat: "関数 '{0}' の第 {1} 引数は {2} ですが、{3}。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "配列の仮引数に、形の合わない実引数を渡しています。"
            + "配列は要素の型と長さの両方が一致していなければなりません。"
            + "配列でない値を渡すこともできません。",
        helpLinkUri: DocumentationLinks.For("HL0344"));

    /// <summary>template の型引数を、実引数から 1 つに決められない (HLSL 2021)。</summary>
    /// <remarks>
    /// 同じ型引数に当たる実引数の型は完全に一致しなければならない。
    /// 接尾辞の無い数 (<c>1.0</c>) は <c>float</c> とは別の型として推論される。DXC で確かめた。
    /// </remarks>
    public static DiagnosticDescriptor TemplateArgumentConflict { get; } = new(
        id: "HL0345",
        title: "template の型引数を実引数から決められません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "template の関数を型引数を書かずに呼ぶと、型引数は実引数の型から決まります (推論)。"
            + "同じ型引数に当たる実引数の型は完全に一致しなければならず、"
            + "float と half、float3 と float のような違いがあると呼び出せません。"
            + "接尾辞の無い数 (1.0 / 1) は float / int とは別の型として扱われます。",
        helpLinkUri: DocumentationLinks.For("HL0345"));

    /// <summary>構造体の値に、使えない演算子を使っている。または多重定義できない演算子を定義している。</summary>
    /// <remarks>
    /// 演算子は数値型の値にしか使えない。HLSL 2021 では、構造体の中で定義した演算子だけが構造体の左辺に使える。
    /// 条件演算子は構造体を選べない。<c>++</c> / <c>--</c> / 複合代入 / <c>&lt;&lt;</c> / <c>&gt;&gt;</c> は多重定義できない。
    /// どれも DXC で確かめた。
    /// </remarks>
    public static DiagnosticDescriptor StructOperatorNotAvailable { get; } = new(
        id: "HL0346",
        title: "構造体に使用できない演算子です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "HLSL の演算子は数値型 (スカラー・ベクトル・行列) の値にしか使えません。"
            + "HLSL 2021 では、構造体の中で operator+ などを定義すると、その構造体を左辺にして使えます。"
            + "条件演算子 (?:) は構造体の値を選べず、++ / -- / 複合代入 (+= など) / << / >> は多重定義できません。",
        helpLinkUri: DocumentationLinks.For("HL0346"));

    /// <summary><c>for</c> の初期化で宣言した変数を、ループの外で使っている (HLSL 2021 へ移せない)。</summary>
    /// <remarks>FXC と DXC 2021 より前は通るが、DXC 2021 はエラーにする。手元の FXC / DXC 1.8 で確かめた。</remarks>
    public static DiagnosticDescriptor LoopVariableOutsideLoop { get; } = new(
        id: "HL0354",
        title: "HLSL 2021 では for の変数をループの外で使用できません",
        messageFormat: "'{0}' は for 文の初期化で宣言した変数です。HLSL 2021 ではエラーになります。",
        category: "Portability",
        defaultSeverity: DiagnosticSeverity.Info,
        description: "for (int i = 0; ...) の i は、FXC と DXC 2021 より前ではループの後でも見えますが、HLSL 2021 では C++ と同じくループの中だけで見えます。"
            + "DXC の HLSL 2021 へ移すとき (Unity の DXC が 2021 になったときを含む) にエラーになるので、ループの前で宣言してください。",
        helpLinkUri: DocumentationLinks.For("HL0354"));

    /// <summary>HLSL 2021 で、ベクトルや行列に <c>&amp;&amp;</c> / <c>||</c> / <c>?:</c> を使っている。</summary>
    /// <remarks>HLSL 2021 ではこれらが短絡評価になり、スカラーにしか使えない。DXC 1.8 で確かめた。</remarks>
    public static DiagnosticDescriptor VectorLogicalOperator { get; } = new(
        id: "HL0349",
        title: "ベクトルに使用できない演算子です (HLSL 2021)",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "HLSL 2021 の && / || / ?: は短絡評価になり、スカラーにしか使えません。"
            + "成分ごとに計算するなら and() / or() / select() を使ってください。FXC と DXC 2021 より前は成分ごとに計算します。",
        helpLinkUri: DocumentationLinks.For("HL0349"));

    /// <summary>構造体の値を増減している (FXC では何もしない)。</summary>
    /// <remarks>
    /// FXC は構造体自身への <c>++</c> / <c>--</c> をエラーにせず、何もしないコードにする。
    /// DXC はどの版でもエラーにする (HL0346)。手元の FXC と、Unity 2022.3〜6.6 の FXC で確かめた。
    /// </remarks>
    public static DiagnosticDescriptor StructIncrementIgnored { get; } = new(
        id: "HL0348",
        title: "構造体の ++ / -- は無視されます",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "FXC は構造体の値への ++ / -- をエラーにせず、何もしません。値は変わらないので、意図した計算になりません。"
            + "DXC ではエラーになります (HL0346)。メンバーごとに増減してください。",
        helpLinkUri: DocumentationLinks.For("HL0348"));

    /// <summary>enum の値を、変換できない型との間でやり取りしている。または使えない演算子を使っている (HLSL 2021)。</summary>
    /// <remarks>
    /// enum の型へ入れられるのは同じ enum の値だけである。<c>enum class</c> の値は数へ暗黙に変換されず、
    /// <c>==</c> / <c>!=</c> (同じ enum どうし) のほかに演算子を使えない。DXC で確かめた。
    /// </remarks>
    public static DiagnosticDescriptor EnumValueMismatch { get; } = new(
        id: "HL0347",
        title: "enum の値の型が一致しません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "enum の型の変数や仮引数へ入れられるのは、同じ enum の値 (か、その型へ変換したもの) だけです。"
            + "数や別の enum の値は、型変換 ((Mode)1) を書かないと入れられません。"
            + "enum class の値は数へ暗黙に変換されず、== / != (同じ enum どうし) のほかに演算子を使えません。"
            + "ふつうの enum の値は数として使えますが、++ / -- は使えません。",
        helpLinkUri: DocumentationLinks.For("HL0347"));
}
