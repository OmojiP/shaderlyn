using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Hlsl;

/// <summary>
/// HLSL の字句解析・プリプロセス・構文解析が報告するルール定義。
/// </summary>
internal static class HlslDescriptors
{
    /// <summary>構文エラー。</summary>
    public static DiagnosticDescriptor SyntaxError { get; } = new(
        id: "HL0001",
        title: "構文エラー",
        messageFormat: "{0}",
        category: "Syntax",
        defaultSeverity: DiagnosticSeverity.Error,
        description: "HLSL の構文として解釈できない記述があります。"
            + "マクロ展開の結果として発生する場合もあるため、"
            + "該当箇所にマクロが使われている場合はその定義も確認してください。",
        helpLinkUri: DocumentationLinks.For("HL0001"));

    /// <summary>プリプロセッサ指令の誤り。</summary>
    public static DiagnosticDescriptor PreprocessorError { get; } = new(
        id: "HL0002",
        title: "プリプロセッサ指令の誤り",
        messageFormat: "{0}",
        category: "Syntax",
        defaultSeverity: DiagnosticSeverity.Error,
        description: "プリプロセッサ指令が正しく書かれていません。"
            + "条件分岐の対応が取れていない場合、以降のコードが意図と異なる形で"
            + "有効化または無効化される可能性があります。",
        helpLinkUri: DocumentationLinks.For("HL0002"));

