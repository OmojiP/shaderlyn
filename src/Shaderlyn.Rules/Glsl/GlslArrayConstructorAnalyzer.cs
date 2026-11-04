using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 配列を作る式 (<c>float[3](a, b, c)</c>) を検査する (GL0319 / GL0001)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>handleConstructorCall</c> (「arrayed constructor」) と、
/// <c>constructorError</c> の配列の検査 (「array constructor must have at least one argument」
/// 「array constructor needs one argument per array element」) を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 配列を作る式は、ES でない 1.20 (か <c>GL_3DL_array_objects</c>) と ES の 3.00 から書ける (GL0001)。
/// </para>
/// <para>
/// 実引数は 1 つ以上要る。大きさを書いた配列では、実引数の数が大きさと同じでなければならない (GL0319)。
/// 大きさは 1 次元の整数のリテラルのときだけ見る。
/// </para>
/// </remarks>
internal sealed class GlslArrayConstructorAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.ArrayConstructorArguments, GlslRuleDescriptors.NotAvailable];

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
            if (node is not InvocationExpressionSyntax { Target: ArrayTypeExpressionSyntax arrayType } constructor)
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

            requirement.ArrayObjectCheck(arrayType.ElementType.Identifier, "arrayed constructor");

            int count = constructor.ArgumentExpressions.Count();

            if (count == 0)
            {
                requirement.Report(GlslRuleDescriptors.ArrayConstructorArguments, constructor.CloseParen, "配列を作るには、実引数が 1 つ以上要ります。");
            }
            else if (arrayType.ArrayRanks is [{ Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.NumericLiteralToken } size, { Kind: ShaderSyntaxKind.CloseBracketToken }]
                     && int.TryParse(size.Text.TrimEnd('u', 'U'), NumberStyles.None, CultureInfo.InvariantCulture, out int length)
                     && length != count)
            {
                requirement.Report(
                    GlslRuleDescriptors.ArrayConstructorArguments,
                    constructor.CloseParen,
                    $"{arrayType.ElementType.Name}[{length}] を作るには、要素ごとに 1 つ、実引数が {length} 個要ります (今は {count} 個)。");
            }
        }
    }
}
