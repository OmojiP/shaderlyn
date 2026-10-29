using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// GLSL ES で、精度を決めていない型の宣言を検査する (GL0201)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>setPrecisionDefaults</c> と <c>precisionQualifierCheck</c> を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// ES では型ごとに既定の精度があり、無い型の宣言には精度の修飾子が要る。
/// フラグメントシェーダーの <c>float</c> には既定が無い (<c>int</c> / <c>uint</c> は <c>mediump</c>)。ほかのステージは <c>int</c> / <c>uint</c> / <c>float</c> とも <c>highp</c>。
/// サンプラーは <c>sampler2D</c> / <c>samplerCube</c> / <c>samplerExternalOES</c> だけが <c>lowp</c> で、ほか (イメージを含む) は既定が無い。
/// </para>
/// <para>
/// 検査するのは、変数・仮引数・関数の戻り値・構造体とブロックのメンバーの宣言である (glslang の文法の fully_specified_type と struct_declaration)。
/// <c>precision mediump float;</c> は、書いた位置より後ろの宣言の既定を決める。範囲 (関数の中) は見ない。
/// </para>
/// <para>
/// デスクトップの GLSL では既定がすべて <c>highp</c> なので、報告することは無い。
/// </para>
/// </remarks>
internal sealed class GlslPrecisionAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.PrecisionRequired];

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
            if (GlslRequirements.Create(context, compilation, program, reported) is not { IsEs: true } requirement
                || compilation.TypeSystem.ForTree(program.Tree) is not GlslTypeSystem types)
            {
                continue;
            }

            Dictionary<string, bool> defaults = new(StringComparer.Ordinal)
            {
                ["int"] = true,
                ["uint"] = true,
                ["float"] = requirement.Stage != GlslShaderStage.Fragment,
                ["sampler2D"] = true,
                ["samplerCube"] = true,
                ["samplerExternalOES"] = true,
                ["atomic_uint"] = true,
            };

            foreach (Core.Syntax.SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    // precision mediump float;
                    case IncompleteDeclarationSyntax { Tokens: [{ Text: "precision" }, { Text: "highp" or "mediump" or "lowp" }, { } type, ..] }:
                        defaults[type.Text] = true;

                        if (type.Text == "int")
                        {
                            defaults["uint"] = true;
                        }

                        break;

                    case VariableDeclarationSyntax variable:
                        Check(requirement, types, defaults, variable.Type, variable.ModifierTokens);
                        break;

                    case ParameterSyntax parameter:
                        Check(requirement, types, defaults, parameter.Type, parameter.ModifierTokens);
                        break;

                    case FunctionDeclarationSyntax function:
                        Check(requirement, types, defaults, function.ReturnType, function.ModifierTokens);
                        break;

                    default:
                        break;
                }
            }
        }
    }

    /// <summary>宣言 1 つの型の精度を検査する。</summary>
    private static void Check(
        GlslRequirements requirement,
        GlslTypeSystem types,
        Dictionary<string, bool> defaults,
        TypeSyntax type,
        ImmutableArray<ShaderSyntaxToken> modifiers)
    {
        if (modifiers.Concat(type.ModifierTokens).Any(m => m.Text is "highp" or "mediump" or "lowp")
            || BasicTypeOf(types, type.NameToken.Text) is not { } basic
            || defaults.GetValueOrDefault(basic))
        {
            return;
        }

        requirement.Report(GlslRuleDescriptors.PrecisionRequired, type.NameToken, basic);
    }

    /// <summary>精度を持つ型の、既定の精度を引く名前 (glslang の基本の型)。サンプラーとイメージは型の名前のまま。</summary>
    private static string? BasicTypeOf(GlslTypeSystem types, string name)
    {
        if (types.TryDescribeNumeric(name, out ShaderNumericShape shape))
        {
            return shape.BaseName is "float" or "int" or "uint" ? shape.BaseName : null;
        }

        if (name == "atomic_uint")
        {
            return name;
        }

        return GlslKeywords.IsBuiltInType(name)
               && (name.Contains("sampler", StringComparison.Ordinal) || name.Contains("texture", StringComparison.Ordinal)
                   || name.Contains("image", StringComparison.Ordinal) || name.Contains("subpassInput", StringComparison.Ordinal))
            ? name
            : null;
    }
}
