using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 型と修飾子のキーワードを、使える版・拡張・ステージ・場所で検査する (GL0001 / GL0002 / GL0101)。
/// </summary>
/// <remarks>
/// glslang が型と修飾子を読むときの検査 (glslang.y の type_specifier_nonarray と修飾子の規則) を、
/// 宣言・仮引数・値の作成 (<c>uint(x)</c>)・修飾子だけの宣言 (<c>layout(…) in;</c>) の、型と修飾子のトークンに掛ける
/// (<see cref="GlslRequirements.CheckTypeKeyword"/>、<see cref="GlslRequirements.CheckQualifierKeyword"/>)。
/// </remarks>
internal sealed class GlslDeclarationRequirementAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [GlslRuleDescriptors.NotAvailable, GlslRuleDescriptors.NotAvailableInStage, GlslRuleDescriptors.QualifierNotAllowed];

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
            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null)
            {
                continue;
            }

            bool global = !node.Ancestors().Any(a => a is FunctionDeclarationSyntax);

            switch (node)
            {
                case TypeSyntax type when GlslKeywords.IsBuiltInType(type.NameToken.Text):
                    requirement.CheckTypeKeyword(type.NameToken);
                    break;

                // 値の作成 (uint(x)、double(x))。
                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } when GlslKeywords.IsBuiltInType(callee.Name):
                    requirement.CheckTypeKeyword(callee.Identifier);
                    break;

                case VariableDeclarationSyntax variable:
                    CheckQualifiers(requirement, variable.ModifierTokens, global);
                    break;

                // 仮引数の修飾子は、glslang では関数の外 (大域の範囲) で読む。
                case ParameterSyntax parameter:
                    CheckQualifiers(requirement, parameter.ModifierTokens, global: true);
                    requirement.ArrayOfArrayVersionCheck(parameter.NameToken, parameter.ArrayDimensions);
                    break;

                case FunctionDeclarationSyntax function:
                    CheckQualifiers(requirement, function.ModifierTokens, global);
                    break;

                case StructDeclarationSyntax { NameOverride: not null } block:
                    CheckQualifiers(requirement, [.. block.Modifiers, block.Keyword], global);
                    break;

                case InterfaceBlockDeclarationSyntax block:
                    CheckQualifiers(requirement, block.Modifiers, global);
                    break;

                case VariableDeclaratorSyntax declarator:
                    requirement.ArrayOfArrayVersionCheck(declarator.NameToken, declarator.ArrayDimensions);
                    break;

                // 修飾子だけの宣言 (layout(local_size_x = 8) in;、invariant gl_Position;)。
                case IncompleteDeclarationSyntax incomplete:
                    CheckQualifiers(requirement, incomplete.Tokens, global);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>修飾子のトークンを検査する。括弧の中 (<c>layout(…)</c> の値) は飛ばす。</summary>
    private static void CheckQualifiers(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> tokens, bool global)
    {
        int depth = 0;

        foreach (ShaderSyntaxToken token in tokens)
        {
            switch (token.Kind)
            {
                case ShaderSyntaxKind.OpenParenToken:
                    depth++;
                    break;

                case ShaderSyntaxKind.CloseParenToken:
                    depth--;
                    break;

                case ShaderSyntaxKind.IdentifierToken when depth == 0 && GlslKeywords.Qualifiers.Contains(token.Text):
                    requirement.CheckQualifierKeyword(token, global);
                    break;

                default:
                    break;
            }
        }
    }
}
