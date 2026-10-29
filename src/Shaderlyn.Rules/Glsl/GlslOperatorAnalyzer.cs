using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 演算子の両辺の型を検査する (GL0301)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>handleBinaryMath</c> / <c>handleUnaryMath</c> と、
/// Intermediate.cpp の <c>addPairConversion</c> / <c>promoteBinary</c> / <c>promoteUnary</c> を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 両辺の要素の型を暗黙の変換でそろえ (<c>int + float</c> は <c>float</c> へ)、そろえた型と形 (スカラー・ベクトル・行列) の組で演算子が使えるかを決める。
/// GLSL には成分を減らす変換も、スカラーをベクトルへ広げる変換 (<c>vec3 + vec4</c>) も無い。
/// </para>
/// <para>
/// 両辺がスカラー・ベクトル・行列のときだけ検査する。構造体と配列は CF0406 が、代入 (<c>=</c>) は CF0410 が見る。
/// 型が分からない辺、構成によって型が変わる辺、配列の値の辺があれば検査しない。
/// </para>
/// </remarks>
internal sealed class GlslOperatorAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.WrongOperandTypes];

    private enum Op
    {
        Relational,
        Equality,
        Logical,
        Shift,
        Mod,
        Bitwise,
        Add,
        Mul,
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
            if (node is not (BinaryExpressionSyntax or AssignmentExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax))
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

            checker?.Check(node);
        }
    }

    /// <summary>スカラー・ベクトル・行列の型。</summary>
    /// <param name="Name">型の名前。</param>
    /// <param name="Base">要素の型。</param>
    /// <param name="Kind">形。</param>
    /// <param name="VectorSize">ベクトルの成分の数。スカラーは 1、行列は 0 (glslang の getVectorSize)。</param>
    /// <param name="Columns">行列の列の数。</param>
    /// <param name="Rows">行列の行の数。</param>
    private sealed record Operand(string Name, string Base, ShaderNumericKind Kind, int VectorSize, int Columns, int Rows)
    {
        public bool IsScalar => Kind == ShaderNumericKind.Scalar;

        public bool IsVector => Kind == ShaderNumericKind.Vector;

        public bool IsMatrix => Kind == ShaderNumericKind.Matrix;

        public bool IsInteger => GlslConversions.IntegerRank(Base) is not null;

        public bool SameType(Operand other) => Base == other.Base && Kind == other.Kind && VectorSize == other.VectorSize
                                                && Columns == other.Columns && Rows == other.Rows;

        public Operand WithBase(string baseName) => this with { Base = baseName };
    }

    /// <summary>1 つの木の演算子を検査する。</summary>
    private sealed class Checker(GlslRequirements requirement, GlslTypeSystem types, ExpressionTypeBinder binder)
    {
        public void Check(ShaderSyntaxNode node)
        {
            switch (node)
            {
                case BinaryExpressionSyntax binary when Classify(binary.OperatorToken.Kind) is { } op:
                    CheckBinary(binary.OperatorToken, op, assign: false, binary.Left, binary.Right);
                    break;

                case AssignmentExpressionSyntax assignment when ClassifyAssignment(assignment.OperatorToken.Kind) is { } op:
                    CheckBinary(assignment.OperatorToken, op, assign: true, assignment.Left, assignment.Right);
                    break;

                case PrefixUnaryExpressionSyntax prefix:
                    CheckUnary(prefix.OperatorToken, prefix.Operand);
                    break;

                case PostfixUnaryExpressionSyntax postfix:
                    CheckUnary(postfix.OperatorToken, postfix.Operand);
                    break;

                default:
                    break;
            }
        }

        private static Op? Classify(ShaderSyntaxKind kind) => kind switch
        {
            ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.GreaterThanToken
                or ShaderSyntaxKind.LessThanEqualsToken or ShaderSyntaxKind.GreaterThanEqualsToken => Op.Relational,
            ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken => Op.Equality,
            ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken or ShaderSyntaxKind.CaretCaretToken => Op.Logical,
            ShaderSyntaxKind.LessThanLessThanToken or ShaderSyntaxKind.GreaterThanGreaterThanToken => Op.Shift,
            ShaderSyntaxKind.PercentToken => Op.Mod,
            ShaderSyntaxKind.AmpersandToken or ShaderSyntaxKind.BarToken or ShaderSyntaxKind.CaretToken => Op.Bitwise,
            ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.SlashToken => Op.Add,
            ShaderSyntaxKind.AsteriskToken => Op.Mul,
            _ => null,
        };

        private static Op? ClassifyAssignment(ShaderSyntaxKind kind) => kind switch
        {
            ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken or ShaderSyntaxKind.SlashEqualsToken => Op.Add,
            ShaderSyntaxKind.AsteriskEqualsToken => Op.Mul,
            ShaderSyntaxKind.PercentEqualsToken => Op.Mod,
            ShaderSyntaxKind.AmpersandEqualsToken or ShaderSyntaxKind.BarEqualsToken or ShaderSyntaxKind.CaretEqualsToken => Op.Bitwise,
            ShaderSyntaxKind.LessThanLessThanEqualsToken or ShaderSyntaxKind.GreaterThanGreaterThanEqualsToken => Op.Shift,
            _ => null,
        };

        private void CheckBinary(ShaderSyntaxToken at, Op op, bool assign, ExpressionSyntax leftExpression, ExpressionSyntax rightExpression)
        {
            if (TypeOf(leftExpression) is not { } left || TypeOf(rightExpression) is not { } right)
            {
                return;
            }

            if (!Allowed(at, op, assign, left, right))
            {
                requirement.Report(
                    GlslRuleDescriptors.WrongOperandTypes,
                    at,
                    $"演算子 '{at.Text}' は、左が {left.Name}、右が {right.Name} の組に使えません。");
            }
        }

        private void CheckUnary(ShaderSyntaxToken at, ExpressionSyntax operandExpression)
        {
            if (at.Kind is not (ShaderSyntaxKind.ExclamationToken or ShaderSyntaxKind.TildeToken or ShaderSyntaxKind.MinusToken
                    or ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken)
                || TypeOf(operandExpression) is not { } operand)
            {
                return;
            }

            bool allowed = ArithmeticAllowed(at, operand) && at.Kind switch
            {
                ShaderSyntaxKind.ExclamationToken => operand.Base == "bool",
                ShaderSyntaxKind.TildeToken => operand.IsInteger,
                _ => operand.IsInteger || operand.Base is "float" or "float16_t" or "double",
            };

            if (!allowed)
            {
                requirement.Report(GlslRuleDescriptors.WrongOperandTypes, at, $"演算子 '{at.Text}' は {operand.Name} に使えません。");
            }
        }

        /// <summary>16 ビットと 8 ビットの型の演算に要る拡張 (glslang の handleBinaryMath の float16Arithmetic など)。</summary>
        private bool ArithmeticAllowed(ShaderSyntaxToken at, Operand operand) => operand.Base switch
        {
            "float16_t" => requirement.IsOn("GL_AMD_gpu_shader_half_float", at) || ExplicitArithmetic(at, "float16"),
            "int16_t" or "uint16_t" => requirement.IsOn("GL_AMD_gpu_shader_int16", at) || ExplicitArithmetic(at, "int16"),
            "int8_t" or "uint8_t" => ExplicitArithmetic(at, "int8"),
            "bfloat16_t" or "floate5m2_t" or "floate4m3_t" or "floate2m1_t" or "floate3m2_t" or "floate2m3_t" or "floatmxint8_t" => false,
            _ => true,
        };

        private bool ExplicitArithmetic(ShaderSyntaxToken at, string kind)
            => requirement.IsOn("GL_EXT_shader_explicit_arithmetic_types", at)
               || requirement.IsOn("GL_NV_gpu_shader5", at)
               || requirement.IsOn($"GL_EXT_shader_explicit_arithmetic_types_{kind}", at);

        /// <summary>演算子をその組に使えるか (glslang の addPairConversion と promoteBinary)。</summary>
        private bool Allowed(ShaderSyntaxToken at, Op op, bool assign, Operand left, Operand right)
        {
            if (!assign)
            {
                if (op == Op.Relational && (!left.IsScalar || !right.IsScalar))
                {
                    return false;
                }

                if (!ArithmeticAllowed(at, left) || !ArithmeticAllowed(at, right))
                {
                    return false;
                }
            }

            // 要素の型をそろえる。
            if (assign)
            {
                if (left.Base != right.Base && !(op == Op.Shift && left.IsInteger && right.IsInteger))
                {
                    if (!types.IsBaseConvertible(right.Base, left.Base))
                    {
                        return false;
                    }

                    right = right.WithBase(left.Base);
                }
            }
            else if (op == Op.Shift)
            {
                if (!left.IsInteger || !right.IsInteger)
                {
                    return false;
                }
            }
            else if (op != Op.Logical && left.Base != right.Base)
            {
                if (types.GetConversionDestination(left.Base, right.Base) is not { } destination)
                {
                    return false;
                }

                left = left.WithBase(destination);
                right = right.WithBase(destination);
            }

            // 辺ごとの検査。
            switch (op)
            {
                case Op.Relational when left.Base == "bool":
                    return false;

                case Op.Logical when left.Base != "bool" || !left.IsScalar:
                    return false;

                case Op.Shift or Op.Mod or Op.Bitwise when !left.IsInteger && !right.IsInteger || left.IsMatrix || right.IsMatrix:
                    return false;

                case Op.Add or Op.Mul when left.Base == "bool" || right.Base == "bool":
                    return false;

                default:
                    break;
            }

            // 左右を比べる。
            switch (op)
            {
                case Op.Relational or Op.Equality or Op.Logical:
                    return left.SameType(right);

                case Op.Mod or Op.Bitwise or Op.Add when left.SameType(right):
                    return true;

                case Op.Mod or Op.Bitwise or Op.Add or Op.Mul when left.Base != right.Base:
                    return false;

                default:
                    break;
            }

            if (left.IsScalar && right.IsScalar)
            {
                return true;
            }

            if (left.IsVector && right.IsVector && left.VectorSize != right.VectorSize && right.VectorSize > 1)
            {
                return false;
            }

            Operand result = left;

            switch (op)
            {
                case Op.Mul when !assign:
                    if (!left.IsMatrix && right.IsMatrix)
                    {
                        if (left.IsVector && left.VectorSize != right.Rows)
                        {
                            return false;
                        }
                    }
                    else if (left.IsMatrix && !right.IsMatrix)
                    {
                        if (right.IsVector && left.Columns != right.VectorSize)
                        {
                            return false;
                        }
                    }
                    else if (left.IsMatrix && right.IsMatrix && left.Columns != right.Rows)
                    {
                        return false;
                    }

                    return true;

                case Op.Mul:
                    if (!left.IsMatrix && right.IsMatrix)
                    {
                        return left.IsVector && left.VectorSize == right.Rows && left.VectorSize == right.Columns;
                    }

                    if (left.IsMatrix && !right.IsMatrix)
                    {
                        return !right.IsVector;
                    }

                    if (left.IsMatrix && right.IsMatrix)
                    {
                        return left.Columns == right.Columns && left.Columns == right.Rows;
                    }

                    return left.IsVector || !right.IsVector;

                case Op.Shift:
                    return !(right.IsVector && (!left.IsVector || right.VectorSize != left.VectorSize));

                default:
                    if ((left.IsMatrix && right.IsVector) || (left.IsVector && right.IsMatrix) || left.Base != right.Base)
                    {
                        return false;
                    }

                    if (left.IsMatrix && right.IsMatrix && (left.Columns != right.Columns || left.Rows != right.Rows))
                    {
                        return false;
                    }

                    if (left.IsVector && right.IsVector && left.VectorSize != right.VectorSize)
                    {
                        return false;
                    }

                    if (right.IsVector || right.IsMatrix)
                    {
                        result = right;
                    }

                    // 代入の演算子は、結果の型が左の型でなければならない。
                    return !assign || result.SameType(left);
            }
        }

        /// <summary>式の型。スカラー・ベクトル・行列でないか、分からないか、構成で変わるか、配列の値なら <see langword="null"/>。</summary>
        private Operand? TypeOf(ExpressionSyntax expression)
        {
            if (IsArrayValue(expression)
                || binder.GetTypesByConfiguration(expression) is not [{ Type: { } name }]
                || !types.TryDescribeNumeric(name, out ShaderNumericShape shape))
            {
                return null;
            }

            // GLSL の形は、行列の列の数を Rows、行の数を Columns に持つ。
            return shape.Kind switch
            {
                ShaderNumericKind.Scalar => new Operand(name, shape.BaseName, shape.Kind, 1, 0, 0),
                ShaderNumericKind.Vector => new Operand(name, shape.BaseName, shape.Kind, shape.Rows * shape.Columns, 0, 0),
                _ => new Operand(name, shape.BaseName, shape.Kind, 0, shape.Rows, shape.Columns),
            };
        }

        /// <summary>添字を付けない配列の変数か、配列のメンバーか。</summary>
        private bool IsArrayValue(ExpressionSyntax expression) => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => IsArrayValue(parenthesized.Expression),
            IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates.Any(c => c.Type.IsArray),
            MemberAccessExpressionSyntax member => binder.GetTypesByConfiguration(member.Target) is not [{ Type: { } target }]
                                                   || (binder.TryGetStructField(target, member.NameToken.Text, out VariableDeclaratorSyntax? field, out _) && field.IsArray),
            _ => false,
        };
    }
}
