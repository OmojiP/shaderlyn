using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 64 ビットの添字 (<c>GL_EXT_shader_64bit_indexing</c>) が要る書き方を検査する (GL0001)。glslang 16.6.0 の ParseHelper.cpp の 64 ビットの添字の検査を移したもの。
/// </summary>
/// <remarks>
/// 64 ビットの整数 (<c>int64_t</c> / <c>uint64_t</c>) の <c>[ ]</c> の添字、協調行列と協調ベクトルの読み書きの関数の 64 ビットの整数の実引数、
/// <c>length64()</c> には、<c>GL_EXT_shader_64bit_indexing</c> が要る。
/// </remarks>
internal sealed class Glsl64BitIndexingAnalyzer : SemanticRuleAnalyzer
{
    private const string Extension = "GL_EXT_shader_64bit_indexing";

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.NotAvailable];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl || !compilation.CodeTokens.Any(t => t.Text.Contains("64", StringComparison.Ordinal)))
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (InvocationExpressionSyntax or ElementAccessExpressionSyntax))
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

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            switch (node)
            {
                case ElementAccessExpressionSyntax element when Is64Bit(binder, element.Index):
                    requirement.RequireExtensions(element.OpenBracket, [Extension], "64 ビットの整数の添字");
                    break;

                case InvocationExpressionSyntax { Target: MemberAccessExpressionSyntax { Name: "length64" } member }:
                    requirement.RequireExtensions(member.NameToken, [Extension], "'length64()'");
                    break;

                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } call
                    when (callee.Name.StartsWith("coopMatLoad", StringComparison.Ordinal) || callee.Name.StartsWith("coopMatStore", StringComparison.Ordinal)
                          || callee.Name.StartsWith("coopVec", StringComparison.Ordinal))
                         && compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty
                         && call.ArgumentExpressions.Any(a => Is64Bit(binder, a)):
                    requirement.RequireExtensions(callee.Identifier, [Extension], $"64 ビットの整数を渡す '{callee.Name}'");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>式が 64 ビットの整数のスカラーか。</summary>
    private static bool Is64Bit(ExpressionTypeBinder binder, ExpressionSyntax expression)
        => binder.GetTypesByConfiguration(expression) is [{ Type: "int64_t" or "uint64_t" }];
}
