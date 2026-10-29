using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 16 ビット・8 ビットの型を、保存にしか使えないときに計算に使っていないかを検査する (GL0001)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>requireFloat16Arithmetic</c> / <c>requireInt16Arithmetic</c> / <c>requireInt8Arithmetic</c> の呼び出しを移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 保存用の拡張 (<c>GL_EXT_shader_16bit_storage</c>、<c>GL_EXT_shader_8bit_storage</c>) だけを有効にしているときは、
/// 16 ビット・8 ビットの型 (<c>float16_t</c>、<c>i16vec2</c>、<c>uint8_t</c> など) を uniform と buffer のブロックの中にしか置けない。
/// 変数の宣言、仮引数、組み込み関数の実引数と戻り値、<c>[</c>、2 つ以上の成分の選び出し、値の作成、構造体と配列の代入と <c>return</c> には、
/// 算術の拡張 (<c>GL_EXT_shader_explicit_arithmetic_types</c> など) が要る。
/// </para>
/// <para>
/// 型が分からない式は検査しない。
/// </para>
/// </remarks>
internal sealed class GlslSmallTypeStorageAnalyzer : SemanticRuleAnalyzer
{
    private static readonly string[] Float16Extensions =
        ["GL_AMD_gpu_shader_half_float", "GL_EXT_shader_explicit_arithmetic_types", "GL_NV_gpu_shader5", "GL_EXT_shader_explicit_arithmetic_types_float16"];

    private static readonly string[] Int16Extensions =
        ["GL_AMD_gpu_shader_int16", "GL_EXT_shader_explicit_arithmetic_types", "GL_NV_gpu_shader5", "GL_EXT_shader_explicit_arithmetic_types_int16"];

