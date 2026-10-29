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
}
