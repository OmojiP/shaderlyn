using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>書き込めるバッファ・テクスチャの名前の接頭辞を揃える。</summary>
public sealed class WritableBufferNamingAnalyzer(string prefix) : HlslRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [CookbookRules.WritableBufferNaming];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<VariableDeclarationSyntax>(c =>
        {
            if (!c.Node.Type.Name.StartsWith("RW", StringComparison.Ordinal))
            {
                return;
            }

            foreach (VariableDeclaratorSyntax variable in c.Node.Variables)
            {
                if (!variable.Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    c.ReportDiagnostic(
                        CookbookRules.WritableBufferNaming, variable.NameToken.Span, variable.Name, prefix);
                }
            }
        });
    }
}
