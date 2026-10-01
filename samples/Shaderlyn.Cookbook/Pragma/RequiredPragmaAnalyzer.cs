using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cookbook;

/// <summary>
/// コードブロックに必須の <c>#pragma</c> があることを確かめる。
/// </summary>
/// <remarks>
/// <para>
/// <b><c>#pragma</c> は展開後の構文木に残らない。</b>
/// プリプロセッサが記録したものを見る。
/// </para>
/// <para>
/// <b>ヘッダが書いた <c>#pragma</c> も数える。</b>
/// 共通のヘッダで <c>multi_compile_instancing</c> を配っている構成は普通にあり、
/// そこで書かれていれば要件は満たされている。
/// </para>
/// <para>
/// <b>Pass ごとに見る。</b> <c>#pragma</c> は Pass ごとに効くためである。
/// </para>
/// </remarks>
public sealed class RequiredPragmaAnalyzer(string name, string? argument = null) : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.MissingPragma];

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
                if (program.Tree.Pragmas.Any(Matches))
                {
                    continue;
                }

                c.ReportDiagnostic(
                    CookbookRules.MissingPragma,
                    CookbookProgramSpan.Of(program),
                    argument is null ? name : name + " " + argument);
            }
        });
    }

    /// <summary>その <c>#pragma</c> が求めているものかを判定する。</summary>
    /// <param name="pragma">判定する <c>#pragma</c>。</param>
    /// <returns>求めているものであれば <see langword="true"/>。</returns>
    private bool Matches(PragmaDirective pragma)
        => string.Equals(pragma.Name, name, StringComparison.Ordinal)
           && (argument is null || pragma.Arguments.Any(a => a.TextIs(argument)));
}
