using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Cookbook;

/// <summary>1 つの <c>SubShader</c> に置ける <c>Pass</c> の数を制限する。</summary>
public sealed class PassCountLimitAnalyzer(int maximum) : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.TooManyPasses];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<SubShaderSyntax>(c =>
        {
            int passes = c.Node.DescendantNodesAndSelf().OfType<PassSyntax>().Count();

            if (passes > maximum)
            {
                c.ReportDiagnostic(CookbookRules.TooManyPasses, c.Node.Keyword.Span, passes, maximum);
            }
        });
    }
}
