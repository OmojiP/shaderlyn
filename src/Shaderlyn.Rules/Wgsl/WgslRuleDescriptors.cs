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
}
