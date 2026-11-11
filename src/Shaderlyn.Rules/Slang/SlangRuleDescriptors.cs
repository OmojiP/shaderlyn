using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang にしか無いルール (SG) の定義。
/// </summary>
/// <remarks>
/// どれも slangc 2026.19 が誤りにするもので、slangc に読ませて確かめた。
/// 番号は、同じ種類の GLSL のルール (GL) にそろえる。
/// </remarks>
internal static class SlangRuleDescriptors
{
    private const string CorrectnessCategory = "Correctness";

    /// <summary>リテラルの書き方が正しくない。</summary>
    public static DiagnosticDescriptor InvalidLiteral { get; } = new(
        id: "SG0004",
        title: "リテラルの書き方が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "文字や文字列のリテラルに使えないエスケープ (\\q、桁の足りない \\u など) を書いているか、文字のリテラルの中身がちょうど 1 文字でないか、"
            + "64 ビットの整数に収まらない整数のリテラルを書いています。slangc は字句の誤り (E10001・E10007・E10008・E10009・E10012・E10013) にします。",
        helpLinkUri: DocumentationLinks.For("SG0004"));

    /// <summary>UTF-8 として読めないバイト。</summary>
    public static DiagnosticDescriptor InvalidUtf8 { get; } = new(
        id: "SG0005",
        title: "UTF-8 として読めないバイトがあります",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "ファイルに、UTF-8 として読めないバイトの並びがあります (Shift_JIS や Latin-1 で保存したファイルなど)。コメントの中も含みます。"
            + "slangc はソースを UTF-8 として読むので、字句の誤り (E10006) にします。",
        helpLinkUri: DocumentationLinks.For("SG0005"));

