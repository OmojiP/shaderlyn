using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 組み込みの関数と変数に要る拡張と SPIR-V のバージョンを検査する (GL0001)。
/// </summary>
/// <remarks>
/// <para>
/// 拡張で足される組み込み (<c>subgroupAdd</c> は GL_KHR_shader_subgroup_arithmetic、<c>gl_SubgroupSize</c> は GL_KHR_shader_subgroup_basic) は、
/// その拡張を有効にしてから使わなければならない (glslang の handleFunctionCall と handleVariable の <c>requireExtensions</c>)。
/// どの組み込みにどの拡張が要るかは、glslang から作った表 (<see cref="GlslBuiltins"/>) にある。
/// </para>
/// <para>
/// サブグループの関数は SPIR-V 1.3 からで、8・16・64 ビットの値を渡すには GL_EXT_shader_subgroup_extended_types_* が要る
/// (glslang の builtInOpCheck)。
/// </para>
/// <para>
/// 利用者が同じ名前の関数を宣言していれば、組み込みとは見なさない。
/// </para>
/// </remarks>
internal sealed class GlslBuiltinRequirementAnalyzer : SemanticRuleAnalyzer
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
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null || compilation.TypeSystem.ForTree(program.Tree) is not GlslTypeSystem types)
            {
                continue;
            }

            switch (node)
            {
                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation:
                    AnalyzeCall(compilation, program, types, requirement, invocation, callee);
                    break;

                // 利用者が宣言し直した組み込み (GL_EXT_spirv_intrinsics の spirv_decorate で宣言した gl_BaryCoordSmoothAMD など) は、その宣言を使う。
                case IdentifierExpressionSyntax identifier
                    when identifier.Name.StartsWith("gl_", StringComparison.Ordinal)
                         && !(identifier.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Target, identifier))
                         && types.Builtins.GetVariable(identifier.Name) is { Extensions.IsEmpty: false } variable
                         && !program.Tree.Root.Declarations.OfType<VariableDeclarationSyntax>().Any(d => d.Variables.Any(v => v.Name == identifier.Name)):
                    requirement.RequireExtensions(identifier.Identifier, variable.Extensions, $"'{identifier.Name}'");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>組み込み関数の呼び出しを検査する。</summary>
    private static void AnalyzeCall(
        ShaderCompilation compilation,
        AnalyzedProgram program,
        GlslTypeSystem types,
        GlslRequirements requirement,
        InvocationExpressionSyntax invocation,
        IdentifierExpressionSyntax callee)
    {
        string name = callee.Name;
        ImmutableArray<GlslBuiltinFunction> overloads = types.Builtins.GetFunctions(name);

        if (overloads.IsEmpty || !compilation.GetFunctionDeclarations(name, program).IsEmpty)
        {
            return;
        }

        // glslang は拡張を関数の名前ごとに決める (setFunctionExtensions)。拡張の要らない形が 1 つでもあれば、要らないものとする。
        if (overloads.All(o => !o.Extensions.IsEmpty))
        {
            requirement.RequireExtensions(callee.Identifier, [.. overloads.SelectMany(o => o.Extensions).Distinct(StringComparer.Ordinal)], $"'{name}'");
        }

        if (!name.StartsWith("subgroup", StringComparison.Ordinal))
        {
            return;
        }

        requirement.RequireSpirv(callee.Identifier, 103, $"サブグループの関数 '{name}'");

        if (invocation.ArgumentExpressions.FirstOrDefault() is not { } first
            || compilation.GetExpressionTypeBinder(program).GetEvaluatorFor(first).Evaluate(first) is not { } type
            || !types.TryDecomposeNumeric(type, out string element, out _))
        {
            return;
        }

        string? extension = element switch
        {
            "int8_t" or "uint8_t" => "GL_EXT_shader_subgroup_extended_types_int8",
            "int16_t" or "uint16_t" => "GL_EXT_shader_subgroup_extended_types_int16",
            "int64_t" or "uint64_t" => "GL_EXT_shader_subgroup_extended_types_int64",
            "float16_t" => "GL_EXT_shader_subgroup_extended_types_float16",
            _ => null,
        };

        if (extension is not null)
        {
            requirement.RequireExtensions(callee.Identifier, [extension], $"サブグループの関数に {type} を渡すこと");
        }
    }
}
