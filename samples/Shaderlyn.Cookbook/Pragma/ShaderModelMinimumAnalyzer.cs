using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cookbook;

/// <summary>
/// <c>#pragma target</c> の下限を確かめる。
/// </summary>
/// <remarks>
/// <para>
/// <b>書かれていないことも報告する。</b>
/// 省略したときのシェーダーモデルは下限より低い。
/// 「書いていない」は分からないことではなく、確かめられる事実である。
/// </para>
/// <para>
/// <b>数として読めない指定は見送る。</b>
/// <c>es3.1</c> や <c>gl4.1</c> のような書き方があり、これらは数の大小で比べられない。
/// </para>
/// </remarks>
public sealed class ShaderModelMinimumAnalyzer(string minimum) : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.ShaderModelTooLow];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterSyntaxTreeAction(c =>
        {
            if (c.Unit.GetModel<ShaderCompilation>() is not { } compilation
                || !double.TryParse(minimum, CultureInfo.InvariantCulture, out double required))
            {
                return;
            }

            foreach (AnalyzedProgram program in compilation.Programs)
            {
                string? written = program.Tree.Pragmas
                    .Where(p => string.Equals(p.Name, "target", StringComparison.Ordinal))
                    .Select(p => p.Arguments.FirstOrDefault()?.Text)
                    .LastOrDefault(t => t is not null);

                if (written is null)
                {
                    c.ReportDiagnostic(
                        CookbookRules.ShaderModelTooLow, CookbookProgramSpan.Of(program), "指定なし", minimum);
                    continue;
                }

                // 数として読めないものは比べようがない。報告しない。
                if (double.TryParse(written, CultureInfo.InvariantCulture, out double value) && value < required)
                {
                    c.ReportDiagnostic(
                        CookbookRules.ShaderModelTooLow, CookbookProgramSpan.Of(program), written, minimum);
                }
            }
        });
    }
}
