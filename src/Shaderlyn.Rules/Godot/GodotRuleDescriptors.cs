using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語にしか無いルール (GD) の定義。
/// </summary>
/// <remarks>
/// どれも Godot 4.7.2 のシェーダーのコンパイラ (<c>servers/rendering/shader_language.cpp</c>) が誤りにするもので、Godot に読ませて確かめた。
/// 番号は、同じ種類の GLSL のルール (GL) にそろえる (演算子は GL0301 と GD0301)。
/// </remarks>
internal static class GodotRuleDescriptors
{
    private const string CorrectnessCategory = "Correctness";

    /// <summary>そのステージ (シェーダーの種類・ステージ関数) では使えない。</summary>
    public static DiagnosticDescriptor NotAvailableInStage { get; } = new(
        id: "GD0002",
        title: "そのステージでは使えません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "フラグメントだけの組み込み関数 (dFdx など) とステージ関数の中でだけ使える組み込み関数 (texture_sdf など) を、"
            + "vertex 関数の中か vertex 関数から呼ぶ関数の中で使っています。dFdx などは spatial・canvas_item・sky のシェーダーでだけ使えます。"
            + "Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0002"));

    /// <summary>ステージ関数の形が正しくない。</summary>
    public static DiagnosticDescriptor StageFunctionSignature { get; } = new(
        id: "GD0322",
        title: "ステージ関数の形が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "ステージ関数 (vertex・fragment・light・start・process・sky・fog など) に仮引数を書いたか、戻り値を void 以外にしています。"
            + "Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0322"));

    /// <summary>演算子を使えない型の組に使った。</summary>
    public static DiagnosticDescriptor WrongOperandTypes { get; } = new(
        id: "GD0301",
        title: "この型には演算子を使えません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "演算子を、使えない型の組 (vec3 + vec4、float + int、-mat4、ベクトルの < など) に使っています。"
            + "Godot シェーダー言語には暗黙の変換がありません。Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0301"));

    /// <summary>組み込み関数と値の作成に、実引数の型に合う形が無い。</summary>
    public static DiagnosticDescriptor NoMatchingBuiltin { get; } = new(
        id: "GD0302",
        title: "組み込み関数に、実引数の型に合う形がありません",
        messageFormat: "組み込み関数 '{0}' に、実引数の型 ({1}) に合う形がありません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "組み込み関数か値の作成 (vec2(…) など) に、どの形にも合わない型の実引数を渡しています (sin(vec2, float)、vec2(int, float) など)。"
            + "Godot シェーダー言語には暗黙の変換がありません。Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0302"));

    /// <summary>条件が bool ではない。</summary>
    public static DiagnosticDescriptor BooleanExpected { get; } = new(
        id: "GD0311",
        title: "条件が bool ではありません",
        messageFormat: "条件は bool でなければなりません (今は {0})。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "if・while・do・for・?: の条件に、bool でない値 (int、bvec2 など) を書いています。"
            + "Godot シェーダー言語は暗黙に bool へ変換しません。Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0311"));
}
