using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace Shaderlyn.Unreal;

/// <summary>
/// マテリアルのシェーダーが取り込む <c>/Engine/Generated/Material.ush</c> の代わりを作る。
/// </summary>
/// <remarks>
/// <para>
/// <b>エンジンは、マテリアルごとに <c>MaterialTemplate.ush</c> の穴 (<c>%{…}</c>) を埋めて <c>Material.ush</c> を作る。</b>
/// 穴の中身はマテリアルのアセットのノードから作るので、シェーダーのソースと C++ だけでは決まらない。
/// ここでは、どのマテリアルでも形の決まっている部分を C++ から作り、マテリアルごとに変わる部分は既定値で埋める。
/// </para>
/// <list type="bullet">
///   <item><description>
///   <c>pixel_material_inputs</c> (<c>FPixelMaterialInputs</c> のメンバー): <c>HLSLMaterialTranslator.cpp</c> の共有する属性
///   (<c>SharedPixelProperties</c>) を、<c>MaterialAttributeDefinitionMap.cpp</c> の名前と型で並べる (エンジンと同じ)
///   </description></item>
///   <item><description>数の穴 (<c>num_material_texcoords</c> など): 0</description></item>
///   <item><description>値を返す関数の本体 (<c>get_material_…</c>): <c>return 0</c></description></item>
///   <item><description>ほかの穴 (ノードから作る計算や宣言): 空</description></item>
/// </list>
/// <para>
/// これを生成物のフォルダーに置くと <c>/Engine/Generated/Material.ush</c> が解決でき、マテリアルのシェーダーでも宣言の検査を行う (UE0002 にならない)。
/// 生成物のフォルダーのファイルは利用者のコードではないので、この中の報告は出ない。
/// </para>
/// </remarks>
public static partial class UnrealMaterialGenerator
{
    /// <summary>書き出すファイルの名前。生成物のフォルダーの直下に置く。</summary>
    public const string FileName = "Material.ush";

    /// <summary>マテリアルの uniform buffer の名前 (<c>/Engine/Generated/UniformBuffers/Material.ush</c>)。</summary>
    public const string UniformBufferName = "Material";

    /// <summary>
    /// 代わりの <c>Material.ush</c> を書き出す。
    /// </summary>
    /// <param name="outputDirectory">生成物のフォルダー。</param>
    /// <param name="engineDirectory">エンジンのフォルダー (<c>…/UE_5.5/Engine</c>)。</param>
    /// <returns>書き出したファイルのパス。テンプレートか C++ が見つからなければ <see langword="null"/>。</returns>
    public static string? Write(string outputDirectory, string engineDirectory)
    {
        ArgumentNullException.ThrowIfNull(outputDirectory);
        ArgumentNullException.ThrowIfNull(engineDirectory);

        string template = Path.Combine(engineDirectory, "Shaders", "Private", "MaterialTemplate.ush");
        string materials = Path.Combine(engineDirectory, "Source", "Runtime", "Engine", "Private", "Materials");
        string definitions = Path.Combine(materials, "MaterialAttributeDefinitionMap.cpp");
        string translator = Path.Combine(materials, "HLSLMaterialTranslator.cpp");

        if (!File.Exists(template) || !File.Exists(definitions) || !File.Exists(translator))
        {
            return null;
        }

        string members = PixelMaterialInputs(File.ReadAllText(definitions), File.ReadAllText(translator));
        string path = Path.Combine(outputDirectory, FileName);
        File.WriteAllText(path, Fill(File.ReadAllText(template), members));

        // テンプレートが取り込むマテリアルの uniform buffer。中身 (マテリアルの式の値やテクスチャ) はマテリアルごとに変わり、使うのも穴の中だけなので、空にする。
        string uniformBuffers = Path.Combine(outputDirectory, "UniformBuffers");
        Directory.CreateDirectory(uniformBuffers);
        File.WriteAllText(
            Path.Combine(uniformBuffers, UniformBufferName + ".ush"),
            "// Shaderlyn が作った。マテリアルの uniform buffer の代わり。中身はマテリアルごとに変わるので、何も宣言しない。\n");

        return path;
    }