    /// <summary>その場所に書けない宣言。</summary>
    public static DiagnosticDescriptor DeclarationNotAllowed { get; } = new(
        id: "SG0101",
        title: "この宣言はここに書けません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "型を書く位置 (仮引数・戻り値・型引数・typealias・as / is など) に構造体を定義しているか、"
            + "その入れ物の中に書けない宣言 (構造体の中の namespace・extension・interface、インターフェイスの中の enum・struct、"
            + "ファイルの直下の __init・associatedtype、関数の中の関数の定義、extension とインターフェイスの中の static でないフィールドなど) を書いているか、"
            + "extension のジェネリックの仮引数に既定値を書いているか、名前の無い enum class か、型と extension の中の演算子 (operator() を除く) か、インターフェイスの __init と property の要件の本体か、[COM] のインターフェイスの associatedtype を書いています。slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0101"));

    /// <summary>修飾子をその宣言に書けない。</summary>
    public static DiagnosticDescriptor ModifierNotAllowed { get; } = new(
        id: "SG0102",
        title: "この修飾子はここに書けません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "関数の外の static の無い const か constexpr の変数、初期値を書いた uniform の変数、局所変数の groupshared、"
            + "構造体のフィールドの __ref / __constref を書いているか、同じ修飾子・1 つしか書けない属性 ([numthreads] など) を 2 度書いているか、"
            + "可視性 (public・internal・private) を 2 つ書いているか、初期値を書いた groupshared、ポインターの coherent / globallycoherent / constexpr、typedef の volatile、"
            + "static の特殊化定数、push constant でもある特殊化定数、標準のモジュールにだけ書ける修飾子 (__magic_type など)、インターフェイスの要件でない関数の [MaybeDifferentiable]、インターフェイスの要件の [TreatAsDifferentiable] を書いています。slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0102"));

    /// <summary>レジスタの種類が資源の型と合わない。</summary>
    public static DiagnosticDescriptor RegisterMismatch { get; } = new(
        id: "SG0103",
        title: "レジスタの種類が資源の型と合いません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "register(…) の種類 (t・u・s・b) が、資源の型と合いません。t は読むだけの資源 (Texture2D・StructuredBuffer など)、u は書ける資源 (RW…)、"
            + "s はサンプラー、b は定数バッファーです。slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0103"));

    /// <summary>形の合わない値どうしの演算子。</summary>
    public static DiagnosticDescriptor WrongOperandTypes { get; } = new(
        id: "SG0301",
        title: "この型には演算子を使えません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "組み込みの演算子 (算術・比較・ビット・シフト・&& / ||) か複合代入を、形 (ベクトルの長さ、行列の行と列) の違う値どうしに使っています。"
            + "HLSL は短い方に切り詰めますが、Slang は組み込みの演算子が見つからないので、slangc はコンパイルの誤りにします。"
            + "ビット演算とシフト (& | ^ ~ << >>) を浮動小数の値に使っているものと、演算子を定義していない構造体の値に組み込みの演算子を使っているもの、?: の 2 つの枝の型が合わないもの、ポインターでない構造体の値への ->、スカラーの値への添字、関数の名前の sizeof、関わりの無い具体的な型どうしの is / as、as の右のインターフェイス、制約の無いジェネリックの型の値の is (右がインターフェイス) と算術の演算子も報告します。",
        helpLinkUri: DocumentationLinks.For("SG0301"));

    /// <summary>書き換えられないメソッドの中で、フィールドに書き込んだ。</summary>
    public static DiagnosticDescriptor NotWritable { get; } = new(
        id: "SG0309",
        title: "書き込めない値です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "[mutating] を付けていない構造体 (かクラス) のメソッドの中で、自分のフィールドに代入しているか、++ / -- か複合代入で書き換えています。"
            + "Slang のメソッドは既定で自分を書き換えられないので、slangc はコンパイルの誤り (E30011・E30047) にします。"
            + "読むだけの資源 (StructuredBuffer・Texture2D など) の要素への書き込み (Interlocked の書き込み先・out の実引数・& を含む) も報告します。Access::Read のポインターの指す先と、get だけの property への書き込みも報告します。",
        helpLinkUri: DocumentationLinks.For("SG0309"));

    /// <summary>条件がスカラーでない。</summary>
    public static DiagnosticDescriptor ScalarConditionExpected { get; } = new(
        id: "SG0311",
        title: "条件にはスカラーの値が要ります",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "if・while・for・do の条件にベクトル・行列・構造体・資源の型の値を書いているか、?: の条件か ! の値に構造体・資源の型の値を書いています。"
            + "条件にできるのはスカラー (bool・int など) だけなので、slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0311"));

    /// <summary>static const の初期値が定数式でない。</summary>
    public static DiagnosticDescriptor StaticConstNotConstant { get; } = new(
        id: "SG0316",
        title: "static const の初期値には定数式が要ります",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "関数の外の static const の初期値に、uniform・定数バッファーのメンバー・関数の外の変数の値か、利用者の関数やメソッドの呼び出しを使っています。slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0316"));

    /// <summary>配列にできない型の配列。</summary>
    public static DiagnosticDescriptor ArrayOfNonAddressable { get; } = new(
        id: "SG0403",
        title: "この型の配列は作れません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "ParameterBlock・大きさを決めない配列 (x[])・それを含む構造体の配列を宣言しています。それらはアドレスを持たない型なので、配列にできません。slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0403"));

    /// <summary>資源を包めない型に資源を入れた。</summary>
    public static DiagnosticDescriptor ResourceInContainer { get; } = new(
        id: "SG0404",
        title: "この型は資源を包めません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "StructuredBuffer (RW・Append・Consume を含む) か Optional の型引数に、資源の型 (テクスチャ・サンプラー・バッファー・ParameterBlock・ConstantBuffer) か、"
            + "それをメンバーに持つ構造体を書いています。slangc はコンパイルの誤り (E38204・E30902) にします。",
        helpLinkUri: DocumentationLinks.For("SG0404"));

    /// <summary>フィールドにできない型。</summary>
    public static DiagnosticDescriptor ReferenceField { get; } = new(
        id: "SG0406",
        title: "この型はフィールドにできません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "構造体のフィールドの型に、参照の型 (Ref<T>・RefParam<T>・BorrowInParam<T>・OutParam<T>・BorrowInOutParam<T>) を書いています。"
            + "参照は値を持たないので、slangc はコンパイルの誤り (E30030) にします。",
        helpLinkUri: DocumentationLinks.For("SG0406"));

    /// <summary>テクスチャの要素にできない型。</summary>
    public static DiagnosticDescriptor InvalidTexelType { get; } = new(
        id: "SG0405",
        title: "テクスチャの要素にできない型です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "テクスチャ (Texture2D・RWTexture2D・Sampler2D など) か型付きバッファー (Buffer・RWBuffer) の要素の型に、行列・構造体・bool・資源の型を書いています。"
            + "要素にできるのは bool でないスカラーとベクトル (ITexelElement) だけなので、slangc はコンパイルの誤り (E38029) にします。",
        helpLinkUri: DocumentationLinks.For("SG0405"));

    /// <summary>[NoDiscard] の関数の戻り値を捨てた。</summary>
    public static DiagnosticDescriptor DiscardedResult { get; } = new(
        id: "SG0338",
        title: "戻り値を使わなければならない関数です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "[NoDiscard] を書いた関数・メソッドの戻り値を捨てています (文として呼ぶ、for の更新の式、括弧・?: の枝・&& と || の右に書いて捨てる)。slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0338"));

    /// <summary>取り除かれた宣言を使った。</summary>
    public static DiagnosticDescriptor RemovedDeclaration { get; } = new(
        id: "SG0341",
        title: "取り除かれた宣言を使っています",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "[RemovedSince(版, \"説明\")] を書いた宣言を使っています。その版の言語 (#language slang 2026 など。書かなければ 2025) から取り除かれたので、slangc はコンパイルの誤りにします。"
            + "言語のバージョン 2026 で取り除かれた書き方 (ベクトルを含んで成分の足りない値の作成と並び。float4(v.xy, 1)) も報告します。",
        helpLinkUri: DocumentationLinks.For("SG0341"));

    /// <summary>インターフェイスの適合が循環している。</summary>
    public static DiagnosticDescriptor CyclicConformance { get; } = new(
        id: "SG0342",
        title: "インターフェイスの適合が循環しています",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "インターフェイスに適合する構造体のフィールドに、たどると同じインターフェイスへ戻るインターフェイス型の値があります。"
            + "動的なディスパッチでは値の大きさが決まらないので、slangc はコンパイルの誤り (E41002・E41003) にします。",
        helpLinkUri: DocumentationLinks.For("SG0342"));

    /// <summary>アクセスできない宣言を使った。</summary>
    public static DiagnosticDescriptor NotAccessible { get; } = new(
        id: "SG0401",
        title: "その宣言にはアクセスできません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "構造体の private のメンバー (フィールド・メソッド) をその構造体の外から使っているか、import した別のモジュールの public でない宣言 (書かなければ internal) を使っています。"
            + "private のメンバーはその構造体 (と中の構造体) の中で、internal の宣言はそのモジュールの中でだけ使えます。slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0401"));

    /// <summary>可視性の書き方が正しくない。</summary>
    public static DiagnosticDescriptor InvalidVisibility { get; } = new(
        id: "SG0402",
        title: "可視性の書き方が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "関数の外の宣言を private にしているか (private は型のメンバーにだけ書けます)、public でない構造体のメンバーを public にしているか、"
            + "public の宣言 (関数・変数・typealias・public の構造体の public のメンバー) が public でない型を使っています。slangc はコンパイルの誤り (E30603・E30601・E30604) にします。",
        helpLinkUri: DocumentationLinks.For("SG0402"));

    /// <summary>定数の初期値が、自分自身に依っている。</summary>
    public static DiagnosticDescriptor CyclicConstant { get; } = new(
        id: "SG0317",
        title: "定数の初期値が自分自身に依っています",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "static const の初期値が、直接か、ほかの static const を通して自分自身の値を使っています。値が決まらないので、slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0317"));

    /// <summary>switch と break / continue の書き方の誤り。</summary>
    public static DiagnosticDescriptor SwitchStatement { get; } = new(
        id: "SG0321",
        title: "switch と break / continue の書き方が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "case か default を switch の本体の直下でない場所 (switch の外、case の中の { } の中) に書いているか、"
            + "break をループと switch の外に、continue をループの外に書いているか、1 つの switch に default を 2 つ、同じ値の case を 2 つ書いているか、"
            + "switch の値が整数でも列挙型でもありません。slangc はコンパイルの誤り (E30003・E30004・E30602・E30605・E30607) にします。",
        helpLinkUri: DocumentationLinks.For("SG0321"));

    /// <summary>属性の値が正しくない。</summary>
    public static DiagnosticDescriptor InvalidAttributeValue { get; } = new(
        id: "SG0304",
        title: "属性の値が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "[numthreads] に 1 より小さい値を、[WaveSize] に 4 から 128 までの 2 の累乗でない値を、[COM] に GUID でない文字列を、"
            + "[RemovedSince] に -1 から 9999 の外の版を書いているか、[DerivativeGroupLinear] / [DerivativeGroupQuad] の [numthreads] のスレッドの数が合いません。"
            + "slangc はコンパイルの誤り (E31102・E31103・E31123・E31209・E31211・E31210) にします。",
        helpLinkUri: DocumentationLinks.For("SG0304"));

    /// <summary>初期値の無い static const。</summary>
    public static DiagnosticDescriptor StaticConstWithoutInitializer { get; } = new(
        id: "SG0318",
        title: "static const には初期値が要ります",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "関数の外か構造体の static const の変数に、初期値を書いていません。slangc はコンパイルの誤り (E31225) にします。"
            + "インターフェイスの要件 (static const int N;) と extern の変数は除きます。",
        helpLinkUri: DocumentationLinks.For("SG0318"));

    /// <summary>その宣言に使えない型。</summary>
    public static DiagnosticDescriptor InvalidDeclarationType { get; } = new(
        id: "SG0407",
        title: "この宣言には使えない型です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "変数の型に void を、ビットフィールドに整数でない型か型の幅を超える幅を、列挙型の基の型に整数でない型を、"
            + "特殊化定数にスカラーでも列挙型でもない型を使っているか、構造体がフィールドを通して自分を値として含んでいます。slangc はコンパイルの誤り (E30009・E31300・E31301・E32000・E31218・E41001) にします。",
        helpLinkUri: DocumentationLinks.For("SG0407"));

    /// <summary>大きさの決まらない型の使い方の誤り。</summary>
    public static DiagnosticDescriptor UnsizedType { get; } = new(
        id: "SG0308",
        title: "大きさの決まらない型はここに使えません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "大きさを書かない配列 (int a[]) か、それを最後のフィールドに持つ構造体を、使えない場所に使っています。"
            + "構造体の最後でないフィールド、ほかの構造体のフィールド、局所変数 (初期値の無いもの)、StructuredBuffer の要素、定数バッファーのメンバーには使えません。"
            + "slangc はコンパイルの誤り (E30070・E30071・E30072・E30031・E31215) にします。",
        helpLinkUri: DocumentationLinks.For("SG0308"));

    /// <summary>static の関数から、static でないメンバーを使っている。</summary>
    public static DiagnosticDescriptor NonStaticMemberFromStatic { get; } = new(
        id: "SG0344",
        title: "static でないメンバーは値から使ってください",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "static のメソッドの中で this か、修飾しない static でないメソッドやフィールドを使っているか、型の名前から static でないフィールドを使っています (Color.Red の Color が構造体)。"
            + "static のメソッドと型には、そのメンバーを持つ値がありません。slangc はコンパイルの誤り (E30100・E30120) にします。",
        helpLinkUri: DocumentationLinks.For("SG0344"));

    /// <summary>throw と try の書き方の誤り。</summary>
    public static DiagnosticDescriptor ErrorHandling { get; } = new(
        id: "SG0345",
        title: "throw と try の書き方が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "throws を書いていない関数に、catch で受けない throw か try を書いているか、defer の中に catch で受けない throw か try を書いているか、"
            + "関数の throws の列挙型と違う列挙型の値を throw しています。slangc はコンパイルの誤り (E30093・E30113・E30114・E30115・E30116) にします。",
        helpLinkUri: DocumentationLinks.For("SG0345"));

    /// <summary>new を書かない class の作成。</summary>
    public static DiagnosticDescriptor ClassWithoutNew { get; } = new(
        id: "SG0346",
        title: "class の値は new で作ってください",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "class の値を、new を書かずに作っています (MyClass()・(MyClass)4)。class は参照の型なので、slangc は new で作るものとしてコンパイルの誤り (E30066) にします。",
        helpLinkUri: DocumentationLinks.For("SG0346"));

    /// <summary>継承と override の書き方の誤り。</summary>
    public static DiagnosticDescriptor InheritanceAndOverride { get; } = new(
        id: "SG0347",
        title: "継承と override の書き方が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "構造体が 2 つ以上の構造体を継承しているか、基底の構造体を継承の並びの最初に書いていないか、"
            + "インターフェイスの既定の実装を置き換えるメソッドに override を書いていないか、基底に当たるメソッドの無いメソッドに override を書いているか、構造体が class を、class が構造体を、#lang 2026 の構造体が構造体を継承しているか、"
            + "インターフェイスの要件に override を書いているか、[COM] のインターフェイスを構造体や [COM] でないインターフェイスが継承しています。"
            + "slangc はコンパイルの誤り (E30820・E30853・E30854・E30811・E30814・E30312・E31124・E31126) にします。",
        helpLinkUri: DocumentationLinks.For("SG0347"));

    /// <summary>property と __subscript の書き方の誤り。</summary>
    public static DiagnosticDescriptor AccessorForm { get; } = new(
        id: "SG0348",
        title: "property と __subscript の書き方が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "__subscript に -> で戻り値の型を書いていないか、property の get に仮引数を、set に 2 つ以上の仮引数か property と違う型の仮引数を、"
            + "accessor に型の無い仮引数を書いているか、インターフェイスの property や __subscript の accessor の片方だけに既定の実装を書いています。slangc はコンパイルの誤り (E30901・E31112・E31108・E31109・E30621・E30318) にします。",
        helpLinkUri: DocumentationLinks.For("SG0348"));

    /// <summary>ジェネリックの制約を満たさない型引数。</summary>
    public static DiagnosticDescriptor ConstraintNotSatisfied { get; } = new(
        id: "SG0349",
        title: "型引数がジェネリックの制約を満たしません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "ジェネリックの関数か型の型の仮引数に、制約 (<T : IFoo>・where T : IFoo) のインターフェイスに適合しない型を渡しています。"
            + "型が適合するのは、継承の並びか extension にそのインターフェイス (かそれを継承したインターフェイス) を書いたときだけです (基底の構造体の適合は受け継ぎません)。"
            + "slangc はコンパイルの誤り (E38029) にします。",
        helpLinkUri: DocumentationLinks.For("SG0349"));

    /// <summary>微分する関数の、繰り返しの回数を決められないループ。</summary>
    public static DiagnosticDescriptor LoopNeedsIterationCount { get; } = new(
        id: "SG0350",
        title: "微分する関数のループには、繰り返しの回数が要ります",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "[Differentiable]・[ForwardDifferentiable]・[BackwardDifferentiable] の関数の中に、繰り返しの回数を決められないループ (while・do、変数を定数と比べて定数ずつ増減する形でない for、"
            + "本体でその変数を書き換える for) を、[MaxIters(n)] も [ForceUnroll] も付けずに書いています。微分のためにループの回数が要るので、slangc はコンパイルの誤り (E30510) にします。",
        helpLinkUri: DocumentationLinks.For("SG0350"));

    /// <summary>初期化子の並びが型に合わない。</summary>
    public static DiagnosticDescriptor InitializerListMismatch { get; } = new(
        id: "SG0351",
        title: "初期化子の並びがこの型に合いません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "初期化子の並び ({ … }) が、その型に合いません。__init を書いた構造体では並びが __init の呼び出しになるので、要素の数を受け取る __init が要ります。"
            + "__init を持つ構造体を継承した構造体と、資源の型の変数には並びを使えません。資源の型のフィールドは、並びで埋めなければなりません。"
            + "slangc はコンパイルの誤り (E30513・E39999) にします。",
        helpLinkUri: DocumentationLinks.For("SG0351"));

    /// <summary>コンパイル時の定数でなければならない実引数。</summary>
    public static DiagnosticDescriptor ArgumentMustBeConstant { get; } = new(
        id: "SG0352",
        title: "この実引数はコンパイル時の定数でなければなりません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "テクスチャの Sample・SampleLevel・SampleGrad などの offset の実引数に、定数でない値 (定数バッファーのメンバー、uniform、ループの変数、代入し直す局所変数) を使っています。"
            + "offset は命令に埋め込むので、slangc はコンパイルの誤り (E40012) にします。__floatAsInt の実引数も、浮動小数のリテラルでなければなりません (E30084・E30085)。",
        helpLinkUri: DocumentationLinks.For("SG0352"));

    /// <summary>構文の誤りのあるモジュールの import。</summary>
    public static DiagnosticDescriptor ModuleHasErrors { get; } = new(
        id: "SG0353",
        title: "取り込んだモジュールに誤りがあります",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "import したモジュール (それがたどって取り込んだモジュールを含む) に構文の誤りがあります。slangc はそのモジュールを読めないので、import の行もコンパイルの誤り (E39999) にします。",
        helpLinkUri: DocumentationLinks.For("SG0353"));

    /// <summary>空でありうる値の並びの使い方。</summary>
    public static DiagnosticDescriptor PackMayBeEmpty { get; } = new(
        id: "SG0354",
        title: "空でありうる並びには使えません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "where nonempty(T) の無い可変長の型の仮引数 (each T) に __first・__last を使っているか、where nonempty(…) を求める関数へ空でありうる並びか空の型引数を渡しているか、"
            + "nonempty の制約を optional にしているか、countof(…) の制約の書き方 (== と左の countof(…)・この宣言の並び・定数の右辺) が正しくないか、型引数の数と並びの数が合いません。"
            + "slangc はコンパイルの誤り (E30411・E30412・E30415・E30440・E30430～E30437) にします。",
        helpLinkUri: DocumentationLinks.For("SG0354"));

    /// <summary>ラムダ式の書き方の誤り。</summary>
    public static DiagnosticDescriptor LambdaForm { get; } = new(
        id: "SG0355",
        title: "ラムダ式の書き方が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "戻り値の型を書かないラムダ式が、型の違う値 (整数と浮動小数) を返しているか、コピーできない型 (RayQuery・HitObject) の変数を取り込んでいます。"
            + "slangc はコンパイルの誤り (E30111・E30112) にします。",
        helpLinkUri: DocumentationLinks.For("SG0355"));

    /// <summary>アドレスを取れない値。</summary>
    public static DiagnosticDescriptor AddressNotTakable { get; } = new(
        id: "SG0356",
        title: "この値のアドレスは取れません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "読むだけの資源 (StructuredBuffer・Texture など) の要素のアドレスを &x か __getAddress(x) で取っているか、GPU のコンパイル先で局所変数か仮引数のアドレスを __getAddress で取っています。"
            + "slangc はコンパイルの誤り (E30079・E31160) にします。",
        helpLinkUri: DocumentationLinks.For("SG0356"));

    /// <summary>配列の大きさの式の誤り。</summary>
    public static DiagnosticDescriptor InvalidArraySize { get; } = new(
        id: "SG0357",
        title: "配列の大きさが正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "配列の大きさの式が 0 で割っているか、配列の大きさを負の数にしています。slangc はコンパイルの誤り (E30002・E30025) にします。",
        helpLinkUri: DocumentationLinks.For("SG0357"));

    /// <summary>ジェネリックの仮引数の書き方の誤り。</summary>
    public static DiagnosticDescriptor GenericParameterForm { get; } = new(
        id: "SG0358",
        title: "ジェネリックの仮引数の書き方が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "値の仮引数 (let N) に型を書いていないか整数・列挙型・bool でない型を書いているか、制約にインターフェイスでない型 (int・構造体) を書いているか、"
            + "制約の型引数が後ろの仮引数を指しているか、extension の型の仮引数が拡張する型に出てこないか、型引数の要るジェネリックの型 (Buffer・Tuple など) に型引数を書いていないか、型引数の数が合わないか、"
            + "可変長の並びの後ろに仮引数を書いているか、既定の値が後ろの仮引数を指しているか、where の左が仮引数でないか、== の右がインターフェイスか、"
            + "nonempty(…) にこの宣言の可変長の並びでないものを書いているか、インターフェイスを extension で広げています。"
            + "slangc はコンパイルの誤り (E30624・E30625・E30401・E30117・E30855・E30408・E30441・E30522・E30122・E30402・E30404・E30413・E30416・E30852) にします。",
        helpLinkUri: DocumentationLinks.For("SG0358"));

    /// <summary>インターフェイスの要件を満たしていない。</summary>
    public static DiagnosticDescriptor InterfaceRequirement { get; } = new(
        id: "SG0359",
        title: "インターフェイスの要件を満たしていません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "インターフェイスに適合する型に、インターフェイスの要件 (本体の無いメソッド) が無いか、要件と [mutating]・static が合わないメソッドで満たしています。"
            + "slangc はコンパイルの誤り (E38100・E38105) にします。",
        helpLinkUri: DocumentationLinks.For("SG0359"));

    /// <summary>自分で書いた微分の関数と no_diff の書き方の誤り。</summary>
    public static DiagnosticDescriptor CustomDerivative { get; } = new(
        id: "SG0360",
        title: "微分の関数の書き方が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "[ForwardDerivative] / [BackwardDerivative] などで指した微分の関数の仮引数の数か型が、元の関数から決まる形と合わないか、static の関数の微分に static でないメソッドを指しているか、"
            + "no_diff を関数の呼び出しでないものか、微分しない関数の中で使っているか、微分する関数の中で後ろ向きの微分を呼んでいるか、let の DifferentialPair を後ろ向きの微分に渡しているか、"
            + "微分する仮引数に __constref を書いているか、インターフェイスの要件と微分の関数を当てています。slangc はコンパイルの誤り (E31149・E31150・E31156・E38031・E38033・E38037・E30047・E38034・E38036・E31152・E31153) にします。",
        helpLinkUri: DocumentationLinks.For("SG0360"));

    /// <summary>微分の型と [DerivativeMember] の書き方の誤り。</summary>
    public static DiagnosticDescriptor DifferentialType { get; } = new(
        id: "SG0361",
        title: "微分の型の書き方が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "IDifferentiable の型の微分の型 (typealias Differential) が自分でないのに、微分するフィールドに [DerivativeMember] も no_diff も書いていないか、"
            + "[DerivativeMember] に微分の型のフィールドでないものを書いているか、微分しない型のフィールドか関数の外の変数に [DerivativeMember] を書いています。"
            + "slangc はコンパイルの誤り (E30121・E31130・E30310・E31132) にします。",
        helpLinkUri: DocumentationLinks.For("SG0361"));

    /// <summary>条件の中の代入。</summary>
    public static DiagnosticDescriptor AssignmentInCondition { get; } = new(
        id: "SG0343",
        title: "条件に代入は書けません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "if・while・for・do の条件に、括弧で包まない代入 (if (a = 0)) を書いています。'==' の書き間違いを防ぐため、slangc はコンパイルの誤り (E30057) にします。"
            + "代入した値を条件に使うなら、括弧で包んでください (if ((a = 0)))。",
        helpLinkUri: DocumentationLinks.For("SG0343"));
}
