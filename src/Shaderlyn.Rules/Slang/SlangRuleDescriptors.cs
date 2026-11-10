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

    /// <summary>アクセスできない宣言を使った。</summary>
    public static DiagnosticDescriptor NotAccessible { get; } = new(
        id: "SG0401",
        title: "その宣言にはアクセスできません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "構造体の private のメンバー (フィールド・メソッド) を、その構造体の外から使っています。private のメンバーは、その構造体 (と中の構造体) の中でだけ使えます。slangc はコンパイルの誤りにします。",
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