    /// <summary>テンプレートの穴を埋める。</summary>
    /// <param name="template"><c>MaterialTemplate.ush</c> の中身。</param>
    /// <param name="pixelMaterialInputs"><c>FPixelMaterialInputs</c> のメンバー。</param>
    /// <returns>埋めたもの。</returns>
    internal static string Fill(string template, string pixelMaterialInputs)
    {
        StringBuilder builder = new();
        builder.Append("// Shaderlyn が MaterialTemplate.ush の穴を既定値で埋めて作った。実際のマテリアルの中身ではない。\n");

        string filled = LineDirective().Replace(template, string.Empty);
        filled = Hole().Replace(filled, match => match.Groups["name"].Value switch
        {
            "pixel_material_inputs" => pixelMaterialInputs,
            var name when name.StartsWith("num_", StringComparison.Ordinal) => "0",
            var name when name.StartsWith("get_", StringComparison.Ordinal) => "return 0",
            _ => string.Empty,
        });

        builder.Append(filled);
        return builder.ToString();
    }

    /// <summary>
    /// <c>FPixelMaterialInputs</c> のメンバーを、エンジンと同じく作る (<c>FHLSLMaterialTranslator::GetSharedInputsMaterialCode</c>)。
    /// </summary>
    /// <param name="definitions"><c>MaterialAttributeDefinitionMap.cpp</c> の中身。</param>
    /// <param name="translator"><c>HLSLMaterialTranslator.cpp</c> の中身。</param>
    /// <returns>メンバーの宣言。</returns>
    internal static string PixelMaterialInputs(string definitions, string translator)
    {
        // 属性の名前と型 (Add(FGuid(…), TEXT("BaseColor"), MP_BaseColor, MCT_Float3, …))。同じ属性の 2 度目は使わない。
        Dictionary<string, (string Name, string Type)> attributes = new(StringComparer.Ordinal);

        foreach (Match match in AttributeDefinition().Matches(definitions))
        {
            attributes.TryAdd(match.Groups["property"].Value, (match.Groups["name"].Value, match.Groups["type"].Value));
        }

        ImmutableArray<string> shared = [.. SharedProperty().Matches(translator).Select(m => m.Groups["property"].Value).Distinct(StringComparer.Ordinal)];
        StringBuilder builder = new();

        foreach (string property in shared)
        {
            if (!attributes.TryGetValue(property, out (string Name, string Type) attribute))
            {
                continue;
            }

            // サブサーフェスの色は、色とプロファイルを合わせた float4 で、名前は Subsurface である (エンジンと同じ)。
            (string name, string type) = property == "MP_SubsurfaceColor" ? ("Subsurface", "MCT_Float4") : attribute;
            builder.Append('\t').Append(HlslType(type)).Append(' ').Append(name).Append(";\n");
        }

        // Substrate を有効にしたときだけエンジンが足すメンバー。マクロで守られた側にだけ置く。
        builder.Append("\t#if SUBSTRATE_USE_FULLYSIMPLIFIED_MATERIAL == 1\n\tFSubstrateData FullySimplifiedFrontMaterial;\n\t#endif\n");
        return builder.ToString();
    }

    /// <summary>属性の値の型 (<c>EMaterialValueType</c>) を HLSL の型にする (<c>HLSLTypeString</c>)。</summary>
    private static string HlslType(string type) => type switch
    {
        "MCT_Float" or "MCT_Float1" => "MaterialFloat",
        "MCT_Float2" => "MaterialFloat2",
        "MCT_Float3" => "MaterialFloat3",
        "MCT_Float4" => "MaterialFloat4",
        "MCT_ShadingModel" or "MCT_UInt" or "MCT_UInt1" => "uint",
        "MCT_Substrate" => "FSubstrateData",
        "MCT_MaterialAttributes" => "FMaterialAttributes",
        "MCT_Bool" => "bool",
        _ => "MaterialFloat4",
    };

    [GeneratedRegex("""^#line\s+%\{line_number\}.*$""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LineDirective();

    [GeneratedRegex("""%\{(?<name>[a-z0-9_]+)\}""", RegexOptions.CultureInvariant)]
    private static partial Regex Hole();

    [GeneratedRegex("""Add\(\s*FGuid\([^)]*\)\s*,\s*TEXT\("(?<name>[A-Za-z0-9_]+)"\)\s*,\s*(?<property>MP_[A-Za-z0-9_]+)\s*,\s*(?<type>MCT_[A-Za-z0-9_]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeDefinition();

    [GeneratedRegex("""SharedPixelProperties\[(?<property>MP_[A-Za-z0-9_]+)\]\s*=\s*true""", RegexOptions.CultureInvariant)]
    private static partial Regex SharedProperty();
}
