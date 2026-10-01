using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 必ず取り込むべきヘッダがあることを確かめる。
/// </summary>
/// <remarks>
/// <b>取り込んだ先が取り込んだものも数える。</b>
/// 自社のヘッダをまとめて配る構成では、利用者が書くのは 1 行だけになる。
/// 問いは「最終的にそのコードが入っているか」なので、辿り着けていれば満たされている。
/// </remarks>
public sealed class RequiredIncludeAnalyzer(params string[] required) : DiagnosticAnalyzer
{
    private readonly ImmutableArray<string> _required = [.. required];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.MissingInclude];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterSyntaxTreeAction(c =>
        {
            if (c.Unit.GetModel<ShaderCompilation>() is not { } compilation)
            {
                return;
            }

            foreach (AnalyzedProgram program in compilation.Programs)
            {
                ImmutableArray<IncludeReference> includes = program.Tree.PreprocessResult.Includes;

                foreach (string path in _required)
                {
                    ImmutableHashSet<string> wanted = [path];

                    if (!includes.Any(i => CookbookIncludes.Matches(wanted, i.Path)))
                    {
                        c.ReportDiagnostic(
                            CookbookRules.MissingInclude, CookbookProgramSpan.Of(program), path);
                    }
                }
            }
        });
    }
}
