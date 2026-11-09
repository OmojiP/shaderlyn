using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>
/// WGSL にしか無いルール (WG) の定義。
/// </summary>
/// <remarks>
/// どれも naga 30 (wgpu の WGSL のコンパイラ) が誤りにするもので、naga に読ませて確かめた。
/// 番号は、同じ種類の GLSL のルール (GL) にそろえる (拡張は GL0001 と WG0001)。
/// </remarks>
internal static class WgslRuleDescriptors
{
    private const string CorrectnessCategory = "Correctness";

    /// <summary>enable していない拡張の機能を使った。</summary>
    public static DiagnosticDescriptor ExtensionNotEnabled { get; } = new(
        id: "WG0001",
        title: "この機能を使うには enable が必要です",
        messageFormat: "{0} を使うには、ファイルの先頭に 'enable {1};' が必要です。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "f16・i16 / u16 の型、binding_array、光線の照会と光線追跡、メッシュシェーダー、2 つの出力の合成 (@blend_src)、"
            + "@builtin(clip_distances) などは、enable で拡張を有効にしたファイルでだけ使えます。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0001"));

    /// <summary>書き込めない値に書き込んだ。</summary>
    public static DiagnosticDescriptor NotWritable { get; } = new(
        id: "WG0309",
        title: "書き込めない値です",
        messageFormat: "{0} には書き込めません ({1})。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "let・const・override の値、関数の仮引数、var<uniform> と読み取りだけの var<storage> の変数、2 つ以上の成分のスウィズル、"
            + "変数でない値 (演算の結果など) に、代入か ++ / -- で書き込んでいます。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0309"));

    /// <summary>そのステージでは使えない。</summary>
    public static DiagnosticDescriptor NotAvailableInStage { get; } = new(
        id: "WG0002",
        title: "このステージでは使えません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "フラグメントシェーダーだけの操作 (discard・dpdx などの微分・textureSample などの暗黙の微分) を頂点か計算のエントリポイントから、"
            + "計算シェーダーだけの操作 (workgroupBarrier などの同期・workgroupUniformLoad) を頂点かフラグメントのエントリポイントから使っています。"
            + "エントリポイントから呼ぶ関数の中も見ます。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0002"));

    /// <summary>エントリポイントの形が正しくない。</summary>
    public static DiagnosticDescriptor InvalidEntryPoint { get; } = new(
        id: "WG0322",
        title: "エントリポイントの形が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "エントリポイントの仮引数・戻り値 (構造体ならそのメンバー) に @location か @builtin が無い、頂点シェーダーが @builtin(position) を返さない、"
            + "@blend_src の使い方が正しくない (構造体のメンバーでない、location 0 の 2 つの出力 0 と 1 でない、型が違う、ほかに @location のメンバーがある、頂点シェーダーで使う)、"
            + "または @builtin(clip_distances) の型が array<f32, N> (N は 8 以下) でありません。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0322"));

    /// <summary>定数式でなければならない実引数に、実行時の値を渡した。</summary>
    public static DiagnosticDescriptor ConstantArgumentRequired { get; } = new(
        id: "WG0303",
        title: "この実引数は定数式でなければなりません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "subgroupBroadcast と quadBroadcast の 2 つ目の実引数 (値を受け取る呼び出しの番号) に、let・var・仮引数などの実行時の値を渡しています。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0303"));

    /// <summary>添字が範囲の外か、整数でない。</summary>
    public static DiagnosticDescriptor IndexOutOfRange { get; } = new(
        id: "WG0307",
        title: "添字が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "大きさの決まった配列・ベクトル・行列に、範囲の外の定数の添字 (負の数か、大きさ以上の数) を書いています。または、添字が整数でありません。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0307"));

    /// <summary>配列の大きさが正しくない。</summary>
    public static DiagnosticDescriptor InvalidArraySize { get; } = new(
        id: "WG0308",
        title: "配列の大きさが正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "array<T, N> の N が、0 以下か、整数 (i32 か u32) でない定数式です。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0308"));

    /// <summary>条件が bool でない。</summary>
    public static DiagnosticDescriptor ConditionNotBool { get; } = new(
        id: "WG0311",
        title: "条件が bool ではありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "if・while・for・break if・const_assert の条件か、select の 3 つ目の実引数が bool (select では vecN<bool> も) ではありません。WGSL は数を真偽値に変換しません。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0311"));

    /// <summary>数が型に収まらない。</summary>
    public static DiagnosticDescriptor ValueOutOfRange { get; } = new(
        id: "WG0315",
        title: "数が型に収まりません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "数のリテラルがその型 (i32・u32 など) の範囲を超えているか、型を書かない整数を、範囲に収まらない型 (u32 に -1 など) に変換しています。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0315"));

    /// <summary>定数式が要るところに、実行時の値を書いた。</summary>
    public static DiagnosticDescriptor ConstantExpressionRequired { get; } = new(
        id: "WG0317",
        title: "定数式が要ります",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "const の初期値か const_assert の条件に、let・var・仮引数・override の値を使っています。const の値はシェーダーを作る前に決まらなければなりません。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0317"));

    /// <summary>関数が自分自身を呼ぶ。</summary>
    public static DiagnosticDescriptor RecursiveCall { get; } = new(
        id: "WG0332",
        title: "関数は自分自身を呼べません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "関数が、直接か別の関数を通して自分自身を呼んでいます。WGSL では再帰の呼び出しができません。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0332"));

    /// <summary>シフトの量か、定数の演算の結果が型の範囲を超えた。</summary>
    public static DiagnosticDescriptor ArithmeticOverflow { get; } = new(
        id: "WG0336",
        title: "シフトの量か定数の演算の結果が範囲を超えています",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "シフトの量 (<< と >> の右) が左の型のビットの幅以上の定数です。または、定数式の演算 (+・-・*・<< など) の結果が型の範囲を超えています。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0336"));

    /// <summary>const_assert の条件が偽。</summary>
    public static DiagnosticDescriptor ConstAssertFailed { get; } = new(
        id: "WG0337",
        title: "const_assert の条件が成り立ちません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "const_assert の条件の定数式が false になります。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0337"));

    /// <summary>型引数が正しくない。</summary>
    public static DiagnosticDescriptor InvalidTypeArgument { get; } = new(
        id: "WG0324",
        title: "型の引数が正しくありません",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "ベクトルの要素がスカラーでない、行列の要素が浮動小数点数でない、テクスチャの要素の型が f32・i32・u32 でない、"
            + "アトミックの型が i32・u32 でない、ストレージのテクスチャの形式かアクセスが無い名前、アドレス空間かアクセスが無い名前、"
            + "または要素の型が決まらない値の作成 (mat2x2()) です。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0324"));

    /// <summary>その宣言には使えない型。</summary>
    public static DiagnosticDescriptor InvalidTypeForDeclaration { get; } = new(
        id: "WG0403",
        title: "その宣言には使えない型です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "構造体のメンバー・配列の要素・局所変数・関数の仮引数と戻り値・関数の外の変数・値の作成に、その場所では使えない型を使っています "
            + "(構造体のメンバーのテクスチャ、最後でないメンバーの大きさを決めない配列、局所変数のアトミック、storage のポインターの仮引数、"
            + "アトミックを含む戻り値、アドレス空間を書かない数の変数、var<immediate> の f16、大きさを決めない配列の値の作成など)。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0403"));

    /// <summary>アドレスを取れない値の <c>&amp;</c> を取った。</summary>
    public static DiagnosticDescriptor NotAddressable { get; } = new(
        id: "WG0330",
        title: "アドレスを取れない値です",
        messageFormat: "{0}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Error,
        description: "ベクトルの成分 (v.x、v[0]、m[0][0]) のアドレスを & で取っています。WGSL ではベクトルの成分を指すポインターを作れません。naga はコンパイルの誤りにします。",
        helpLinkUri: DocumentationLinks.For("WG0330"));
}
