using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 決まった種類の変数でなければならない、組み込み関数の実引数を検査する (GL0306)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>builtInOpCheck</c> のうち、<c>traverseLValueBase</c> で実引数のもとの変数を見る検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// <c>interpolateAtCentroid</c> / <c>interpolateAtSample</c> / <c>interpolateAtOffset</c> の第 1 引数は、入力の変数 (<c>in</c>) か、その要素・ブロックのメンバー。
/// 成分の選び出し (<c>.xy</c>) とベクトルの添字は、デスクトップの 4.40 からしか使えない。ES では、構造体 (ブロックでない) のメンバーも使えない。
/// </para>
/// <para>
/// アトミックの関数 (<c>atomicAdd</c> など) の第 1 引数と、協調行列の読み書きの関数 (<c>coopMatLoad</c> など) の第 2 引数は、
/// buffer のブロックか shared の変数 (アトミックは <c>taskPayloadSharedEXT</c> も) か、その要素・メンバー。
/// </para>
/// <para>
/// もとの変数を言い切れるときだけ報告する。インスタンスの名前を持つブロック、<c>gl_</c> の組み込みの変数、
/// バッファの参照 (<c>buffer_reference</c>) を通るかもしれない式は報告しない。
/// </para>
/// </remarks>
internal sealed class GlslVariableArgumentAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.VariableArgumentRequired];

    /// <summary>実引数のもとの変数の記憶域。</summary>
    private enum Storage
    {
        Unknown,
        In,
        Buffer,
        Shared,
        TaskPayload,
        Other,
    }

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

            (int Index, bool Interpolant, bool TaskPayload)? requiredOrNull = Required(callee.Name);
            bool imageAtomic = callee.Name.StartsWith("imageAtomic", StringComparison.Ordinal);

            if (requiredOrNull is null && !imageAtomic)
            {
                continue;
            }

            if (!checkers.TryGetValue(program, out Checker? checker))
            {
                checker = checkers[program] =
                    GlslRequirements.Create(context, compilation, program, reported) is { } requirement
                    && compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types
                        ? new Checker(requirement, types, compilation.GetExpressionTypeBinder(program), program)
                        : null;
            }

            ExpressionSyntax[] arguments = [.. invocation.ArgumentExpressions];

            if (checker is null
                || arguments.Length == 0
                || checker.Types.Builtins.GetFunctions(callee.Name).IsEmpty
                || !compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty)
            {
                continue;
            }

            if (imageAtomic)
            {
                checker.CheckImageFormat(callee, arguments[0]);
                continue;
            }

            (int Index, bool Interpolant, bool TaskPayload) required = requiredOrNull!.Value;

            if (required.Index >= arguments.Length)
            {
                continue;
            }

            bool? valid = required.Interpolant
                ? checker.IsInterpolant(arguments[required.Index])
                : checker.IsMemoryVariable(arguments[required.Index], required.TaskPayload);

            if (valid == false)
            {
                checker.Requirement.Report(
                    GlslRuleDescriptors.VariableArgumentRequired,
                    callee.Identifier,
                    callee.Name,
                    required.Index + 1,
                    required.Interpolant
                        ? "、入力の変数 (in) か、その配列の要素・ブロックのメンバーでなければなりません"
                        : "、buffer のブロックか shared の変数 (とその要素・メンバー) でなければなりません");
            }
        }
    }

    /// <summary>関数が、決まった種類の変数を求める実引数。</summary>
    private static (int Index, bool Interpolant, bool TaskPayload)? Required(string name) => name switch
    {
        "interpolateAtCentroid" or "interpolateAtSample" or "interpolateAtOffset" => (0, true, false),
        "atomicAdd" or "atomicMin" or "atomicMax" or "atomicAnd" or "atomicOr" or "atomicXor"
            or "atomicExchange" or "atomicCompSwap" or "atomicLoad" or "atomicStore" => (0, false, true),
        "coopMatLoad" or "coopMatStore" or "coopMatLoadNV" or "coopMatStoreNV"
            or "coopMatLoadTensorNV" or "coopMatStoreTensorNV" => (1, false, false),
        _ => null,
    };

    /// <summary>括弧を外す。</summary>
    private static ExpressionSyntax Strip(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    /// <summary>変数を指さない式 (演算・呼び出し・リテラル) か。glslang の traverseLValueBase が変数にたどり着かない式。</summary>
    private static bool IsValue(ExpressionSyntax expression) => expression is BinaryExpressionSyntax or AssignmentExpressionSyntax
        or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax or ConditionalExpressionSyntax
        or InvocationExpressionSyntax or LiteralExpressionSyntax or CastExpressionSyntax;

    /// <summary>1 つの木の実引数を検査する。</summary>
    private sealed class Checker(GlslRequirements requirement, GlslTypeSystem types, ExpressionTypeBinder binder, AnalyzedProgram program)
    {
        public GlslRequirements Requirement => requirement;

        public GlslTypeSystem Types => types;

        /// <summary>
        /// イメージのアトミックの関数に渡すイメージの形式 (glslang の builtInOpCheck の EOpImageAtomic*)。
        /// 整数のイメージは r32i / r32ui (64 ビットは r64i / r64ui)、ES の浮動小数点数のイメージは r32f でなければならない。
        /// 宣言を言い切れるイメージ (関数の外の変数と仮引数、その配列の要素) だけを見る。
        /// </summary>
        public void CheckImageFormat(IdentifierExpressionSyntax callee, ExpressionSyntax argument)
        {
            ExpressionSyntax expression = Strip(argument);

            while (expression is ElementAccessExpressionSyntax element)
            {
                expression = Strip(element.Target);
            }

            if (expression is not IdentifierExpressionSyntax identifier
                || binder.ResolveName(identifier) is not { Kind: DeclaredNameKind.Local or DeclaredNameKind.Global, Candidates: [var candidate] })
            {
                return;
            }

            ImmutableArray<ShaderSyntaxToken> modifiers = candidate.Declarator switch
            {
                ParameterSyntax parameter => parameter.ModifierTokens,
                VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: CompilationUnitSyntax } declaration } => declaration.ModifierTokens,
                _ => default,
            };

            if (modifiers.IsDefault)
            {
                return;
            }

            string type = candidate.Type.TypeName;
            string? format = modifiers.Select(m => m.Text).FirstOrDefault(GlslLayoutAnalyzer.ImageFormats.Contains);
            string? required = type switch
            {
                _ when type.StartsWith("iimage", StringComparison.Ordinal) || type.StartsWith("uimage", StringComparison.Ordinal)
                    || type.StartsWith("i64image", StringComparison.Ordinal) || type.StartsWith("u64image", StringComparison.Ordinal)
                    => format is "r32i" or "r32ui" or "r64i" or "r64ui" ? null : "layout(r32i) か layout(r32ui) のイメージ",
                _ when type.StartsWith("image", StringComparison.Ordinal) && requirement.IsEs
                    => format is "r32f" or "rg16f" or "rgba16f" ? null : "layout(r32f) のイメージ",
                _ => null,
            };

            if (required is not null)
            {
                requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, callee.Identifier, callee.Name, 1, $"、{required}でなければなりません");
            }
        }

        /// <summary>補間できる入力か。分からなければ <see langword="null"/>。</summary>
        public bool? IsInterpolant(ExpressionSyntax argument)
        {
            bool swizzleOkay = !requirement.IsEs && requirement.Version >= 440;
            ExpressionSyntax expression = Strip(argument);

            while (true)
            {
                switch (expression)
                {
                    case IdentifierExpressionSyntax identifier:
                        return Resolve(identifier) switch
                        {
                            (Storage.Unknown, _) => null,
                            (Storage.In, _) => true,
                            _ => false,
                        };

                    case MemberAccessExpressionSyntax member:
                        ExpressionSyntax target = Strip(member.Target);
                        string? targetType = binder.GetEvaluatorFor(target).Evaluate(target);

                        if (!swizzleOkay && types.TryDescribeNumeric(targetType, out ShaderNumericShape shape) && shape.Kind != ShaderNumericKind.None)
                        {
                            return false;
                        }

                        if (requirement.IsEs && program.Structs.Any(s => s.Name == targetType && s.Keyword.Text == "struct"))
                        {
                            return false;
                        }

                        expression = target;
                        break;

                    case ElementAccessExpressionSyntax element:
                        ExpressionSyntax array = Strip(element.Target);

                        if (!swizzleOkay
                            && array is IdentifierExpressionSyntax arrayName
                            && binder.ResolveName(arrayName).Candidates is { IsEmpty: false } candidates
                            && candidates.All(c => !c.Type.IsArray
                                && types.TryDescribeNumeric(c.Type.TypeName, out ShaderNumericShape element0)
                                && element0.Kind is ShaderNumericKind.Scalar or ShaderNumericKind.Vector))
                        {
                            return false;
                        }

                        expression = array;
                        break;

                    default:
                        return IsValue(expression) ? false : null;
                }
            }
        }

        /// <summary>buffer のブロックか shared の変数か。分からなければ <see langword="null"/>。</summary>
        public bool? IsMemoryVariable(ExpressionSyntax argument, bool taskPayload)
        {
            ExpressionSyntax expression = Strip(argument);
            bool viaMember = false;

            while (true)
            {
                switch (expression)
                {
                    case IdentifierExpressionSyntax identifier:
                        (Storage storage, string? type) = Resolve(identifier);

                        return storage switch
                        {
                            Storage.Unknown => null,
                            Storage.Buffer or Storage.Shared => true,
                            Storage.TaskPayload when taskPayload => true,

                            // バッファの参照の型の変数なら、メンバーの先が buffer のこともある。
                            _ when type is null || !GlslKeywords.IsBuiltInType(type) => null,
                            _ => false,
                        };

                    case MemberAccessExpressionSyntax member:
                        viaMember = true;
                        expression = Strip(member.Target);
                        break;

                    case ElementAccessExpressionSyntax element:
                        expression = Strip(element.Target);
                        break;

                    default:
                        return IsValue(expression) && !viaMember ? false : null;
                }
            }
        }

        /// <summary>名前が指す変数の記憶域と型。</summary>
        private (Storage Storage, string? Type) Resolve(IdentifierExpressionSyntax identifier)
        {
            if (identifier.Name.StartsWith("gl_", StringComparison.Ordinal))
            {
                return (Storage.Unknown, null);
            }

            NameResolution resolution = binder.ResolveName(identifier);

            if (resolution is not { Kind: DeclaredNameKind.Local or DeclaredNameKind.Global, Candidates: [var candidate] })
            {
                return (Storage.Unknown, null);
            }

            if (resolution.Kind == DeclaredNameKind.Local)
            {
                return (Storage.Other, candidate.Type.TypeName);
            }

            if (candidate.Declarator.Parent is not VariableDeclarationSyntax declaration)
            {
                return (Storage.Unknown, null);
            }

            IEnumerable<ShaderSyntaxToken> modifiers = declaration.Parent is InterfaceBlockDeclarationSyntax block
                ? block.Modifiers.Concat(declaration.ModifierTokens)
                : declaration.ModifierTokens;

            Storage storage = Storage.Other;
            int depth = 0;

            // layout(…) の中 (layout(shared) など) は記憶域ではない。
            foreach (ShaderSyntaxToken modifier in modifiers)
            {
                depth += modifier.Kind switch
                {
                    ShaderSyntaxKind.OpenParenToken => 1,
                    ShaderSyntaxKind.CloseParenToken => -1,
                    _ => 0,
                };

                storage = depth > 0 ? storage : modifier.Text switch
                {
                    "in" or "varying" => Storage.In,
                    "buffer" => Storage.Buffer,
                    "shared" => Storage.Shared,
                    "taskPayloadSharedEXT" => Storage.TaskPayload,
                    _ => storage,
                };
            }

            return (storage, candidate.Type.TypeName);
        }
    }
}
