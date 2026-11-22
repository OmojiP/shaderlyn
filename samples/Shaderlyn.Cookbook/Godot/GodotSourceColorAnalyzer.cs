using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// Godot の色のテクスチャ (名前に <c>albedo</c>・<c>color</c> などを含む <c>uniform sampler2D</c>) に、
/// <c>source_color</c> のヒントが付いているかを検査する。
/// </summary>
/// <remarks>
/// <para>
/// <b>Godot だけに当てる例である。</b> <see cref="Languages"/> を <see cref="ShaderLanguages.Godot"/> にすると、
/// <c>.gdshader</c>・<c>.gdshaderinc</c> だけが渡ってくる。
/// </para>
/// <para>
/// Godot 4 は、<c>source_color</c> の付いていないテクスチャを sRGB から線形に変換しない。色のテクスチャに付け忘れると、色が明るく褪せて見える。
/// <c>uniform sampler2D albedo_tex : source_color, filter_linear;</c> のヒントは、宣言子の <see cref="VariableDeclaratorSyntax.Semantics"/> に 1 つずつ入る。
/// </para>
/// <para>
/// 画面・深度・法線のテクスチャ (<c>hint_screen_texture</c> など) は、名前に <c>color</c> を含んでいても報告しない。
/// </para>
/// </remarks>
public sealed class GodotSourceColorAnalyzer : ShaderRuleAnalyzer
{
    /// <summary>色のテクスチャとみなす名前の一部。</summary>
    private static readonly ImmutableArray<string> ColorWords = ["albedo", "color", "colour", "diffuse", "base_col"];

    /// <summary>エンジンが用意するテクスチャを指すヒント。</summary>
    private static readonly ImmutableHashSet<string> EngineTextureHints =
        ["hint_screen_texture", "hint_depth_texture", "hint_normal_roughness_texture"];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.GodotSourceColor];

    protected override ShaderLanguages Languages => ShaderLanguages.Godot;

    protected override void InitializeShader(ShaderAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<VariableDeclarationSyntax>(c =>
        {
            if (!c.Node.HasModifier("uniform") || c.Node.Type.Name is not ("sampler2D" or "sampler2DArray"))
            {
                return;
            }

            foreach (VariableDeclaratorSyntax variable in c.Node.Variables)
            {
                string[] hints = [.. variable.Semantics.Select(s => s.Name)];

                if (c.Compilation.IsReportable(variable.NameToken)
                    && ColorWords.Any(w => variable.Name.Contains(w, StringComparison.OrdinalIgnoreCase))
                    && !hints.Contains("source_color")
                    && !hints.Any(EngineTextureHints.Contains))
                {
                    c.ReportDiagnostic(CookbookRules.GodotSourceColor, variable.NameToken.Span, variable.Name);
                }
            }
        });
    }
}
