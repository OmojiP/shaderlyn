using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Rules;

/// <summary>
/// レンダーパイプライン固有のルールの定義。
/// </summary>
/// <remarks>
/// <para>
/// ID の接頭辞は <c>URP</c> だが、規約自体は SRP (URP と HDRP) に共通のものが多い。
/// どのパイプラインで有効にするかは <c>IRenderPipelineProfile</c> が決める。
/// </para>
/// <para>
/// <b>これらのルールは、プロファイルの指定だけでは有効にならない。</b>
/// 対象のシェーダー自身が SRP のヘッダを取り込んでいることを確認してから適用する。
/// Built-in 時代のシェーダーが混在しているプロジェクトで、
/// それらに SRP の規約を当てはめるのは誤りだからである。
/// </para>
/// </remarks>
internal static class RenderPipelineRuleDescriptors
{
    private const string CorrectnessCategory = "Correctness";
    private const string PerformanceCategory = "Performance";
    private const string UsageCategory = "Usage";

    /// <summary>プロパティがマテリアル定数バッファに含まれていない。</summary>
    /// <remarks>
    /// SRP Batcher は「マテリアルごとの値がすべて 1 つの定数バッファに収まっている」ことを
    /// 前提に描画呼び出しをまとめる。1 つでも外れていると、そのシェーダーは
    /// バッチの対象から外れる。<b>エラーにはならず、ただ遅くなる。</b>
    /// </remarks>
    public static DiagnosticDescriptor SrpBatcherIncompatible { get; } = new(
        id: "URP0001",
        title: "プロパティがマテリアル定数バッファに含まれていません",
        messageFormat: "プロパティ '{0}' が '{1}' に含まれていません。SRP Batcher が無効になります。",
        category: PerformanceCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "SRP Batcher は、マテリアルごとの値がすべて所定の定数バッファへ"
            + "収められていることを前提に描画呼び出しをまとめます。"
            + "Properties に宣言された値が 1 つでもバッファの外にあると、"
            + "そのシェーダーはバッチの対象から外れます。"
            + "コンパイルは通り、描画結果も変わらないため、"
            + "プロファイラで描画呼び出し数を見るまで気づけません。",
        helpLinkUri: DocumentationLinks.For("URP0001"));

    /// <summary>マテリアル定数バッファにテクスチャやサンプラが含まれている。</summary>
    public static DiagnosticDescriptor TextureInConstantBuffer { get; } = new(
        id: "URP0002",
        title: "定数バッファにテクスチャまたはサンプラが含まれています",
        messageFormat: "'{1}' に含まれる '{0}' はテクスチャまたはサンプラです。定数バッファの外で宣言してください。",
        category: CorrectnessCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "定数バッファに入れられるのは数値だけです。"
            + "テクスチャとサンプラは別のリソースであり、定数バッファへ入れることはできません。"
            + "テクスチャの宣言は CBUFFER_START / CBUFFER_END の外に置き、"
            + "スケールとオフセット (_ST) だけをバッファへ入れてください。",
        helpLinkUri: DocumentationLinks.For("URP0002"));

    /// <summary>そのパイプラインで使うべきでない API の使用。</summary>
    /// <remarks>
    /// <b>マクロ展開前のコードに対して検査する。</b>
    /// <c>UNITY_MATRIX_MVP</c> のようにマクロ自体を対象にしたい場合、
    /// 展開後のトークン列にはその名前が残っていない。
    /// </remarks>
    public static DiagnosticDescriptor ObsoleteApi { get; } = new(
        id: "URP0003",
        title: "このレンダーパイプラインで推奨されない API を使用しています",
        messageFormat: "'{0}' は {1} では推奨されません。代わりに {2} を使ってください。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Warning,
        description: "Built-in レンダーパイプライン向けの API を SRP のシェーダーで使っています。"
            + "多くは互換のために残されているだけで、SRP Batcher やインスタンシングと"
            + "組み合わせたときに正しく動かないことがあります。"
            + "このルールは、対象のシェーダーが実際に SRP のヘッダを取り込んでいる場合にのみ適用されます。",
        helpLinkUri: DocumentationLinks.For("URP0003"));
}
