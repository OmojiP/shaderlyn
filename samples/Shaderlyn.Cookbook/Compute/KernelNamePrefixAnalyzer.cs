using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cookbook;

/// <summary>カーネル名の接頭辞を揃える。</summary>
public sealed class KernelNamePrefixAnalyzer(string prefix) : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [CookbookRules.KernelNamePrefix];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterSyntaxTreeAction(c =>
        {
            if (c.Unit.GetModel<ShaderCompilation>() is not { IsStandaloneHlsl: true } compilation)
            {
                return;
            }

            // 同じカーネルは 1 ブロックずつあるので、そのまま数えてよい。
            foreach (AnalyzedProgram program in compilation.Programs)
            {
                if (program.KernelName is { } kernel && !kernel.StartsWith(prefix, StringComparison.Ordinal))
                {
                    c.ReportDiagnostic(
                        CookbookRules.KernelNamePrefix, new Core.Text.TextSpan(0, 0), kernel, prefix);
                }
            }
        });
    }
}
