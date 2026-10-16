using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Unreal.Rules;

/// <summary>
/// Unreal Engine の環境だけのルールの定義。
/// </summary>
/// <remarks>
/// どちらも「検査していない」ことを伝えるルールである (<c>HL0005</c> と同じ考え方)。
/// 原因と直し方が Unreal に固有なので、<c>HL0005</c> から分けて別の ID にしている。
/// 分けておくと、直せないもの (<see cref="MaterialGeneratedFilesUnknown"/>) だけを設定で止められる。
/// </remarks>
internal static class UnrealRuleDescriptors
{
    private const string UsageCategory = "Usage";

    /// <summary>C++ から作れる生成ファイルが無いので、宣言の検査を行っていない。</summary>
    public static DiagnosticDescriptor GeneratedFilesMissing { get; } = new(
        id: "UE0001",
        title: "エンジンが生成するファイルが無いため、一部の検査を行っていません",
        messageFormat: "宣言が無いことを根拠にする検査 (HL0301 / HL0310 など) を行っていません。"
            + "ビルドのときにエンジンが生成するファイル ({0}) の中身が分からないため、宣言の一覧が不完全です。"
            + "--unreal-generate で C++ から作り、設定ファイルの unreal: generated に書くと検査できます。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "エンジンは、C++ に書いた uniform buffer の構造体から、シェーダーをコンパイルする直前に "
            + "/Engine/Generated/ の宣言をメモリ上で作ります。ディスクには無いので、そのままでは宣言の一覧が揃わず、"
            + "「宣言が無い」ことを根拠にする検査 (HL0301 / HL0310 など) を見送ります。"
            + "--unreal-generate で同じ宣言をエンジンとプロジェクトの C++ から作り、unreal: generated に書くと検査できます。",
        helpLinkUri: DocumentationLinks.For("UE0001"));

    /// <summary>マテリアルごとに生成されるファイルの中身が分からないので、宣言の検査を行っていない。</summary>
    public static DiagnosticDescriptor MaterialGeneratedFilesUnknown { get; } = new(
        id: "UE0002",
        title: "マテリアルごとに生成されるファイルの中身が分からないため、一部の検査を行っていません",
        messageFormat: "宣言が無いことを根拠にする検査 (HL0301 / HL0310 など) を行っていません。"
            + "マテリアルごとに生成されるファイル ({0}) の中身が分からないため、宣言の一覧が不完全です。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "Material.ush と VertexFactory.ush は、エンジンがマテリアルと頂点ファクトリごとに作ります。"
            + "中身はマテリアルで変わるので、Shaderlyn は作れません。これらを取り込むシェーダー (マテリアルのシェーダー) では、"
            + "「宣言が無い」ことを根拠にする検査 (HL0301 / HL0310 など) を見送ります。"
            + "利用者には直せないので、知らせが要らなければ設定ファイルの rules で無効にしてください。",
        helpLinkUri: DocumentationLinks.For("UE0002"));
}
