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
            + "main の中で、if・ループ・switch・?: の外に、return より前に書かなければなりません。"
            + "サンプラーの値の作成 (sampler2D(t, s)) は、使う場所 (組み込み関数の実引数など) に直接書かなければなりません。glslang はコンパイルの誤りにします。",
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

    /// <summary>書き込めない値に書き込んでいる。</summary>
    /// <remarks>
    /// glslang の <c>lValueErrorCheck</c> (「l-value required」「can't modify a const」「can't modify a uniform」
    /// 「can't modify shader input」「can't modify a readonly buffer」など) に当たる。文の <c>{0}</c> は書き込む先、<c>{1}</c> は理由。
    /// </remarks>
    public static DiagnosticDescriptor NotWritable { get; } = new(
        id: "GL0309",
        title: "書き込めない値です",
        messageFormat: "{0} には書き込めません ({1})。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "const の変数、uniform、ステージの入力、readonly の buffer、変数でない値 (演算の結果など) に、代入・++ / --・out の実引数で書き込んでいます。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0309"));

    /// <summary>writeonly の値を読んでいる。</summary>
    /// <remarks>glslang の <c>rValueErrorCheck</c> (「can't read from writeonly object」) に当たる。文の <c>{0}</c> は変数。</remarks>
    public static DiagnosticDescriptor WriteOnlyRead { get; } = new(
        id: "GL0310",
        title: "writeonly の値を読んでいます",
        messageFormat: "writeonly の '{0}' は読めません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "writeonly を付けた buffer のブロック (かそのメンバー) の値を、演算・代入の右辺・初期化子などで読んでいます。glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0310"));

    /// <summary>条件が bool のスカラーでない。</summary>
    /// <remarks>glslang の <c>boolCheck</c> (「boolean expression expected」) に当たる。文の <c>{0}</c> は条件の型。</remarks>
    public static DiagnosticDescriptor BooleanExpected { get; } = new(
        id: "GL0311",
        title: "条件が bool ではありません",
        messageFormat: "条件は bool のスカラーでなければなりません (今は {0})。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "if・while・do・for・?: の条件に、bool のスカラーでない値 (int、bvec2 など) を書いています。"
            + "GLSL は暗黙に bool へ変換しません。glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0311"));
    /// <summary>変数を関数として呼んでいる。</summary>
    public static DiagnosticDescriptor VariableCalled { get; } = new(
        id: "GL0312",
        title: "変数を関数として呼んでいます",
        messageFormat: "'{0}' はここでは変数です。同じ名前の関数は、この変数に隠れて呼べません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "関数の中の変数か仮引数が、同じ名前の関数 (組み込み関数を含む) を隠しています。その範囲では関数を呼べません。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0312"));

    /// <summary>サンプラーかイメージの実引数の修飾子が、仮引数と合わない。</summary>
    public static DiagnosticDescriptor ArgumentQualifierMismatch { get; } = new(
        id: "GL0313",
        title: "実引数の修飾子が仮引数と合いません",
        messageFormat: "'{0}' を渡せません。{1}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "メモリの修飾子 (coherent・readonly・writeonly など) を持つイメージを、その修飾子の無い仮引数に渡しています。"
            + "または、イメージの形式 (layout(rgba8) など) が仮引数と違います。glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0313"));

    /// <summary>スコープとセマンティクスを取るアトミックとバリアの関数で、セマンティクスの組み合わせが正しくない。</summary>
    public static DiagnosticDescriptor InvalidMemorySemantics { get; } = new(
        id: "GL0314",
        title: "メモリのセマンティクスの組み合わせが正しくありません",
        messageFormat: "{0}() のセマンティクスが正しくありません。{1}。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "GL_KHR_memory_scope_semantics のアトミックとバリアの関数に渡したセマンティクス (gl_SemanticsAcquire など) と記憶域のセマンティクス (gl_StorageSemanticsBuffer など) の組み合わせが、決まりに合っていません。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0314"));

    /// <summary>整数のリテラルが 32 ビットに収まらないか、8 進数に 8 と 9 を書いている。</summary>
    public static DiagnosticDescriptor LiteralOutOfRange { get; } = new(
        id: "GL0315",
        title: "整数のリテラルが正しくありません",
        messageFormat: "'{0}': {1}。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "接尾辞の無い整数と u の付いた整数のリテラルは、32 ビット (0xFFFFFFFF) に収まらなければなりません。0 で始まるリテラルは 8 進数なので、8 と 9 は書けません。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0315"));

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

    /// <summary>配列の大きさを書かなければならない所で、書いていない。</summary>
    /// <remarks>
    /// glslang の <c>arraySizeRequiredCheck</c> と <c>arraySizesCheck</c> (「array size required」
    /// 「only outermost dimension of an array of arrays can be implicitly sized」) に当たる。文の <c>{0}</c> は場所。
    /// </remarks>
    public static DiagnosticDescriptor ArraySizeRequired { get; } = new(
        id: "GL0402",
        title: "配列の大きさが要ります",
        messageFormat: "{0}の配列には、大きさを書かなければなりません。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "関数の戻り値・仮引数・構造体のメンバーの配列、配列の配列の内側の次元、ES の変数とブロックの配列 (初期化子と決まった例外を除く) は、"
            + "大きさを書かなければなりません。glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0402"));

    /// <summary>その宣言には使えない型。</summary>
    /// <remarks>
    /// glslang の <c>samplerCheck</c> (「sampler/image types can only be used in uniform variables or function parameters」) と
    /// <c>voidErrorCheck</c> (「illegal use of type 'void'」) に当たる。文の <c>{0}</c> は名前、<c>{1}</c> は理由。
    /// </remarks>
    public static DiagnosticDescriptor TypeNotAllowedHere { get; } = new(
        id: "GL0403",
        title: "その宣言には使えない型です",
        messageFormat: "'{0}' の型は使えません。{1}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "サンプラーとイメージの型 (とそれを含む構造体) は uniform の変数と仮引数にしか使えません。void の変数と、名前のある void の仮引数は宣言できません。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0403"));

    /// <summary>テッセレーションのシェーダーの入力の配列の大きさが、gl_MaxPatchVertices でない。</summary>
    /// <remarks>glslang の「tessellation input array size must be gl_MaxPatchVertices or implicitly sized」に当たる。文の <c>{0}</c> は大きさ。</remarks>
    public static DiagnosticDescriptor PatchInputSize { get; } = new(
        id: "GL0404",
        title: "テッセレーションの入力の配列の大きさが違います",
        messageFormat: "テッセレーションのシェーダーの入力の配列の大きさは、gl_MaxPatchVertices ({1}) にするか省いてください (今は {0})。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "テッセレーションの制御・評価のシェーダーの、patch でない入力の配列 (とブロックの配列) の大きさは、gl_MaxPatchVertices か、省いたものでなければなりません。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0404"));
    /// <summary>ES で組み込み関数を宣言し直している。</summary>
    public static DiagnosticDescriptor BuiltinRedeclared { get; } = new(
        id: "GL0405",
        title: "組み込み関数を宣言し直せません",
        messageFormat: "'{0}' は組み込み関数の名前です。{1}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "ES では、組み込み関数と同じ形の関数を宣言できません。ES の 3.00 以降では、組み込み関数と同じ名前の関数と、関数の外の変数も宣言できません。"
            + "glslang はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("GL0405"));


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
