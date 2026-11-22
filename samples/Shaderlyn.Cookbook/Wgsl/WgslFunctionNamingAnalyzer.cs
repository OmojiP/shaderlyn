using System.Collections.Immutable;
using System.Text;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// WGSL の関数の名前が小文字と <c>_</c> だけ (<c>snake_case</c>) で書かれているかを検査する。
/// </summary>
/// <remarks>
/// <para>
/// <b>WGSL だけに当てる例である。</b> <see cref="Languages"/> を <see cref="ShaderLanguages.Wgsl"/> にすると、<c>.wgsl</c> だけが渡ってくる。
/// HLSL や GLSL では <c>camelCase</c> の名前が普通なので、言語を絞らないと誤検出になる。
/// </para>
/// <para>
/// WGSL の <c>fn</c> は、ほかの言語の関数と同じ <see cref="FunctionDeclarationSyntax"/> として渡ってくる。
/// <c>@vertex</c>・<c>@fragment</c> などの属性は <see cref="FunctionDeclarationSyntax.Attributes"/> に入る。
/// </para>
/// </remarks>
public sealed class WgslFunctionNamingAnalyzer : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.WgslFunctionNaming];

    protected override ShaderLanguages Languages => ShaderLanguages.Wgsl;

    protected override void InitializeShader(ShaderAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<FunctionDeclarationSyntax>(c =>
        {
            string name = c.Node.Name;

            if (c.Compilation.IsReportable(c.Node.NameToken) && name.Any(char.IsUpper))
            {
                c.ReportDiagnostic(CookbookRules.WgslFunctionNaming, c.Node.NameToken.Span, name, ToSnakeCase(name));
            }
        });
    }

    /// <summary><c>shadeSurface</c> を <c>shade_surface</c> にする。</summary>
    /// <param name="name">名前。</param>
    /// <returns>小文字と <c>_</c> の名前。</returns>
    private static string ToSnakeCase(string name)
    {
        StringBuilder builder = new();

        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && name[i - 1] != '_' && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
