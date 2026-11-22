using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// GLSL の古い組み込み関数 (<c>texture2D</c>・<c>textureCube</c> など) の呼び出しを報告する。
/// </summary>
/// <remarks>
/// <para>
/// <b>GLSL だけに当てる例である。</b> <see cref="Languages"/> を <see cref="ShaderLanguages.Glsl"/> にすると、
/// <c>.vert</c>・<c>.frag</c> などの GLSL のファイルだけが渡ってくる。HLSL のファイルには当たらない。
/// </para>
/// <para>
/// これらは GLSL 1.30 で非推奨になり、core プロファイルと GLSL ES 3.00 では使用できない。
/// 利用者が同じ名前の関数を定義している場合は報告しない (呼び出しが組み込みでなく利用者の関数に解決する)。
/// </para>
/// </remarks>
public sealed class DeprecatedGlslFunctionAnalyzer : ShaderRuleAnalyzer
{
    private static readonly ImmutableDictionary<string, string> Replacements = new Dictionary<string, string>
    {
        ["texture2D"] = "texture",
        ["texture2DLod"] = "textureLod",
        ["texture2DProj"] = "textureProj",
        ["textureCube"] = "texture",
        ["textureCubeLod"] = "textureLod",
        ["texture3D"] = "texture",
        ["shadow2D"] = "texture",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.DeprecatedGlslFunction];

    protected override ShaderLanguages Languages => ShaderLanguages.Glsl;

    protected override void InitializeShader(ShaderAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<InvocationExpressionSyntax>(c =>
        {
            if (c.Node.Target is not IdentifierExpressionSyntax name
                || !Replacements.TryGetValue(name.Name, out string? replacement)
                || c.Program.Tree.Root.Declarations.OfType<FunctionDeclarationSyntax>().Any(f => f.Name == name.Name))
            {
                return;
            }

            c.ReportDiagnostic(CookbookRules.DeprecatedGlslFunction, name.Span, name.Name, replacement);
        });
    }
}
