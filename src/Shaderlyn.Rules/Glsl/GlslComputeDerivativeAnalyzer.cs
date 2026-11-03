using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Glsl;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 計算・タスク・メッシュのシェーダーの微分 (<c>GL_NV_compute_shader_derivatives</c> / <c>GL_KHR_compute_shader_derivatives</c>) を検査する (GL0001 / GL0104)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>requireDerivativeLayout</c> と、ファイルの終わりの derivative_group の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// タスクとメッシュのシェーダーの微分の関数 (<c>dFdx</c> など) には <c>GL_KHR_compute_shader_derivatives</c> が要る (NV の拡張は計算シェーダーだけ)。
/// <c>GL_KHR_compute_shader_derivatives</c> を有効にしたら、微分の関数より前に <c>layout(derivative_group_quadsKHR) in;</c> か
/// <c>layout(derivative_group_linearKHR) in;</c> が要り、ファイルのどこかに KHR の名前の derivative_group が要る (ファイルの終わりで報告する)。
/// </para>
/// <para>
/// derivative_group_quads は local_size_x と local_size_y が 2 の倍数、derivative_group_linear はワークグループの大きさが 4 の倍数でなければならない
/// (特殊化の定数で決める大きさは見ない)。
/// </para>
/// </remarks>
internal sealed class GlslComputeDerivativeAnalyzer : SemanticRuleAnalyzer
{
    private const string Khr = "GL_KHR_compute_shader_derivatives";

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.NotAvailable, GlslRuleDescriptors.LayoutRequired];

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
            if (GlslRequirements.Create(context, compilation, program, reported) is not { } requirement
                || requirement.Stage is not (GlslShaderStage.Compute or GlslShaderStage.Task or GlslShaderStage.Mesh))
            {
                continue;
            }

            Check(compilation, requirement, program);
        }
    }

    private static void Check(ShaderCompilation compilation, GlslRequirements requirement, AnalyzedProgram program)
    {
        IncompleteDeclarationSyntax[] layouts = [.. program.Tree.Root.ChildNodes().OfType<IncompleteDeclarationSyntax>().Where(d => d.Tokens.Any(t => t.Text == "in"))];
        (ShaderSyntaxToken Name, string Id)[] groups =
        [
            .. layouts.SelectMany(d => GlslLayoutQualifier.Read(d.Tokens))
                .Select(e => (e.Name, Id: e.Name.Text.ToLowerInvariant()))
                .Where(e => e.Id.StartsWith("derivative_group_", StringComparison.Ordinal)),
        ];

        foreach (InvocationExpressionSyntax call in program.Tree.Root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (call.Target is not IdentifierExpressionSyntax { Name: "dFdx" or "dFdy" or "fwidth" or "dFdxFine" or "dFdyFine" or "fwidthFine" or "dFdxCoarse" or "dFdyCoarse" or "fwidthCoarse" } callee
                || !compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty
                || !compilation.IsReportable(callee.Identifier))
            {
                continue;
            }

            // どちらの拡張も無ければ、組み込み関数の拡張の検査 (GlslBuiltinRequirementAnalyzer) が報告する。
            if (requirement.Stage != GlslShaderStage.Compute && requirement.IsOn("GL_NV_compute_shader_derivatives", callee.Identifier)
                && !requirement.RequireExtensions(callee.Identifier, [Khr], $"タスクとメッシュのシェーダーの '{callee.Name}'"))
            {
                continue;
            }

            if (requirement.IsOn(Khr, callee.Identifier)
                && ReferenceEquals(callee.Identifier.Source, program.Tree.Text)
                && !groups.Any(g => ReferenceEquals(g.Name.Source, program.Tree.Text) && g.Name.Span.Start < callee.Identifier.Span.Start))
            {
                requirement.Report(GlslRuleDescriptors.LayoutRequired, callee.Identifier,
                    $"GL_KHR_compute_shader_derivatives を有効にしたら、'{callee.Name}' より前に layout(derivative_group_quadsKHR) in; か layout(derivative_group_linearKHR) in; を書いてください。");
            }
        }

        // ファイルの終わりの検査。取り込んだファイルの宣言は見ない。
        if (layouts.Any(d => !ReferenceEquals(d.Tokens[0].Source, program.Tree.Text)))
        {
            return;
        }

        // glslang はファイルの終わり (最後の改行の後) の位置で報告する。字句の終わりのトークンは最後のトークンの直後にあるので、位置を作る。
        ShaderSyntaxToken end = new(ShaderSyntaxKind.EndOfFileToken, program.Tree.Text, new TextSpan(program.Tree.Text.Length, 0), string.Empty);

        if (requirement.Extensions.IsOn(Khr, null) && !groups.Any(g => g.Id.EndsWith("khr", StringComparison.Ordinal)))
        {
            requirement.Report(GlslRuleDescriptors.LayoutRequired, end, "GL_KHR_compute_shader_derivatives を有効にしたら、derivative_group_quadsKHR か derivative_group_linearKHR の layout が要ります。");
        }

        if (groups.Length == 0)
        {
            return;
        }

        long? Size(string name) => layouts.SelectMany(d => GlslLayoutQualifier.Read(d.Tokens)).Where(e => e.Name.Text == name).Select(e => GlslLayoutQualifier.Evaluate(e.Value, _ => null)).LastOrDefault() is { } value
            ? (long)value.Value
            : null;

        bool Specialized(string name) => layouts.SelectMany(d => GlslLayoutQualifier.Read(d.Tokens)).Any(e => e.Name.Text == name + "_id");

        (long x, long y, long z) = (Size("local_size_x") ?? 1, Size("local_size_y") ?? 1, Size("local_size_z") ?? 1);

        if (groups[^1].Id.StartsWith("derivative_group_quads", StringComparison.Ordinal))
        {
            if (!Specialized("local_size_x") && x % 2 != 0 || !Specialized("local_size_y") && y % 2 != 0)
            {
                requirement.Report(GlslRuleDescriptors.LayoutRequired, end, "derivative_group_quads では、local_size_x と local_size_y を 2 の倍数にしなければなりません。");
            }
        }
        else if (!Specialized("local_size_x") && !Specialized("local_size_y") && !Specialized("local_size_z") && x * y * z % 4 != 0)
        {
            requirement.Report(GlslRuleDescriptors.LayoutRequired, end, "derivative_group_linear では、ワークグループの大きさ (local_size_x × local_size_y × local_size_z) を 4 の倍数にしなければなりません。");
        }
    }
}
