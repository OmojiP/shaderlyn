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

    /// <summary>組み込み関数の、定数でなければならない実引数に、定数でない式を渡した。</summary>
    /// <remarks>glslang の「argument must be compile-time constant」に当たる。文の <c>{0}</c> は、どの実引数か。</remarks>
    public static DiagnosticDescriptor ConstantArgumentRequired { get; } = new(
        id: "GL0303",
        title: "この実引数は定数でなければなりません",
        messageFormat: "{0} は定数の式でなければなりません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "組み込み関数の、定数でなければならない実引数 (textureOffset のオフセット、サブグループのクラスターの大きさなど) に、"
            + "変数や仮引数を使う式を渡しています。glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0303"));

    /// <summary>組み込み関数の実引数の値が、使える範囲の外にある。</summary>
    /// <remarks>glslang の「value is out of range」「argument must be a power of 2」などに当たる。文の <c>{0}</c> は実引数、<c>{1}</c> は使える値。</remarks>
    public static DiagnosticDescriptor ArgumentOutOfRange { get; } = new(
        id: "GL0304",
        title: "実引数の値が範囲の外です",
        messageFormat: "{0} は {1} でなければなりません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "組み込み関数の実引数の値が、使える範囲の外にあります (テクセルのオフセットは -8 から 7、textureGather の成分の番号は 0 から 3 など)。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0304"));

    /// <summary>組み込み関数を、呼べない場所で呼んでいる。</summary>
    /// <remarks>
    /// glslang の <c>checkLocation</c> (「tessellation control barrier() cannot be placed within flow control」
    /// 「beginInvocationInterlockARB() must only be called once」など) に当たる。文の <c>{0}</c> は関数、<c>{1}</c> は理由。
    /// </remarks>
    public static DiagnosticDescriptor CallNotAllowedHere { get; } = new(
        id: "GL0305",
        title: "ここでは呼べない組み込み関数です",
        messageFormat: "{0}() は{1}。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "テッセレーションの制御シェーダーの barrier() と、beginInvocationInterlockARB() / endInvocationInterlockARB() は、"
            + "main の中で、if・ループ・switch・?: の外に、return より前に書かなければなりません。glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0305"));

    /// <summary>組み込み関数に、決まった種類の変数でなければならない実引数として、別のものを渡している。</summary>
    /// <remarks>
    /// glslang の「first argument must be an interpolant, or interpolant-array element」
    /// 「Only l-values corresponding to shader block storage or shared variables can be used with atomic memory functions」などに当たる。
    /// 文の <c>{0}</c> は関数、<c>{1}</c> は実引数の番号、<c>{2}</c> は要るもの。
    /// </remarks>
    public static DiagnosticDescriptor VariableArgumentRequired { get; } = new(
        id: "GL0306",
        title: "実引数に渡せない値です",
        messageFormat: "{0} の第 {1} 引数は{2}。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "interpolateAt* の第 1 引数は入力の変数、アトミックの関数の第 1 引数と協調行列の読み書きの関数の第 2 引数は "
            + "buffer のブロックか shared の変数でなければなりません。glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0306"));

    /// <summary>定数の添字が、配列・ベクトル・行列の範囲の外にある。</summary>
    /// <remarks>
    /// glslang の <c>checkIndex</c> (「index out of range」「array index out of range」「vector index out of range」「matrix index out of range」) に当たる。
    /// 文の <c>{0}</c> は添字の値、<c>{1}</c> は範囲。
    /// </remarks>
    public static DiagnosticDescriptor IndexOutOfRange { get; } = new(
        id: "GL0307",
        title: "添字が範囲の外です",
        messageFormat: "添字 {0} は範囲の外です ({1})。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "定数の添字が負の数か、配列の大きさ・ベクトルの成分の数・行列の列の数以上です。glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0307"));

    /// <summary>大きさの決まっていない配列を、定数でない添字で引くか、<c>length()</c> で長さを求めている。</summary>
    /// <remarks>
    /// glslang の「array must be redeclared with a size before being indexed with a variable」
    /// 「array must be declared with a size before using this method」に当たる。文の <c>{0}</c> は配列、<c>{1}</c> はしたこと。
    /// </remarks>
    public static DiagnosticDescriptor UnsizedArrayUse { get; } = new(
        id: "GL0308",
        title: "大きさの決まっていない配列です",
        messageFormat: "大きさの決まっていない配列 '{0}' を{1}。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "大きさを書かずに宣言した配列 (buffer のブロックの最後のメンバーを除く) は、定数でない添字で引けず、length() も使えません。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0308"));

    /// <summary>予約された名前を宣言している。</summary>
    /// <remarks>
    /// glslang の <c>reservedErrorCheck</c> (「identifiers starting with "gl_" are reserved」
    /// 「identifiers containing consecutive underscores ("__") are reserved, and an error if version &lt; 300」) に当たる。
    /// 文の <c>{0}</c> は名前、<c>{1}</c> は理由。
    /// </remarks>
    public static DiagnosticDescriptor ReservedName { get; } = new(
        id: "GL0401",
        title: "予約された名前です",
        messageFormat: "'{0}' は予約された名前です。{1}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "gl_ で始まる名前 (宣言し直せる組み込みの変数を除く) と、ES の 3.00 より前の __ を含む名前は宣言できません。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0401"));

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
