using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 計算シェーダーで、ワークグループの大きさを決める前に <c>gl_WorkGroupSize</c> を読んでいないかを検査する (GL0104)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>rValueErrorCheck</c> の
/// 「can't read from gl_WorkGroupSize before a fixed workgroup size has been declared」を移したもの。
/// </summary>
/// <remarks>
/// 大きさは <c>layout(local_size_x = …) in;</c> (<c>local_size_x_id</c> などの特殊化の定数も) で決まる。
/// その宣言より前 (宣言が無ければどこでも) の <c>gl_WorkGroupSize</c> を報告する。
/// </remarks>
internal sealed class GlslWorkGroupSizeAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.LayoutRequired];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (GlslRequirements.Create(context, compilation, program, reported) is not { Stage: GlslShaderStage.Compute } requirement)
            {
                continue;
            }

            IncompleteDeclarationSyntax[] declarations =
            [
                .. program.Tree.Root.DescendantNodesAndSelf()
                    .OfType<IncompleteDeclarationSyntax>()
                    .Where(d => d.Tokens.Any(t => t.Text.StartsWith("local_size_", StringComparison.Ordinal)) && d.Tokens.Any(t => t.Text == "in")),
            ];

            // 取り込んだファイルの宣言は、書いた位置を比べられない。
            if (declarations.Any(d => !ReferenceEquals(d.Tokens[0].Source, program.Tree.Text)))
            {
                continue;
            }

            int? declared = declarations.Length == 0 ? null : declarations.Min(d => d.Tokens[0].Span.Start);

            foreach (IdentifierExpressionSyntax use in program.Tree.Root.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
            {
                if (use.Name == "gl_WorkGroupSize"
                    && compilation.IsReportable(use.Identifier)
                    && ReferenceEquals(use.Identifier.Source, program.Tree.Text)
                    && (declared is not { } at || use.Identifier.Span.Start < at))
                {
                    requirement.Report(GlslRuleDescriptors.LayoutRequired, use.Identifier,
                        "gl_WorkGroupSize を読む前に、ワークグループの大きさ (layout(local_size_x = …) in;) を宣言してください。");
                }
            }
        }
    }
}
