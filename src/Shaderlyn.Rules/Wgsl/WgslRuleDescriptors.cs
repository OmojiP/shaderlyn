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
