using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 組み込み関数の、定数でなければならない実引数と、その値の範囲を検査する (GL0303 / GL0304)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>builtInOpCheck</c> のうち、実引数の定数と値の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// テクセルのオフセット (<c>textureOffset</c> などの <c>offset</c>)、<c>textureGather</c> の成分の番号、<c>textureGatherOffsets</c> のオフセット、
/// サブグループのクラスターの大きさ、SPIR-V 1.5 より前の <c>subgroupBroadcast</c> の id、<c>rayQueryGetIntersection*</c> の committed を見る。
/// </para>
/// <para>
/// 定数でないと言い切れる式 (<c>const</c> でない変数・仮引数を使う式) だけを報告する。分からない式は報告しない。
/// 値は glslang の既定の資源の上限 (<c>gl_MinProgramTexelOffset</c> = -8、<c>gl_MaxProgramTexelOffset</c> = 7) で確かめる。
/// </para>
/// </remarks>
internal sealed class GlslBuiltinArgumentAnalyzer : SemanticRuleAnalyzer
{
    private const int MinProgramTexelOffset = -8;
    private const int MaxProgramTexelOffset = 7;

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [GlslRuleDescriptors.ConstantArgumentRequired, GlslRuleDescriptors.ArgumentOutOfRange];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, Checker?> checkers = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation)
            {
                continue;
            }

            if (!checkers.TryGetValue(program, out Checker? checker))
            {
                checker = checkers[program] =
                    GlslRequirements.Create(context, compilation, program, reported) is { } requirement
                    && compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types
                        ? new Checker(requirement, types, compilation.GetExpressionTypeBinder(program))
                        : null;
            }

            if (checker is not null
                && !checker.Types.Builtins.GetFunctions(callee.Name).IsEmpty
                && compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty)
            {
                checker.Check(callee, [.. invocation.ArgumentExpressions]);
            }
        }
    }

    /// <summary>1 つの木の組み込み関数の呼び出しを検査する。</summary>
    private sealed class Checker(GlslRequirements requirement, GlslTypeSystem types, ExpressionTypeBinder binder)
    {
        private readonly GlslConstantEvaluator _evaluator = new(binder);

        public GlslTypeSystem Types => types;

        public void Check(IdentifierExpressionSyntax callee, ExpressionSyntax[] arguments)
        {
            string name = callee.Name;
            ShaderSyntaxToken at = callee.Identifier;

            if (TexelOffsetIndex(name, arguments) is { } offset && offset < arguments.Length)
            {
                CheckTexelOffset(at, arguments[offset]);
            }

            if (name.StartsWith("textureGather", StringComparison.Ordinal) || name.StartsWith("sparseTextureGather", StringComparison.Ordinal))
            {
                CheckGather(at, name, arguments);
            }

            if (name is "subgroupClusteredAdd" or "subgroupClusteredMul" or "subgroupClusteredMin" or "subgroupClusteredMax"
                    or "subgroupClusteredAnd" or "subgroupClusteredOr" or "subgroupClusteredXor" && arguments.Length >= 2)
            {
                CheckClusterSize(at, arguments[1]);
            }

            if (name is "subgroupBroadcast" or "subgroupQuadBroadcast" && arguments.Length >= 2 && requirement.SpirvVersion is { } spirv && spirv < 105)
            {
                RequireConstant(at, arguments[1], "id");
            }

            if (name.StartsWith("rayQueryGetIntersection", StringComparison.Ordinal) && arguments.Length >= 2)
            {
                RequireConstant(at, arguments[1], "committed");
            }
        }

        /// <summary>テクセルのオフセットの実引数の位置 (glslang の builtInOpCheck の EOpTextureOffset など)。</summary>
        private int? TexelOffsetIndex(string name, ExpressionSyntax[] arguments)
        {
            int? index = name switch
            {
                "textureOffset" or "textureProjOffset" or "sparseTextureOffsetARB" => 2,
                "textureLodOffset" or "textureProjLodOffset" or "sparseTextureLodOffsetARB" => 3,
                "textureGradOffset" or "textureProjGradOffset" or "sparseTextureGradOffsetARB" => 4,
                "texelFetchOffset" or "sparseTexelFetchOffsetARB" => SamplerOf(arguments) is { } sampler && sampler.Contains("Rect", StringComparison.Ordinal) ? 2 : 3,
                _ => null,
            };

            // float16 の比較値を取る影のサンプラーでは、比較値の分だけ後ろへずれる。
            if (index is { } i && arguments.Length > 1
                && SamplerOf(arguments) is { } shadow && shadow.Contains("Shadow", StringComparison.Ordinal)
                && TypeOf(arguments[1]) is { } coordinate && types.TryDecomposeNumeric(coordinate, out string element, out _) && element == "float16_t")
            {
                return i + 1;
            }

            return index;
        }

        private void CheckTexelOffset(ShaderSyntaxToken at, ExpressionSyntax offset)
        {
            if (_evaluator.IsCertainlyNotConstant(offset))
            {
                if (!requirement.IsOn("GL_EXT_texture_offset_non_const", at) && !requirement.IsOn("GL_NV_gpu_shader5", at))
                {
                    requirement.Report(GlslRuleDescriptors.ConstantArgumentRequired, at, "テクセルのオフセット");
                }

                return;
            }

            if (_evaluator.EvaluateComponents(offset) is { } components
                && components.Any(c => c.Integer is < MinProgramTexelOffset or > MaxProgramTexelOffset))
            {
                requirement.Report(
                    GlslRuleDescriptors.ArgumentOutOfRange,
                    at,
                    "テクセルのオフセット",
                    string.Create(CultureInfo.InvariantCulture, $"{MinProgramTexelOffset} から {MaxProgramTexelOffset} (gl_MinProgramTexelOffset から gl_MaxProgramTexelOffset)"));
            }
        }

        /// <summary><c>textureGather</c> の成分の番号と、<c>textureGatherOffsets</c> のオフセット。</summary>
        private void CheckGather(ShaderSyntaxToken at, string name, ExpressionSyntax[] arguments)
        {
            bool shadow = SamplerOf(arguments) is { } sampler && sampler.Contains("Shadow", StringComparison.Ordinal);

            if (name is "textureGatherOffsets" or "sparseTextureGatherOffsetsARB" && arguments.Length > (shadow ? 3 : 2))
            {
                if (_evaluator.IsCertainlyNotConstant(arguments[shadow ? 3 : 2]) && !requirement.IsOn("GL_NV_gpu_shader5", at))
                {
                    requirement.Report(GlslRuleDescriptors.ConstantArgumentRequired, at, "オフセットの配列 (offsets)");
                }
            }

            if (shadow || name.StartsWith("sparse", StringComparison.Ordinal))
            {
                return;
            }

            int component = name == "textureGather" ? 2 : 3;

            if (component >= arguments.Length)
            {
                return;
            }

            if (_evaluator.IsCertainlyNotConstant(arguments[component]))
            {
                requirement.Report(GlslRuleDescriptors.ConstantArgumentRequired, at, "成分の番号 (comp)");
            }
            else if (_evaluator.Evaluate(arguments[component]) is { Integer: < 0 or > 3 })
            {
                requirement.Report(GlslRuleDescriptors.ArgumentOutOfRange, at, "成分の番号 (comp)", "0、1、2、3 のどれか");
            }
        }

        /// <summary>サブグループのクラスターの大きさ。1 以上の 2 のべき乗の定数でなければならない。</summary>
        private void CheckClusterSize(ShaderSyntaxToken at, ExpressionSyntax size)
        {
            if (_evaluator.IsCertainlyNotConstant(size))
            {
                requirement.Report(GlslRuleDescriptors.ConstantArgumentRequired, at, "クラスターの大きさ (clusterSize)");
            }
            else if (_evaluator.Evaluate(size) is { } value && (value.Integer < 1 || (value.Integer & (value.Integer - 1)) != 0))
            {
                requirement.Report(GlslRuleDescriptors.ArgumentOutOfRange, at, "クラスターの大きさ (clusterSize)", "1 以上の 2 のべき乗");
            }
        }

        private void RequireConstant(ShaderSyntaxToken at, ExpressionSyntax argument, string what)
        {
            if (_evaluator.IsCertainlyNotConstant(argument))
            {
                requirement.Report(GlslRuleDescriptors.ConstantArgumentRequired, at, what);
            }
        }

        private string? SamplerOf(ExpressionSyntax[] arguments) => arguments.Length > 0 ? TypeOf(arguments[0]) : null;

        private string? TypeOf(ExpressionSyntax expression)
            => binder.GetTypesByConfiguration(expression) is [{ Type: { } type }] ? type : null;
    }
}
