using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Unreal.Rules;

/// <summary>
/// Unreal Engine の環境だけのルールの定義。
/// </summary>
/// <remarks>
/// どちらも「検査していない」ことを伝えるルールである (<c>CF0101</c> と同じ考え方)。
/// 原因と直し方が Unreal に固有なので、<c>CF0101</c> から分けて別の ID にしている。
/// 分けておくと、直せないもの (<see cref="MaterialGeneratedFilesUnknown"/>) だけを設定で止められる。
/// </remarks>
internal static class UnrealRuleDescriptors
{
    private const string UsageCategory = "Usage";

    /// <summary>C++ から作れる生成ファイルが無いので、宣言の検査を行っていない。</summary>
    public static DiagnosticDescriptor GeneratedFilesMissing { get; } = new(
        id: "UE0001",
        title: "エンジンが生成するファイルが無いため、一部の検査を行っていません",
        messageFormat: "宣言が無いことを根拠にする検査 (CF0301 / CF0310 など) を行っていません。"
            + "ビルドのときにエンジンが生成するファイル ({0}) の中身が分からないため、宣言の一覧が不完全です。"
            + "--unreal-generate で C++ から作り、設定ファイルの unreal: generated に書くと検査できます。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "エンジンは、C++ に書いた uniform buffer の構造体から、シェーダーをコンパイルする直前に "
            + "/Engine/Generated/ の宣言をメモリ上で作ります。ディスクには無いので、そのままでは宣言の一覧が揃わず、"
            + "「宣言が無い」ことを根拠にする検査 (CF0301 / CF0310 など) を見送ります。"
            + "--unreal-generate で同じ宣言をエンジンとプロジェクトの C++ から作り、unreal: generated に書くと検査できます。",
        helpLinkUri: DocumentationLinks.For("UE0001"));

    /// <summary>マテリアルのシェーダーが取り込む生成ファイルが無いので、宣言の検査を行っていない。</summary>
    public static DiagnosticDescriptor MaterialGeneratedFilesUnknown { get; } = new(
        id: "UE0002",
        title: "マテリアルのシェーダーが取り込む生成ファイルが無いため、一部の検査を行っていません",
        messageFormat: "宣言が無いことを根拠にする検査 (CF0301 / CF0310 など) を行っていません。"
            + "マテリアルのシェーダーが取り込む生成ファイル ({0}) が無いため、宣言の一覧が不完全です。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "Material.ush と VertexFactory.ush は、エンジンがマテリアルと頂点ファクトリごとに作ります。"
            + "ディスクには無いので、これらを取り込むシェーダー (マテリアルのシェーダー) では、"
            + "「宣言が無い」ことを根拠にする検査 (CF0301 / CF0310 など) を見送ります。"
            + "--unreal-generate で代わりを作り、unreal: generated に書くと検査できます。",
        helpLinkUri: DocumentationLinks.For("UE0002"));

    /// <summary>登録したシェーダーがどれも、解析するプラットフォームではコンパイルされないので、解析していない。</summary>
    public static DiagnosticDescriptor NotCompiledOnPlatform { get; } = new(
        id: "UE0003",
        title: "このプラットフォームではコンパイルされないシェーダーのため、解析していません",
        messageFormat: "このファイルを解析していません。登録した C++ のクラス ({0}) は、どのパーミュテーションも "
            + "ShouldCompilePermutation が PC の Direct3D 12 (SM6) では除きます。",
        category: UsageCategory,
        defaultSeverity: DiagnosticSeverity.Info,
        description: "--unreal-generate は、C++ の ShouldCompilePermutation を PC の Direct3D 12 (SM6) として読みます。"
            + "ファイルを登録したクラスがどれも、どのパーミュテーションでもコンパイルされないと分かったファイル "
            + "(モバイルだけ、コンソールだけのシェーダーなど) は、エンジンも PC ではコンパイルしません。"
            + "既定の構成で読むと、実在しない構成の誤りを報告することになるので、解析しません。",
        helpLinkUri: DocumentationLinks.For("UE0003"));
}
