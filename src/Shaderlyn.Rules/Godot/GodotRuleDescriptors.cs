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

    /// <summary>修飾子を、書けないところに書いた。</summary>
    public static DiagnosticDescriptor QualifierNotAllowed { get; } = new(
        id: "GD0101",
        title: "この修飾子はここに書けません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "instance uniform をサンプラー・行列・配列に使うか、spatial と canvas_item 以外のシェーダーで使っています。"
            + "または、global uniform を配列にするか、varying を particles・sky・fog のシェーダーで使っています。Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0101"));

    /// <summary>描画のモードかステンシルのモードが正しくない。</summary>
    public static DiagnosticDescriptor InvalidMode { get; } = new(
        id: "GD0102",
        title: "描画のモードかステンシルのモードが正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "render_mode か stencil_mode に、そのシェーダーの種類に無い名前を書いたか、同じ名前を 2 度書いたか、"
            + "同じモードの別の選択肢 (blend_mix と blend_add など) を書いています。ステンシルの参照値は 0 から 255 の 1 つだけです。"
            + "Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0102"));

    /// <summary>uniform のヒントか既定値が正しくない。</summary>
    public static DiagnosticDescriptor InvalidUniformHint { get; } = new(
        id: "GD0103",
        title: "uniform のヒントか既定値が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "uniform のヒントを、使えない型に書いたか (float の source_color、サンプラーでない uniform の filter_linear など)、"
            + "同じ種類のヒントを 2 度書いたか、そのシェーダーの種類で使えないヒント (sky の hint_screen_texture など) を書いています。"
            + "または、instance_index の範囲・instance uniform の数・既定値の型が正しくありません。Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0103"));

    /// <summary>その宣言には使えない型。</summary>
    public static DiagnosticDescriptor TypeNotAllowed { get; } = new(
        id: "GD0403",
        title: "その宣言には使えない型です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "uniform に構造体か void の型を、varying にサンプラー・構造体・void の型を、構造体のメンバーにサンプラーか void の型を使っています。"
            + "Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0403"));

    /// <summary>out / inout の仮引数に渡せない値。</summary>
    public static DiagnosticDescriptor InvalidOutArgument { get; } = new(
        id: "GD0306",
        title: "out の実引数に渡せない値です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "out / inout の仮引数に、書き込めない値 (リテラル・式・関数の呼び出しの結果・const の変数・uniform・書き込めない組み込み・ベクトルの添字) を渡しています。"
            + "Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0306"));

    /// <summary>添字が正しくない。</summary>
    public static DiagnosticDescriptor InvalidIndex { get; } = new(
        id: "GD0307",
        title: "添字が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "添字がリテラルで、配列の長さ・ベクトルの成分の数・行列の列の数の外にあるか、添字が int / uint のスカラーでないか、"
            + "配列・ベクトル・行列でない値 (float、構造体など) に添字を付けています。Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0307"));

    /// <summary>書き込めない値に書き込んだ。</summary>
    public static DiagnosticDescriptor ReadOnlyTarget { get; } = new(
        id: "GD0309",
        title: "書き込めない値です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "const の変数・uniform・その関数で書き込めない組み込みの変数 (TIME、fragment の VERTEX など)・関数の呼び出しの結果・"
            + "同じ成分を 2 度選んだスウィズルに、代入か ++ / -- で書き込んでいます。Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0309"));

    /// <summary>switch と break / continue の書き方が正しくない。</summary>
    public static DiagnosticDescriptor SwitchStatement { get; } = new(
        id: "GD0321",
        title: "switch と break / continue の書き方が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "switch の式が int / uint でないか、case の値が switch の式と同じ型の整数の定数でないか、同じ case の値や 2 つ目の default を書いています。"
            + "または、case / default を switch の外に、break をループと switch の外に、continue をループの外 (ループの中の switch の中を含む) に書いています。"
            + "Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0321"));

    /// <summary>組み込みの名前を宣言し直した。</summary>
    public static DiagnosticDescriptor BuiltinRedefinition { get; } = new(
        id: "GD0405",
        title: "組み込みの名前を宣言し直せません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "組み込みの変数か定数の名前 (TIME、PI、ALBEDO など) で、変数・仮引数・関数を宣言しているか、組み込み関数の名前 (sin など) で関数を宣言しています。"
            + "関数の外の宣言・関数・仮引数には、そのシェーダーの種類のどの関数の組み込みの名前も使えません。Godot はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GD0405"));

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
