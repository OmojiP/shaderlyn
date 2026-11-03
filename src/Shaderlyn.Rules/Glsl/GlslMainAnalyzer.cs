using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// <c>main</c> の定義の形を検査する (GL0322)。glslang 16.6.0 の ParseHelper.cpp の <c>handleFunctionDefinition</c> を移したもの。
/// </summary>
/// <remarks>
/// <c>main</c> は仮引数を取れず (<c>void main(void)</c> は書ける)、戻り値は <c>void</c> でなければならない
/// (「function cannot take any parameter(s)」「entry point cannot return a value」)。定義だけを見る。
/// </remarks>
internal sealed class GlslMainAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.MainSignature];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not FunctionDeclarationSyntax { Body: not null, Name: "main" } function)
            {
                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null)
            {
                continue;
            }

            if (function.ParameterList.Any(p => !(p.Type.Name == "void" && p.NameToken.Text.Length == 0)))
            {
                requirement.Report(GlslRuleDescriptors.MainSignature, function.NameToken, "main は仮引数を取れません。");
            }

            if (function.ReturnType.Name != "void")
            {
                requirement.Report(GlslRuleDescriptors.MainSignature, function.NameToken, $"main の戻り値は void でなければなりません (今は {function.ReturnType.Name})。");
            }
        }
    }
}