    private static readonly string[] Int8Extensions =
        ["GL_EXT_shader_explicit_arithmetic_types", "GL_NV_gpu_shader5", "GL_EXT_shader_explicit_arithmetic_types_int8"];

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.NotAvailable];

    [Flags]
    private enum Small
    {
        None = 0,
        Float16 = 1,
        Int16 = 2,
        Int8 = 4,
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
            if (!checkers.TryGetValue(program, out Checker? checker))
            {
                checker = checkers[program] =
                    GlslRequirements.Create(context, compilation, program, reported) is { } requirement
                    && compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types
                        ? new Checker(requirement, types, compilation.GetExpressionTypeBinder(program), program, compilation)
                        : null;
            }

            checker?.Check(node);
        }
    }

    /// <summary>1 つの木を検査する。</summary>
    private sealed class Checker(
        GlslRequirements requirement,
        GlslTypeSystem types,
        ExpressionTypeBinder binder,
        AnalyzedProgram program,
        ShaderCompilation compilation)
    {
        public void Check(ShaderSyntaxNode node)
        {
            switch (node)
            {
                case VariableDeclarationSyntax variable when variable.Parent is not StructDeclarationSyntax and not Shaderlyn.Glsl.Syntax.InterfaceBlockDeclarationSyntax
                                                             && !variable.HasModifier("uniform") && !variable.HasModifier("buffer"):
                    Small declared = Of(variable.Type.NameToken.Text, depth: 0);

                    foreach (VariableDeclaratorSyntax declarator in variable.Variables)
                    {
                        Require(declarator.NameToken, declared, "qualifier", "can only be in uniform block or buffer storage");
                    }

                    break;

                case ParameterSyntax parameter:
                    Require(parameter.NameToken, Of(parameter.Type.NameToken.Text, depth: 0), parameter.Type.NameToken.Text, "can only be in uniform block or buffer storage");
                    break;

                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation:
                    CheckCall(invocation, callee);
                    break;

                case ElementAccessExpressionSyntax element when !IsArrayValue(element.Target) && TypeOf(element.Target) is { } indexed
                                                                && types.TryDescribeNumeric(indexed, out ShaderNumericShape shape) && shape.Kind == ShaderNumericKind.Vector:
                    Require(element.OpenBracket, Of(indexed, depth: 0), "[", "does not operate on types containing");
                    break;

                case MemberAccessExpressionSyntax member when member.NameToken.Text.Length > 1 && TypeOf(member.Target) is { } swizzled
                                                              && types.TryDescribeNumeric(swizzled, out ShaderNumericShape vector) && vector.Kind == ShaderNumericKind.Vector:
                    Require(member.DotToken, Of(swizzled, depth: 0), ".", "can't swizzle types containing");
                    break;

                case AssignmentExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsToken } assignment:
                    CheckWhole(assignment.OperatorToken, assignment.Left, "=");
                    break;

                case ReturnStatementSyntax { Expression: { } value } statement:
                    CheckWhole(statement.Keyword, value, "return");
                    break;

                default:
                    break;
            }
        }

        /// <summary>組み込み関数の戻り値と実引数、16 ビット・8 ビットのベクトルの値の作成。</summary>
        private void CheckCall(InvocationExpressionSyntax invocation, IdentifierExpressionSyntax callee)
        {
            ExpressionSyntax[] arguments = [.. invocation.ArgumentExpressions];

            // f16vec2(x, y) のように、成分ごとに作るもの。1 つのベクトルからの変換だけが使える。
            if (types.TryDescribeNumeric(callee.Name, out ShaderNumericShape constructed))
            {
                if (constructed.Kind == ShaderNumericKind.Vector && arguments.Length != 1)
                {
                    Require(callee.Identifier, Of(callee.Name, depth: 0), callee.Name, "vectors only take vector types");
                }

                return;
            }

            if (types.Builtins.GetFunctions(callee.Name).IsEmpty || !compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty)
            {
                return;
            }

            if (TypeOf(invocation) is { } result)
            {
                Require(callee.Identifier, Of(result, depth: 0), "built-in function", "can only be in uniform block or buffer storage");
            }

            foreach (ExpressionSyntax argument in arguments)
            {
                if (TypeOf(argument) is { } type)
                {
                    Require(callee.Identifier, Of(type, depth: 0), "built-in function", "can only be in uniform block or buffer storage");
                }
            }
        }

        /// <summary>構造体と配列を丸ごと代入するか返すとき (glslang の storage16BitAssignmentCheck)。</summary>
        private void CheckWhole(ShaderSyntaxToken at, ExpressionSyntax expression, string op)
        {
            if (TypeOf(expression) is not { } type)
            {
                return;
            }

            bool isStruct = !types.TryDescribeNumeric(type, out _);

            if (isStruct || IsArrayValue(expression))
            {
                Require(at, Of(type, depth: 0), op, isStruct ? "can't use with structs containing" : "can't use with arrays containing");
            }
        }

        private void Require(ShaderSyntaxToken at, Small small, string op, string description)
        {
            if (small.HasFlag(Small.Float16))
            {
                requirement.RequireExtensions(at, Float16Extensions, $"{op}: float16 の型 ({description})");
            }

            if (small.HasFlag(Small.Int16))
            {
                requirement.RequireExtensions(at, Int16Extensions, $"{op}: (u)int16 の型 ({description})");
            }

            if (small.HasFlag(Small.Int8))
            {
                requirement.RequireExtensions(at, Int8Extensions, $"{op}: (u)int8 の型 ({description})");
            }
        }

        /// <summary>型が含む 16 ビット・8 ビットの型。構造体はメンバーをたどる。</summary>
        private Small Of(string type, int depth)
        {
            if (types.TryDescribeNumeric(type, out ShaderNumericShape shape))
            {
                return shape.BaseName switch
                {
                    "float16_t" => Small.Float16,
                    "int16_t" or "uint16_t" => Small.Int16,
                    "int8_t" or "uint8_t" => Small.Int8,
                    _ => Small.None,
                };
            }

            if (depth > 8 || program.Structs.FirstOrDefault(s => s.Name == type) is not { } structure)
            {
                return Small.None;
            }

            Small small = Small.None;

            foreach (VariableDeclarationSyntax field in structure.Fields)
            {
                small |= Of(field.Type.NameToken.Text, depth + 1);
            }

            return small;
        }

        private string? TypeOf(ExpressionSyntax expression)
            => binder.GetTypesByConfiguration(expression) is [{ Type: { } type }] ? type : null;

        /// <summary>添字を付けない配列の変数か、配列のメンバーか。</summary>
        private bool IsArrayValue(ExpressionSyntax expression) => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => IsArrayValue(parenthesized.Expression),
            IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates.Any(c => c.Type.IsArray),
            MemberAccessExpressionSyntax member => TypeOf(member.Target) is { } target
                                                   && binder.TryGetStructField(target, member.NameToken.Text, out VariableDeclaratorSyntax? field, out _) && field.IsArray,
            _ => false,
        };
    }
}
