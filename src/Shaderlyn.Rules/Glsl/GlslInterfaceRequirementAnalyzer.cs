using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// ステージの入力と出力の変数の宣言を、使える版とプロファイルで検査する (GL0001)。
/// </summary>
/// <remarks>
/// glslang 16.6.0 の ParseHelper.cpp の <c>globalQualifierFixCheck</c> (<c>in</c> / <c>out</c> の版)、
/// <c>globalQualifierTypeCheck</c> (入力と出力に使える型)、<c>arrayQualifierError</c> / <c>arrayError</c> (入力と出力の配列) を移したもの。
/// 関数の外の変数の宣言 (ブロックのメンバーを除く) だけを見る。
/// </remarks>
internal sealed partial class GlslInterfaceRequirementAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.NotAvailable];

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
            if (GlslRequirements.Create(context, compilation, program, reported) is not { Stage: { } stage } requirement)
            {
                continue;
            }

            foreach (DeclarationSyntax declaration in program.Tree.Root.Declarations)
            {
                switch (declaration)
                {
                    case VariableDeclarationSyntax variable:
                        Check(requirement, program, stage, variable.ModifierTokens, variable.Type.Name, variable.Type.ArrayRanks, [.. variable.Variables]);
                        break;

                    // 構造体の宣言と同時に宣言した変数 (varying struct S { … } s;)。
                    case StructDeclarationSyntax { Keyword.Text: "struct", Name: { } name } structure when !structure.Modifiers.IsEmpty:
                        Check(requirement, program, stage, structure.Modifiers, name, [], [.. structure.Declarators.Select(d => d.Node).OfType<VariableDeclaratorSyntax>()]);
                        break;

                    default:
                        break;
                }
            }
        }
    }

    /// <summary>1 つの宣言を検査する。</summary>
    private static void Check(
        GlslRequirements requirement,
        AnalyzedProgram program,
        GlslShaderStage stage,
        ImmutableArray<ShaderSyntaxToken> modifiers,
        string typeName,
        ImmutableArray<ShaderSyntaxToken> typeRanks,
        IReadOnlyList<VariableDeclaratorSyntax> declarators)
    {
        bool input = false;
        bool output = false;
        int depth = 0;

        foreach (ShaderSyntaxToken modifier in modifiers)
        {
            depth += modifier.Kind switch
            {
                ShaderSyntaxKind.OpenParenToken => 1,
                ShaderSyntaxKind.CloseParenToken => -1,
                _ => 0,
            };

            if (depth > 0)
            {
                continue;
            }

            switch (modifier.Text)
            {
                case "in":
                    requirement.ProfileRequires(modifier, GlslProfileMask.None, 130, [], "ステージの入力の in");
                    requirement.ProfileRequires(modifier, GlslProfileMask.Es, 300, [], "ステージの入力の in");
                    input = true;
                    break;

                case "out":
                    requirement.ProfileRequires(modifier, GlslProfileMask.None, 130, [], "ステージの出力の out");
                    requirement.ProfileRequires(modifier, GlslProfileMask.Es, 300, [], "ステージの出力の out");
                    output = true;
                    break;

                case "attribute":
                    input = true;
                    break;

                case "varying":
                    input |= stage != GlslShaderStage.Vertex;
                    output |= stage == GlslShaderStage.Vertex;
                    break;

                default:
                    break;
            }
        }

        if (input == output || modifiers.IsEmpty)
        {
            return;
        }

        ShaderSyntaxToken at = modifiers[0];
        StructDeclarationSyntax? structure = program.Structs.FirstOrDefault(s => s.Name == typeName && s.Keyword.Text == "struct");

        if (IntegerType().IsMatch(typeName) || DoubleType().IsMatch(typeName))
        {
            requirement.ProfileRequires(at, GlslProfileMask.Es, 300, [], "整数と double の入力と出力");
            requirement.ProfileRequires(at, GlslProfileMask.NotEs, 130, [], "整数と double の入力と出力");
        }

        int typeDimensions = typeRanks.Count(t => t.Kind == ShaderSyntaxKind.OpenBracketToken);

        if (input && stage == GlslShaderStage.Vertex)
        {
            if (typeDimensions > 0 || declarators.Any(d => d.IsArray))
            {
                requirement.RequireProfile(at, GlslProfileMask.NotEs, "頂点シェーダーの入力の配列");
                requirement.ProfileRequires(at, GlslProfileMask.None, 150, [], "頂点シェーダーの入力の配列");
            }

            if (DoubleType().IsMatch(typeName))
            {
                requirement.ProfileRequires(at, GlslProfileMask.NotEs, 410, ["GL_NV_gpu_shader5", "GL_ARB_vertex_attrib_64bit"], "頂点シェーダーの double の入力");
            }
        }

        if (structure is not null && (input && stage == GlslShaderStage.Fragment || output && stage == GlslShaderStage.Vertex))
        {
            string feature = input ? "フラグメントシェーダーの構造体の入力" : "頂点シェーダーの構造体の出力";
            requirement.ProfileRequires(at, GlslProfileMask.Es, 300, [], feature);
            requirement.ProfileRequires(at, GlslProfileMask.NotEs, 150, [], feature);

            if (structure.Fields.Any(f => program.Structs.Any(s => s.Name == f.Type.Name && s.Keyword.Text == "struct")))
            {
                requirement.RequireProfile(at, GlslProfileMask.NotEs, $"{feature} (構造体を含むもの)");
            }

            if (structure.Fields.Any(f => !f.Type.ArrayRanks.IsEmpty || f.Variables.Any(v => v.IsArray)))
            {
                requirement.RequireProfile(at, GlslProfileMask.NotEs, $"{feature} (配列を含むもの)");
            }
        }

        // 配列の入力と出力 (arrayError)。glslang は宣言子ごとに見る。
        foreach (VariableDeclaratorSyntax declarator in declarators)
        {
            int dimensions = typeDimensions + declarator.ArrayRankTokens.Count(t => t.Kind == ShaderSyntaxKind.OpenBracketToken);

            if (dimensions == 0)
            {
                continue;
            }

            string? feature = (input, output, stage) switch
            {
                (_, true, GlslShaderStage.Vertex) when dimensions > 1 => "頂点シェーダーの出力の配列の配列",
                (_, true, GlslShaderStage.Vertex) when structure is not null => "頂点シェーダーの出力の構造体の配列",
                (true, _, GlslShaderStage.Fragment) when dimensions > 1 => "フラグメントシェーダーの入力の配列の配列",
                (true, _, GlslShaderStage.Fragment) when structure is not null => "フラグメントシェーダーの入力の構造体の配列",
                (_, true, GlslShaderStage.Fragment) when dimensions > 1 => "フラグメントシェーダーの出力の配列の配列",
                _ => null,
            };

            if (feature is not null)
            {
                requirement.RequireProfile(declarator.NameToken, GlslProfileMask.NotEs, feature);
            }
        }
    }

    /// <summary>整数の型 (glslang の isTypeInt)。</summary>
    [GeneratedRegex(@"^(u?int|[iu]vec[234]|u?int(8|16|64)_t|[iu](8|16|64)vec[234])$")]
    private static partial Regex IntegerType();

    /// <summary>double の型。</summary>
    [GeneratedRegex(@"^(double|dvec[234]|dmat[234](x[234])?)$")]
    private static partial Regex DoubleType();
}
