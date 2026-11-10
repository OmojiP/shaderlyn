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

    /// <summary>その場所に書けない宣言。</summary>
    public static DiagnosticDescriptor DeclarationNotAllowed { get; } = new(
        id: "SG0101",
        title: "この宣言はここに書けません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "型を書く位置 (仮引数・戻り値・型引数・typealias・as / is など) に構造体を定義しているか、"
            + "その入れ物の中に書けない宣言 (構造体の中の namespace・extension・interface、インターフェイスの中の enum・struct、"
            + "ファイルの直下の __init・associatedtype、関数の中の関数の定義など) を書いています。slangc はコンパイルの誤りにします。",
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
            + "可視性 (public・internal・private) を 2 つ書いています。slangc はコンパイルの誤りにします。",
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
            + "HLSL は短い方に切り詰めますが、Slang は組み込みの演算子が見つからないので、slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0301"));

    /// <summary>書き換えられないメソッドの中で、フィールドに書き込んだ。</summary>
    public static DiagnosticDescriptor NotWritable { get; } = new(
        id: "SG0309",
        title: "書き込めない値です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "[mutating] を付けていない構造体 (かクラス) のメソッドの中で、自分のフィールドに代入しているか、++ / -- か複合代入で書き換えています。"
            + "Slang のメソッドは既定で自分を書き換えられないので、slangc はコンパイルの誤り (E30011・E30047) にします。",
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
        description: "[RemovedSince(版, \"説明\")] を書いた宣言を使っています。その版の言語 (#language slang 2026 など。書かなければ 2025) から取り除かれたので、slangc はコンパイルの誤りにします。",
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

    /// <summary>定数の初期値が、自分自身に依っている。</summary>
    public static DiagnosticDescriptor CyclicConstant { get; } = new(
        id: "SG0317",
        title: "定数の初期値が自分自身に依っています",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "static const の初期値が、直接か、ほかの static const を通して自分自身の値を使っています。値が決まらないので、slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0317"));

    /// <summary>switch の外の case / default。</summary>
    public static DiagnosticDescriptor SwitchLabelOutsideSwitch { get; } = new(
        id: "SG0321",
        title: "case と default は switch の本体にだけ書けます",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "case か default を、switch の本体の直下でない場所 (switch の外、case の中の { } の中) に書いています。slangc はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("SG0321"));
}
