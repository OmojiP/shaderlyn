using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Rules;

/// <summary>
/// ShaderLab 単体で検査できるルールの定義。
/// </summary>
/// <remarks>
/// HLSL 側の情報を必要とするルール (プロパティと uniform の対応など) はここには含まれない。
/// それらは意味解析層が入る M3 以降で追加する。
/// </remarks>
internal static class ShaderLabRuleDescriptors
{
    private const string CorrectnessCategory = "Correctness";
    private const string NamingCategory = "Naming";
    private const string UsageCategory = "Usage";

    /// <summary>タグ名のスペルミス。</summary>
    /// <remarks>
    /// <b>未知のタグ名それ自体は報告しない。</b>
    /// タグの名前空間は開かれており、レンダーパイプラインや地形システムなどが
    /// 独自のタグを自由に追加するためである。
    /// 既知のタグに十分近い名前だけを、スペルミスの疑いとして報告する。
    /// </remarks>
    public static DiagnosticDescriptor UnknownTag { get; } = new(
        id: "SL1010",
        title: "タグ名にスペルミスの可能性があります",
        messageFormat: "タグ '{0}' は既知のタグ名ではありません。'{1}' の誤字の可能性があります。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "既知のタグ名によく似た、しかし一致しないタグ名が指定されています。"
            + "Unity はタグ名のスペルミスをエラーにしないため、"
            + "「設定したつもりが効いていない」という形でしか現れません。"
            + "独自に定義したタグであれば、この指摘は無視して構いません。",
        helpLinkUri: DocumentationLinks.For("SL1010"));

    /// <summary>Pass 名の重複。</summary>
    public static DiagnosticDescriptor DuplicatePassName { get; } = new(
        id: "SL1041",
        title: "Pass 名が重複しています",
        messageFormat: "Pass 名 '{0}' が同じ SubShader 内で重複しています。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "同じ SubShader 内に同名の Pass があります。"
            + "Unity は Pass 名を大文字に正規化して扱うため、大文字小文字だけが異なる名前も重複とみなされます。"
            + "UsePass で参照した場合にどちらが使われるかは保証されません。",
        helpLinkUri: DocumentationLinks.For("SL1041"));

    /// <summary>
    /// タグの値にスペルミスの可能性がある。
    /// </summary>
    /// <remarks>
    /// <b>未知の値それ自体は報告しない。</b>
    /// <c>RenderType</c> のようにプロジェクトが自由な値を付けられるタグがあり、
    /// 一覧に無いことを根拠にすると正しいシェーダーを誤りとして指摘することになる。
    /// 既知の値に十分近いものだけを、スペルミスの疑いとして報告する。
    /// 判定の考え方は <see cref="UnknownTag"/> と同じである。
    /// </remarks>
    public static DiagnosticDescriptor MisspelledTagValue { get; } = new(
        id: "SL1012",
        title: "タグの値にスペルミスの可能性があります",
        messageFormat: "タグ '{0}' の値 '{1}' は既知の値ではありません。'{2}' の誤字の可能性があります。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "既知の値によく似た、しかし一致しない値がタグに指定されています。"
            + "Unity はタグ値のスペルミスをエラーにしないため、"
            + "「設定したつもりが効いていない」という形でしか現れません。"
            + "意図してその値を使っている場合、この指摘は無視して構いません。",
        helpLinkUri: DocumentationLinks.For("SL1012"));

    /// <summary>タグの値が不正。</summary>
    public static DiagnosticDescriptor InvalidTagValue { get; } = new(
        id: "SL1011",
        title: "タグの値が不正です",
        messageFormat: "タグ '{0}' の値 '{1}' は不正です。{2}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "既知のタグに、そのタグが受け付けない値が指定されています。"
            + "Unity は不正なタグ値をエラーにせず既定値で動作させるため、"
            + "意図した設定になっていないことに気づきにくい不具合になります。",
        helpLinkUri: DocumentationLinks.For("SL1011"));

