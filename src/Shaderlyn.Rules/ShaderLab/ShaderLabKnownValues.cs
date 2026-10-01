using System.Collections.Frozen;

namespace Shaderlyn.Rules;

/// <summary>
/// ShaderLab の命令・タグ・属性について、既知の名前と取りうる値をまとめた表。
/// </summary>
/// <remarks>
/// <para>
/// <b>ここに無いものを「誤り」と断定してはならない。</b>
/// Unity のバージョンが上がれば命令もタグも増えるし、
/// 属性はユーザーが <c>MaterialPropertyDrawer</c> を実装して自由に追加できる。
/// この表の役割は「知っているものについて、値が明確に誤っている場合だけ指摘する」ことであり、
/// 網羅的なホワイトリストとして使うものではない。
/// </para>
/// <para>
/// 比較はすべて大文字小文字を区別しない。ShaderLab がそう振る舞うためである。
/// </para>
/// </remarks>
internal static class ShaderLabKnownValues
{
    private static FrozenSet<string> Set(params string[] values)
        => values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>ブレンド係数として指定できる値。</summary>
    public static FrozenSet<string> BlendFactors { get; } = Set(
        "One", "Zero",
        "SrcColor", "SrcAlpha", "DstColor", "DstAlpha",
        "OneMinusSrcColor", "OneMinusSrcAlpha", "OneMinusDstColor", "OneMinusDstAlpha",
        "SrcAlphaSaturate");

    /// <summary>ブレンド演算として指定できる値。</summary>
    public static FrozenSet<string> BlendOperations { get; } = Set(
        "Add", "Sub", "RevSub", "Min", "Max",
        "LogicalClear", "LogicalSet", "LogicalCopy", "LogicalCopyInverted",
        "LogicalNoop", "LogicalInvert", "LogicalAnd", "LogicalNand",
        "LogicalOr", "LogicalNor", "LogicalXor", "LogicalEquiv",
        "LogicalAndReverse", "LogicalAndInverted", "LogicalOrReverse", "LogicalOrInverted",
        "Multiply", "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn",
        "HardLight", "SoftLight", "Difference", "Exclusion", "HSLHue",
        "HSLSaturation", "HSLColor", "HSLLuminosity");

    /// <summary>
    /// ステンシルの比較関数として指定できる値。
    /// </summary>
    public static FrozenSet<string> CompareFunctions { get; } = Set(
        "Less", "Greater", "LEqual", "GEqual", "Equal", "NotEqual", "Always", "Never");

    /// <summary>
    /// 深度テストの比較関数として指定できる値。
    /// </summary>
    /// <remarks>
    /// <b><c>ZTest Off</c> は妥当である。</b>
    /// 深度テストを無効化する指定で、<c>ZTest Always</c> と同じ意味になる。
    /// Unity 同梱のシェーダーで実際に多用されており、
    /// これを不正としていた時期には公式シェーダーに 20 件の誤検出が出ていた。
    /// </remarks>
    public static FrozenSet<string> DepthTestFunctions { get; } = Set(
        "Less", "Greater", "LEqual", "GEqual", "Equal", "NotEqual", "Always", "Never", "Off");

    /// <summary>ステンシルの操作として指定できる値。</summary>
    public static FrozenSet<string> StencilOperations { get; } = Set(
        "Keep", "Zero", "Replace", "IncrSat", "DecrSat", "Invert", "IncrWrap", "DecrWrap");

    /// <summary>
    /// オンオフを表す値。
    /// </summary>
    /// <remarks>
    /// <b><c>True</c> / <c>False</c> も受け付ける。</b>
    /// Unity 同梱のシェーダーが <c>ZClip false</c> や <c>Conservative True</c> と書いており、
    /// それらが実際に動作している以上、この記法は妥当である。
    /// On/Off に限定していた時期には公式シェーダーに誤検出が出ていた。
    /// </remarks>
    public static FrozenSet<string> OnOff { get; } = Set("On", "Off", "True", "False");

    /// <summary>面のカリング方法として指定できる値。</summary>
    public static FrozenSet<string> CullModes { get; } = Set("Back", "Front", "Off");

    /// <summary>
    /// <c>Queue</c> タグに指定できる基準名。
    /// </summary>
    /// <remarks>
    /// 実際の値は <c>Transparent+100</c> のように数値のオフセットを伴えるため、
    /// 値の検証ではオフセット部分を切り離してからこの集合と照合する。
    /// </remarks>
    public static FrozenSet<string> RenderQueues { get; } = Set(
        "Background", "Geometry", "AlphaTest", "Transparent", "Overlay");

    /// <summary>
    /// 半透明として扱われる描画キュー。
    /// </summary>
    /// <remarks>
    /// 深度書き込みとの矛盾検査 (SL1020) で、対象となるキューを判定するために使う。
    /// </remarks>
    public static FrozenSet<string> TransparentQueues { get; } = Set("Transparent", "Overlay");

    /// <summary>
    /// <c>RenderType</c> タグに使われる既知の値。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>これは網羅的な一覧ではない。</b>
    /// <c>RenderType</c> は <c>Camera.RenderWithShader</c> によるシェーダー置き換えの目印であり、
    /// プロジェクトは自由な値を付けてよい。
    /// この一覧は「スペルミスに見えるか」を測る基準としてのみ使う (SL1012)。
    /// </para>
    /// <para>
    /// 前半は Unity のシェーダー置き換えの説明にある組み込みの値、
    /// 後半は HDRP が実際に使っている値である。
    /// <b>HDRP の 2 つは揃えて入れなければならない。</b>
    /// 片方だけだと、もう片方は編集距離 2 のスペルミスとして報告されてしまう。
    /// </para>
    /// </remarks>
    public static FrozenSet<string> RenderTypes { get; } = Set(
        "Opaque", "Transparent", "TransparentCutout", "Background", "Overlay",
        "TreeOpaque", "TreeTransparentCutout", "TreeBillboard", "Grass", "GrassBillboard",
        "HDLitShader", "HDUnlitShader");

