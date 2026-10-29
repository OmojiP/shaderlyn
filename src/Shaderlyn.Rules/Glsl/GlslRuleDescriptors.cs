using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// GLSL にしか無いルール (GL) の定義。
/// </summary>
/// <remarks>
/// どれも glslang 16.6.0 が誤りにするものである。glslang の検査を移して、同じ所で同じ誤りを伝える。
/// </remarks>
internal static class GlslRuleDescriptors
{
    private const string CorrectnessCategory = "Correctness";

    /// <summary>その <c>#version</c>・プロファイル・拡張・SPIR-V のバージョンでは使えない。</summary>
    /// <remarks>
    /// glslang の <c>requireExtensions</c> (「required extension not requested」)、<c>profileRequires</c> (「not supported for this version or the enabled extensions」)、
    /// <c>requireProfile</c> (「not supported with this profile」)、<c>requireSpv</c> (「requires SPIR-V 1.3」) に当たる。
    /// 文の <c>{0}</c> は、何が使えないかと、どうすれば使えるか。
    /// </remarks>
    public static DiagnosticDescriptor NotAvailable { get; } = new(
        id: "GL0001",
        title: "このバージョンと拡張では使えません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "#version で決まる言語のバージョンとプロファイル、#extension で有効にした拡張、作る SPIR-V のバージョンでは使えない機能を使っています。"
            + "glslang はコンパイルの誤りにします。拡張を有効にするか、バージョンを上げてください。",
        helpLinkUri: DocumentationLinks.For("GL0001"));

    /// <summary>そのステージでは使えない。</summary>
    /// <remarks>glslang の <c>requireStage</c> (「not supported in this stage」) に当たる。</remarks>
    public static DiagnosticDescriptor NotAvailableInStage { get; } = new(
        id: "GL0002",
        title: "このステージでは使えません",
        messageFormat: "{0} は{1}では使えません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "ファイルの拡張子で決まるステージ (.vert は頂点シェーダー、.frag はフラグメントシェーダーなど) では使えない機能を使っています。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0002"));

    /// <summary>修飾子をここに書けない。</summary>
    /// <remarks>glslang の <c>globalCheck</c> (「not allowed in nested scope」) などに当たる。文の <c>{0}</c> は、何をどこに書けないか。</remarks>
    public static DiagnosticDescriptor QualifierNotAllowed { get; } = new(
        id: "GL0101",
        title: "この修飾子はここに書けません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "修飾子 (in / out / uniform / flat など) を、書けない場所に書いています。"
            + "たとえば記憶域の修飾子は関数の外の宣言にだけ付けられます。glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0101"));

    /// <summary><c>layout(…)</c> に、無い名前を書いた。</summary>
    /// <remarks>
    /// glslang の <c>setLayoutQualifier</c> の「unrecognized layout identifier」と「there is no such layout identifier for this stage taking an assigned value」に当たる。
    /// 文の <c>{0}</c> は、何がなぜ使えないか。
    /// </remarks>
    public static DiagnosticDescriptor UnknownLayoutIdentifier { get; } = new(
        id: "GL0102",
        title: "layout にこの名前はありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "layout(…) に、GLSL に無い名前か、このステージでは使えない名前を書いています。値を取る名前 (location など) に値を書いていないときも当たります。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0102"));

    /// <summary><c>layout(…)</c> の値が正しくない。</summary>
    /// <remarks>
    /// glslang の <c>setLayoutQualifier</c> の値の検査 (「cannot be negative」「must be a power of 2」「needs a literal integer」「location is too large」など) に当たる。
    /// 文の <c>{0}</c> は、どの値がなぜ正しくないか。
    /// </remarks>
    public static DiagnosticDescriptor InvalidLayoutValue { get; } = new(
        id: "GL0103",
        title: "layout の値が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "layout(…) の値 (location = 2 の 2 など) が、負の数、大きすぎる数、2 のべき乗でない数、定数でない式などになっています。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0103"));

    /// <summary><c>layout(…)</c> に要る指定が無い。</summary>
    /// <remarks>
    /// glslang の「layout(binding=X) is required」「requires an input_attachment_index layout qualifier」
    /// 「SPIR-V requires location for user input/output」「format requires readonly or writeonly memory qualifier」などに当たる。
    /// 文の <c>{0}</c> は、何に何が要るか。
    /// </remarks>
    public static DiagnosticDescriptor LayoutRequired { get; } = new(
        id: "GL0104",
        title: "layout に要る指定がありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "宣言に要る layout の指定 (SPIR-V を作るときのブロックの binding、入力と出力の location など) を書いていません。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0104"));

    /// <summary>演算子をその型の組に使えない。</summary>
    /// <remarks>glslang の「wrong operand types: no operation '+' exists that takes …」と「wrong operand type」に当たる。文の <c>{0}</c> は、どの演算子をどの型に使ったか。</remarks>
    public static DiagnosticDescriptor WrongOperandTypes { get; } = new(
        id: "GL0301",
        title: "この型には演算子を使えません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "演算子を、使えない型の組 (vec3 + vec4、float + bool、mat4 * vec3、ベクトルの < など) に使っています。"
            + "GLSL には、成分の数を変える暗黙の変換も、bool から数への暗黙の変換もありません。glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0301"));

    /// <summary>組み込み関数に、実引数の型に合う形が無い。</summary>
    /// <remarks>glslang の「no matching overloaded function found」のうち、組み込み関数のものに当たる。</remarks>
    public static DiagnosticDescriptor NoMatchingBuiltin { get; } = new(
        id: "GL0302",
        title: "組み込み関数に合う形がありません",
        messageFormat: "組み込み関数 '{0}' に、実引数の型 ({1}) に合う形がありません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "組み込み関数を、どの多重定義にも合わない型の実引数で呼んでいます (sampler2D の texture に vec3 を渡すなど)。"
            + "暗黙の変換で合うかは #version と有効にした拡張で決まります。glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0302"));

    /// <summary>ES で、精度を決めていない型を使っている。</summary>
    /// <remarks>glslang の <c>precisionQualifierCheck</c> (「type requires declaration of default precision qualifier」) に当たる。</remarks>
    public static DiagnosticDescriptor PrecisionRequired { get; } = new(
        id: "GL0201",
        title: "精度を決めていない型です",
        messageFormat: "'{0}' の精度を決めていません。宣言に highp / mediump / lowp を付けるか、その前で precision mediump {0}; と既定の精度を決めてください。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "GLSL ES では、既定の精度の無い型 (フラグメントシェーダーの float、ほとんどのサンプラーとイメージ) の宣言に、精度の修飾子が要ります。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0201"));
}