    /// <summary>半透明描画と深度書き込みの矛盾。</summary>
    public static DiagnosticDescriptor TransparentWithZWrite { get; } = new(
        id: "SL1020",
        title: "半透明描画で深度書き込みが有効になっています",
        messageFormat: "描画キューが '{0}' でブレンドが有効ですが、ZWrite が On です。ZWrite Off を指定してください。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "半透明のオブジェクトが深度バッファへ書き込むと、"
            + "後ろにある半透明オブジェクトが深度テストで捨てられ、見え方が描画順に依存して破綻します。"
            + "ZWrite の既定値は On であるため、Transparent キューのシェーダーでは明示的に Off を指定する必要があります。"
            + "意図的に深度を書き込む場合はこのルールを抑制してください。",
        helpLinkUri: DocumentationLinks.For("SL1020"));

    /// <summary>レンダーステートの値が不正。</summary>
    public static DiagnosticDescriptor InvalidRenderStateValue { get; } = new(
        id: "SL1021",
        title: "レンダーステートの値が不正です",
        messageFormat: "'{0}' に指定された値 '{1}' は不正です。指定できる値: {2}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "レンダーステート命令に、その命令が受け付けない値が指定されています。"
            + "Unity は不正な値を既定値へ倒して動作を続けるため、"
            + "意図した描画設定になっていないことに実行するまで気づけません。",
        helpLinkUri: DocumentationLinks.For("SL1021"));

    /// <summary>プロパティ名の命名規約違反。</summary>
    /// <remarks>
    /// アンダースコア始まりのみを検査し、それ以降の綴り方 (PascalCase など) は検査しない。
    /// 適切な綴り方はプロジェクトごとに異なるため、
    /// 設定ファイルで宣言できるユーザー定義ルール (M4) の領分としている。
    /// </remarks>
    public static DiagnosticDescriptor PropertyNamingConvention { get; } = new(
        id: "SL1030",
        title: "プロパティ名がアンダースコアで始まっていません",
        messageFormat: "プロパティ名 '{0}' はアンダースコアで始めてください。",
        category: NamingCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "Unity のシェーダープロパティはアンダースコア始まりにするのが慣習です。"
            + "これに従わないと、HLSL 側の組み込み変数や関数名と衝突する危険があり、"
            + "またプロジェクト内で機械的にプロパティを探す仕組みからも漏れます。",
        helpLinkUri: DocumentationLinks.For("SL1030"));

    /// <summary>プロパティ属性の引数が不正。</summary>
    /// <remarks>
    /// 未知の属性名は報告しない。属性は <c>MaterialPropertyDrawer</c> を継承することで
    /// プロジェクトごとに追加できるため、未知であること自体は誤りではない。
    /// </remarks>
    public static DiagnosticDescriptor InvalidAttributeArguments { get; } = new(
        id: "SL1031",
        title: "プロパティ属性の引数が不正です",
        messageFormat: "属性 '{0}' の引数の個数が不正です ({1} 個)。{2}",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "既知のプロパティ属性に、必要な引数が渡されていないか、余分な引数が渡されています。"
            + "Unity のインスペクタは引数が不正な属性を無視するため、"
            + "意図した表示にならない原因になります。",
        helpLinkUri: DocumentationLinks.For("SL1031"));

    /// <summary>解析の対象外のコードブロックがある。</summary>
    /// <remarks>
    /// <b>読んでいないことを伝えるためのルールである。</b>
    /// 何も報告しないと、検査して問題が無かったのと区別が付かない。
    /// </remarks>
    public static DiagnosticDescriptor UnanalyzedBlock { get; } = new(
        id: "SL0005",
        title: "解析の対象外のコードブロックです",
        messageFormat: "{0} の中は現在解析の対象外です。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "GLSLPROGRAM / GLSLINCLUDE の中のコードは解析しません。"
            + "このブロックについては、構文の誤りもプロパティとの対応も検査していません。",
        helpLinkUri: DocumentationLinks.For("SL0005"));

    /// <summary>シェーダー名の構造が不正。</summary>
    public static DiagnosticDescriptor InvalidShaderName { get; } = new(
        id: "SL1040",
        title: "シェーダー名の構造が不正です",
        messageFormat: "シェーダー名 '{0}' は不正です。{1}",
        category: NamingCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "シェーダー名は Unity のマテリアルインスペクタでスラッシュ区切りのメニュー階層になります。"
            + "空の階層や前後の空白があると、メニューに表示されないか、選択できない項目になります。",
        helpLinkUri: DocumentationLinks.For("SL1040"));
}