    /// <summary>真偽値を取るタグ。</summary>
    public static FrozenSet<string> BooleanTagKeys { get; } = Set(
        "IgnoreProjector", "ForceNoShadowCasting", "CanUseSpriteAtlas", "ShadowSupport");

    /// <summary>
    /// 既知のタグ名。
    /// </summary>
    /// <remarks>
    /// レンダーパイプラインが独自のタグを追加するため、ここに無いタグが不正とは限らない。
    /// 未知のタグは情報レベルでの報告にとどめる。
    /// </remarks>
    public static FrozenSet<string> TagKeys { get; } = Set(
        // SubShader / Pass の一般的なタグ
        "RenderType", "Queue", "RenderPipeline", "IgnoreProjector", "ForceNoShadowCasting",
        "CanUseSpriteAtlas", "PreviewType", "DisableBatching", "LightMode", "PassFlags",
        "RequireOptions", "ShadowSupport", "SpritePreviewType", "AlwaysIncludeShaders",
        "UniversalMaterialType", "ShaderGraphShader", "ShaderGraphTargetId", "RenderQueue",

        // 地形シェーダーが使うタグ
        "TerrainCompatible", "SplatCount", "Format", "Size", "Name",
        "MaskMapR", "MaskMapG", "MaskMapB", "MaskMapA",
        "DiffuseA", "DiffuseA_MaskMapUsed", "EmptyColor",

        // エディタや検証まわりのタグ
        "ShaderModel", "PerformanceChecks", "ForceSupported");

    /// <summary><c>DisableBatching</c> タグに指定できる値。</summary>
    public static FrozenSet<string> DisableBatchingValues { get; } = Set("True", "False", "LODFading");

    /// <summary><c>PreviewType</c> タグに指定できる値。</summary>
    public static FrozenSet<string> PreviewTypeValues { get; } = Set("Sphere", "Plane", "Skybox");

    /// <summary>
    /// 命令名から、その引数に指定できる値の集合への対応表。
    /// </summary>
    /// <remarks>
    /// ここに無い命令は検証しない。値が数値やプロパティ参照の場合も検証しない
    /// (プロパティ参照の実際の値は静的には分からないため)。
    /// </remarks>
    public static FrozenDictionary<string, FrozenSet<string>> CommandValues { get; } =
        new Dictionary<string, FrozenSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cull"] = CullModes,
            ["ZWrite"] = OnOff,
            ["ZTest"] = DepthTestFunctions,
            ["ZClip"] = OnOff,
            ["AlphaToMask"] = OnOff,
            ["Conservative"] = OnOff,
            ["Lighting"] = OnOff,
            ["Fog"] = OnOff,
            ["BlendOp"] = BlendOperations,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ステンシルブロック内の命令名から、その引数に指定できる値の集合への対応表。
    /// </summary>
    /// <remarks>
    /// <c>Ref</c> / <c>ReadMask</c> / <c>WriteMask</c> は数値を取るためここには含めない。
    /// </remarks>
    public static FrozenDictionary<string, FrozenSet<string>> StencilCommandValues { get; } =
        new Dictionary<string, FrozenSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Comp"] = CompareFunctions,
            ["CompBack"] = CompareFunctions,
            ["CompFront"] = CompareFunctions,
            ["Pass"] = StencilOperations,
            ["PassBack"] = StencilOperations,
            ["PassFront"] = StencilOperations,
            ["Fail"] = StencilOperations,
            ["FailBack"] = StencilOperations,
            ["FailFront"] = StencilOperations,
            ["ZFail"] = StencilOperations,
            ["ZFailBack"] = StencilOperations,
            ["ZFailFront"] = StencilOperations,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 属性名から、許容される引数の個数への対応表。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ここに無い属性名を誤りとして報告してはならない。</b>
    /// 属性は <c>MaterialPropertyDrawer</c> を継承したクラスを書くことで
    /// プロジェクトごとに自由に追加できる。未知の名前を誤りとすると、
    /// 独自ドロワーを使っているだけのプロジェクトで大量の誤検出が出る。
    /// </para>
    /// <para><c>Enum</c> は引数の形が 2 通りあるため、この表では扱わず個別に検証する。</para>
    /// </remarks>
    public static FrozenDictionary<string, (int Min, int Max)> AttributeArgumentCounts { get; } =
        new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["MainTexture"] = (0, 0),
            ["MainColor"] = (0, 0),
            ["Normal"] = (0, 0),
            ["HDR"] = (0, 0),
            ["Gamma"] = (0, 0),
            ["HideInInspector"] = (0, 0),
            ["NoScaleOffset"] = (0, 0),
            ["PerRendererData"] = (0, 0),
            ["Toggle"] = (0, 1),
            ["ToggleOff"] = (0, 1),
            ["KeywordEnum"] = (1, 9),
            ["PowerSlider"] = (1, 1),
            ["IntRange"] = (0, 0),
            ["Space"] = (0, 1),
            ["Header"] = (1, 1),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}
