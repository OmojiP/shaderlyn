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
/// スコープとセマンティクスを取るアトミックとバリアの関数 (<c>GL_KHR_memory_scope_semantics</c>) は、それらの実引数が定数か、
/// セマンティクスの組み合わせが正しいかも見る (GL0314。glslang の <c>memorySemanticsCheck</c>)。
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
        [GlslRuleDescriptors.ConstantArgumentRequired, GlslRuleDescriptors.ArgumentOutOfRange, GlslRuleDescriptors.InvalidMemorySemantics];

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

            CheckMemorySemantics(at, name, arguments);
        }

        /// <summary>メモリのセマンティクスの定数 (glslang の Initialize.cpp と同じ値)。</summary>
        private static readonly Dictionary<string, long> SemanticsConstants = new(StringComparer.Ordinal)
        {
            ["gl_SemanticsRelaxed"] = 0x0,
            ["gl_SemanticsAcquire"] = 0x2,
            ["gl_SemanticsRelease"] = 0x4,
            ["gl_SemanticsAcquireRelease"] = 0x8,
            ["gl_SemanticsMakeAvailable"] = 0x2000,
            ["gl_SemanticsMakeVisible"] = 0x4000,
            ["gl_SemanticsVolatile"] = 0x8000,
            ["gl_StorageSemanticsNone"] = 0x0,
            ["gl_StorageSemanticsBuffer"] = 0x40,
            ["gl_StorageSemanticsShared"] = 0x100,
            ["gl_StorageSemanticsImage"] = 0x800,
            ["gl_StorageSemanticsOutput"] = 0x1000,
        };

        private const long Acquire = 0x2;
        private const long Release = 0x4;
        private const long AcquireRelease = 0x8;
        private const long MakeAvailable = 0x2000;
        private const long MakeVisible = 0x4000;
        private const long Volatile = 0x8000;
        private const long MemoryOrder = Acquire | Release | AcquireRelease;

        /// <summary>
        /// スコープとセマンティクスを取るアトミックとバリアの関数の、最初の定数の実引数と、記憶域のセマンティクスとセマンティクスの位置
        /// (glslang の memorySemanticsCheck)。比較と交換の関数は、等しくないときの組 (semStorageUnequal、semUnequal) も持つ。
        /// </summary>
        private (int First, int Storage, int Semantics, int Storage2, int Semantics2)? SemanticsOperands(string name, ExpressionSyntax[] arguments)
        {
            bool multisample = name.StartsWith("imageAtomic", StringComparison.Ordinal)
                               && SamplerOf(arguments) is { } image && image.Contains("MS", StringComparison.Ordinal);
            int ms = multisample ? 1 : 0;

            return name switch
            {
                "atomicAdd" or "atomicMin" or "atomicMax" or "atomicAnd" or "atomicOr" or "atomicXor" or "atomicExchange" or "atomicStore"
                    when arguments.Length > 3 => (2, 3, 4, -1, -1),
                "atomicLoad" when arguments.Length > 3 => (1, 2, 3, -1, -1),
                "atomicCompSwap" when arguments.Length > 3 => (3, 4, 5, 6, 7),
                "imageAtomicAdd" or "imageAtomicMin" or "imageAtomicMax" or "imageAtomicAnd" or "imageAtomicOr" or "imageAtomicXor"
                    or "imageAtomicExchange" or "imageAtomicStore"
                    when arguments.Length > 4 + ms => (3 + ms, 4 + ms, 5 + ms, -1, -1),
                "imageAtomicLoad" when arguments.Length > 4 + ms => (2 + ms, 3 + ms, 4 + ms, -1, -1),
                "imageAtomicCompSwap" when arguments.Length > 4 + ms => (4 + ms, 5 + ms, 6 + ms, 7 + ms, 8 + ms),
                "controlBarrier" or "controlBarrierArrive" or "controlBarrierWait" when arguments.Length > 0 => (0, 2, 3, -1, -1),
                "memoryBarrier" when arguments.Length > 0 => (0, 1, 2, -1, -1),
                _ => null,
            };
        }

        /// <summary>スコープとセマンティクスの実引数を検査する (glslang の memorySemanticsCheck)。</summary>
        private void CheckMemorySemantics(ShaderSyntaxToken at, string name, ExpressionSyntax[] arguments)
        {
            if (SemanticsOperands(name, arguments) is not { } operands)
            {
                return;
            }

            if (arguments.Skip(operands.First).Any(_evaluator.IsCertainlyNotConstant))
            {
                requirement.Report(GlslRuleDescriptors.ConstantArgumentRequired, at, "スコープとセマンティクスの実引数");
                return;
            }

            long? Value(int index) => index < 0 ? 0 : index < arguments.Length ? SemanticsValue(arguments[index]) : null;

            if (Value(operands.Storage) is not { } storage
                || Value(operands.Semantics) is not { } semantics
                || Value(operands.Storage2) is not { } storage2
                || Value(operands.Semantics2) is not { } semantics2)
            {
                return;
            }

            bool compareSwap = operands.Storage2 >= 0;
            bool store = name is "atomicStore" or "imageAtomicStore";
            bool load = name is "atomicLoad" or "imageAtomicLoad";
            List<string> problems = [];

            if (((semantics | semantics2) & ~(MemoryOrder | MakeAvailable | MakeVisible | Volatile)) != 0)
            {
                problems.Add("セマンティクスに使えない値が入っています");
            }

            if (((storage | storage2) & ~(0x40L | 0x100L | 0x800L | 0x1000L)) != 0)
            {
                problems.Add("記憶域のセマンティクスに使えない値が入っています");
            }

            if (!IsSingleOrder(semantics) || !IsSingleOrder(semantics2))
            {
                problems.Add("セマンティクスには gl_SemanticsAcquire・gl_SemanticsRelease・gl_SemanticsAcquireRelease を 1 つしか入れられません");
            }

            if (((semantics & MemoryOrder) != 0 && storage == 0) || ((semantics2 & MemoryOrder) != 0 && storage2 == 0))
            {
                problems.Add("gl_SemanticsAcquire・gl_SemanticsRelease・gl_SemanticsAcquireRelease を使うときは、記憶域のセマンティクスを 0 にできません");
            }

            if ((storage != 0 && (semantics & MemoryOrder) == 0) || (storage2 != 0 && (semantics2 & MemoryOrder) == 0))
            {
                problems.Add("記憶域のセマンティクスが 0 でないときは、セマンティクスに gl_SemanticsAcquire・gl_SemanticsRelease・gl_SemanticsAcquireRelease のどれかが要ります");
            }

            if (storage != 0 && name == "controlBarrierArrive" && (semantics & ~MakeAvailable) != Release)
            {
                problems.Add("controlBarrierArrive のセマンティクスは gl_SemanticsRelease (と gl_SemanticsMakeAvailable) でなければなりません");
            }

            if (storage != 0 && name == "controlBarrierWait" && (semantics & ~MakeVisible) != Acquire)
            {
                problems.Add("controlBarrierWait のセマンティクスは gl_SemanticsAcquire (と gl_SemanticsMakeVisible) でなければなりません");
            }

            if (((semantics & MakeAvailable) != 0 && (semantics & (Release | AcquireRelease)) == 0)
                || ((semantics2 & MakeAvailable) != 0 && (semantics2 & (Release | AcquireRelease)) == 0))
            {
                problems.Add("gl_SemanticsMakeAvailable には gl_SemanticsRelease か gl_SemanticsAcquireRelease が要ります");
            }

            if (((semantics & MakeVisible) != 0 && (semantics & (Acquire | AcquireRelease)) == 0)
                || ((semantics2 & MakeVisible) != 0 && (semantics2 & (Acquire | AcquireRelease)) == 0))
            {
                problems.Add("gl_SemanticsMakeVisible には gl_SemanticsAcquire か gl_SemanticsAcquireRelease が要ります");
            }

            if (store && (semantics & Acquire) != 0)
            {
                problems.Add("ストアには gl_SemanticsAcquire を使えません");
            }

            if (load && (semantics & Release) != 0)
            {
                problems.Add("ロードには gl_SemanticsRelease を使えません");
            }

            if ((store || load) && (semantics & AcquireRelease) != 0)
            {
                problems.Add("ロードとストアには gl_SemanticsAcquireRelease を使えません");
            }

            if (name == "memoryBarrier" && (semantics & MemoryOrder) == 0)
            {
                problems.Add("memoryBarrier のセマンティクスには gl_SemanticsAcquire・gl_SemanticsRelease・gl_SemanticsAcquireRelease のどれかが要ります");
            }

            if (name is "memoryBarrier" or "controlBarrier" or "controlBarrierArrive" or "controlBarrierWait" && (semantics & Volatile) != 0)
            {
                problems.Add($"{name} には gl_SemanticsVolatile を使えません");
            }

            if (compareSwap)
            {
                if ((semantics2 & (Release | AcquireRelease)) != 0)
                {
                    problems.Add("等しくないときのセマンティクス (semUnequal) に gl_SemanticsRelease と gl_SemanticsAcquireRelease は使えません");
                }

                if ((semantics2 & Acquire) != 0 && (semantics & (Acquire | AcquireRelease)) == 0)
                {
                    problems.Add("等しいときのセマンティクス (semEqual) が gl_SemanticsAcquire か gl_SemanticsAcquireRelease でなければ、semUnequal に gl_SemanticsAcquire は使えません");
                }

                if ((semantics2 & MakeVisible) != 0 && (semantics & MakeVisible) == 0)
                {
                    problems.Add("semEqual に gl_SemanticsMakeVisible が無ければ、semUnequal にも使えません");
                }

                if ((storage2 & ~storage) != 0)
                {
                    problems.Add("等しくないときの記憶域のセマンティクス (semStorageUnequal) には、semStorageEqual に無いものを入れられません");
                }

                if (((semantics ^ semantics2) & Volatile) != 0)
                {
                    problems.Add("gl_SemanticsVolatile は semEqual と semUnequal の両方に入れるか、どちらにも入れないかにしてください");
                }
            }

            foreach (string problem in problems)
            {
                requirement.Report(GlslRuleDescriptors.InvalidMemorySemantics, at, name, problem);
            }

            static bool IsSingleOrder(long value) => (value & MemoryOrder) is var order && (order & (order - 1)) == 0;
        }

        /// <summary>セマンティクスの実引数の値。gl_Semantics* の定数と、その | の組み合わせ。分からなければ <see langword="null"/>。</summary>
        private long? SemanticsValue(ExpressionSyntax expression) => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => SemanticsValue(parenthesized.Expression),
            IdentifierExpressionSyntax identifier when SemanticsConstants.TryGetValue(identifier.Name, out long value) => value,
            BinaryExpressionSyntax { OperatorToken.Text: "|" } binary
                => SemanticsValue(binary.Left) is { } left && SemanticsValue(binary.Right) is { } right ? left | right : null,
            _ => _evaluator.Evaluate(expression) is { Kind: GlslConstantKind.Int or GlslConstantKind.Uint } constant ? constant.Integer : null,
        };

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
