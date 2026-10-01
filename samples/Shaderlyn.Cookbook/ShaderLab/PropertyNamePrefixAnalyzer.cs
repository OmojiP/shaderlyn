using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Cookbook;

/// <summary>プロパティ名の接頭辞を揃える。</summary>
public sealed class PropertyNamePrefixAnalyzer(string prefix) : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [CookbookRules.PropertyNamePrefix];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<PropertyDeclarationSyntax>(c =>
        {
            if (!c.Node.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                c.ReportDiagnostic(
                    CookbookRules.PropertyNamePrefix, c.Node.NameToken.Span, c.Node.Name, prefix);
            }
        });
    }
}