    /// <summary>この解析ツールが解釈できない宣言。</summary>
    /// <remarks>
    /// <para>
    /// <b>HLSL の <c>class</c> と <c>interface</c> は、この解析ツールが持っていない概念である。</b>
    /// 型の評価器が持つのは <c>HlslTypeClass</c> の粗い分類と、
    /// 構造体のフィールド名から型名への対応だけである。
    /// 「インターフェイスが要求するメソッド一式」も「クラスが実装したメソッド一式」も
    /// どこにも作られないため、両者を突き合わせる検査 (HLSL の X3108 / X3126) は行えない。
    /// </para>
    /// <para>
    /// <b>何も伝えずに変数宣言として取り込んでいたほうが危険だった。</b>
    /// <c>interface IBase { ... };</c> は
    /// 「<c>interface</c> 型の変数 <c>IBase</c>」として解析され、
    /// 波括弧の中身は <c>sampler_state { ... }</c> を読み飛ばす経路
    /// (<c>ParseDeclaratorRest</c>) が丸ごと捨てていた。
    /// 診断は 1 件も出ず、中にあるメソッドもフィールドも存在しないものとして扱われる。
    /// 指摘が出ないことを「問題が無い」と受け取られてはならない。
    /// </para>
    /// <para>
    /// <b>この診断はブロックの解析結果そのものを信用できないものにする。</b>
    /// <c>ShaderCompilation.HasCompleteDependencies</c> は
    /// ブロックの診断が空であることを条件にしているため、
    /// 「無いこと」を根拠とするルールはこのブロックでは何も報告しなくなり、
    /// 見送ったことは <c>SL0002</c> が伝える。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor UnsupportedDeclaration { get; } = new(
        id: "HL0003",
        title: "Unity のシェーダーでは使えない構文です",
        messageFormat: "{0} は Unity のシェーダーでは使えません。",
        category: "Syntax",
        defaultSeverity: DiagnosticSeverity.Error,
        description: "class / interface / enum / template の宣言、operator による演算子の多重定義、"
            + "構造体のビットフィールド、#line 指令は、Unity のシェーダーのコンパイルでエラーになります。"
            + "構文は読み飛ばし、そのコードブロックでは「宣言が見つからない」ことを根拠とする検査を行いません (SL0002)。"
            + "struct・定数・通常の関数で書き直してください。",
        helpLinkUri: DocumentationLinks.For("HL0003"));

    /// <summary><c>#ifdef</c> / <c>#ifndef</c> の名前の後ろに記述がある。</summary>
    /// <remarks>
    /// <para>
    /// <b>名前の後ろは、何も伝えられずに捨てられる。</b>
    /// <c>#ifdef A0 || A1</c> は <c>#ifdef A0</c> と同じ意味になり、<c>A1</c> はどこにも効かない。
    /// コンパイルは通るので、書いた本人は「A0 か A1 のとき」のつもりのまま気づけない。
    /// </para>
    /// <para>
    /// 捨てた名前は、条件で参照したものとして数えない。見られていない以上、
    /// <c>HL0331</c> (宣言したシンボルが条件に現れない) はそのまま正しい。
    /// ただし <c>HL0331</c> はシンボルの宣言を指すので、原因の行が分からない。
    /// この診断は書いた行そのものを指す。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor ExtraTokensAfterConditionName { get; } = new(
        id: "HL0004",
        title: "#ifdef / #ifndef の後ろの記述は無視されます",
        messageFormat: "#{0} はシンボルを 1 つだけ解析します。後ろの '{1}' は無視されます。{2}",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "#ifdef / #ifndef は直後の名前 1 つだけを条件にし、その後ろの記述を無視します。"
            + "#ifdef A || B は #ifdef A と解釈されます。"
            + "複数の名前を条件にするときは #if defined(A) || defined(B) と書いてください。",
        helpLinkUri: DocumentationLinks.For("HL0004"));

    /// <summary>include の循環参照。</summary>
    public static DiagnosticDescriptor CircularInclude { get; } = new(
        id: "HL0320",
        title: "include が循環しています",
        messageFormat: "'{0}' の include が循環しています。",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        description: "include が自分自身を直接または間接に取り込んでいます。"
            + "インクルードガード (#ifndef ... #define ... #endif) が"
            + "書かれていないか、正しく機能していない可能性があります。",
        helpLinkUri: DocumentationLinks.For("HL0320"));

    /// <summary>宣言されていないシンボルを条件に使っている。</summary>
    /// <remarks>
    /// <b>その分岐は決して有効にならない。</b>
    /// シンボルはシェーダー自身が <c>#pragma shader_feature</c> などで
    /// 宣言して初めてマテリアルから切り替えられる。
    /// 綴りを 1 文字誤っただけで、その機能が丸ごと入らないまま出荷されうる。
    /// </remarks>
    public static DiagnosticDescriptor UndeclaredSymbol { get; } = new(
        id: "HL0330",
        title: "未宣言のシンボルが使用されています",
        messageFormat: "シンボル '{0}' はこのシェーダーで宣言されていません。この分岐は有効になりません。",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "#ifdef などで参照しているシンボルが、"
            + "#pragma shader_feature / multi_compile のどれでも宣言されていません。"
            + "シンボルは宣言して初めてマテリアルやスクリプトから切り替えられるため、"
            + "この分岐のコードは決してコンパイルに含まれません。"
            + "スペルミスか、pragma の書き忘れです。",
        helpLinkUri: DocumentationLinks.For("HL0330"));

    /// <summary>宣言したシンボルがどこでも使われていない。</summary>
    /// <remarks>
    /// シンボルは 1 つ増えるごとにバリアントの数を倍にする。
    /// 使っていない宣言はビルド時間とメモリをそのぶん消費する。
    /// </remarks>
    public static DiagnosticDescriptor UnusedSymbol { get; } = new(
        id: "HL0331",
        title: "宣言したシンボルが未使用です",
        messageFormat: "シンボル '{0}' は宣言されていますが、条件のどこにも現れません。",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        description: "#pragma shader_feature / multi_compile で宣言したシンボルが、"
            + "#ifdef などで一度も参照されていません。"
            + "シンボルは 1 つ増えるごとにバリアントの数を倍にするため、"
            + "使っていない宣言はビルド時間とメモリを無駄に消費します。",
        helpLinkUri: DocumentationLinks.For("HL0331"));

    /// <summary>include を解決できなかった。</summary>
    /// <remarks>
    /// <para>
    /// <b>取り込めないヘッダがあるなら、それは誤りである。</b>
    /// 解決できないヘッダの中身は、そこで定義されるマクロも型も宣言も分からない。
    /// その状態で出す指摘は、根拠が欠けたまま出す指摘である。
    /// かつては警告にして解析を続けていたが、
    /// 「Unity のヘッダが引けないまま精度だけが落ちた結果」を
    /// 利用者が信じてしまう形になっていた。
    /// </para>
    /// <para>
    /// <b>1 件も解決できないときは、原因はパスではなく環境である。</b>
    /// その場合はこの診断を 1 行ずつ並べるのではなく、
    /// <c>TOOL0004</c> がファイルごとに 1 件だけ、直し方とともに報告する。
    /// </para>
    /// <para>
    /// <c>PreprocessorOptions.ReportUnresolvedIncludes</c> は、
    /// プリプロセッサ単体を試すときに全件を見るための入口として残してある。
    /// </para>
    /// </remarks>
    public static DiagnosticDescriptor UnresolvedInclude { get; } = new(
        id: "HL0321",
        title: "include を解決できません",
        messageFormat: "include '{0}' を解決できませんでした。",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        description: "指定されたパスのファイルが見つかりませんでした。"
            + "探索パスの設定が不足しているか、パスの書き方が誤っている可能性があります。"
            + "解決できない include があると、その中で定義されるマクロや型が未知のままになり、"
            + "その先の検査は根拠を欠いたまま行うことになります。",
        helpLinkUri: DocumentationLinks.For("HL0321"));
}
