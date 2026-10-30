using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 定数の添字を、配列の大きさ・ベクトルの成分の数・行列の列の数で検査する (GL0307)。
/// glslang 16.6.0 の ParseContextBase.cpp の <c>checkIndex</c> を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 負の添字は、何を添字で引いても報告する。大きすぎる添字は、添字で引くものの型を言い切れるときだけ報告する。
/// 宣言の名前か、それを添字で引いた式 (<c>a[1][2]</c>、<c>m[1][3]</c>) で、配列の大きさが数で書かれているもの、
/// またはベクトルか行列の変数である。構造体のメンバーと、大きさを書かない配列は見ない。
/// ただし外側の大きさを省いて初期化した配列 (<c>const int a[] = int[](3, 2);</c>) は、初期化子の大きさにする。
/// </para>
/// <para>
/// 組み込みの変数は、大きさが決まった配列だけを見る。<c>gl_FragData</c> (gl_MaxDrawBuffers)、<c>gl_BoundingBox</c> 系 (2)、
/// <c>gl_SampleMask</c> / <c>gl_SampleMaskIn</c> (gl_MaxSamples から 1)、テッセレーションの制御シェーダーの <c>gl_out</c> (<c>layout(vertices = …) out;</c> の値)。
/// 大きさは glslang の既定の資源の値である。
/// </para>
/// <para>
/// 添字は glslang の front-end の定数 (リテラルと <c>const</c> の変数) だけを見る。特殊化の定数 (<c>constant_id</c>) を使う添字は見ない。
/// </para>
/// </remarks>
internal sealed class GlslIndexRangeAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>大きさが決まった組み込みの配列 (glslang の既定の資源の値)。</summary>
    private static readonly Dictionary<string, (int Length, string Element)> BuiltinArrays = new(StringComparer.Ordinal)
    {
        ["gl_FragData"] = (32, "vec4"),
        ["gl_BoundingBox"] = (2, "vec4"),
        ["gl_BoundingBoxEXT"] = (2, "vec4"),
        ["gl_BoundingBoxOES"] = (2, "vec4"),
        ["gl_SampleMask"] = (1, "int"),
        ["gl_SampleMaskIn"] = (1, "int"),
    };

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.IndexOutOfRange];

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
            if (node is not ElementAccessExpressionSyntax element)
            {
                continue;
            }

            if (!checkers.TryGetValue(program, out Checker? checker))
            {
                checker = checkers[program] =
                    GlslRequirements.Create(context, compilation, program, reported) is { } requirement
                    && compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types
                        ? new Checker(requirement, types, compilation.GetExpressionTypeBinder(program), OutputVertices(program, requirement))
                        : null;
            }

            checker?.Check(element, element.Index);
        }
    }

    /// <summary>テッセレーションの制御シェーダーの <c>layout(vertices = …) out;</c> の値。</summary>
    private static int? OutputVertices(AnalyzedProgram program, GlslRequirements requirement)
    {
        if (requirement.Stage != GlslShaderStage.TessellationControl)
        {
            return null;
        }

        foreach (IncompleteDeclarationSyntax declaration in program.Tree.Root.Declarations.OfType<IncompleteDeclarationSyntax>())
        {
            foreach (GlslLayoutEntry entry in GlslLayoutQualifier.Read(declaration.Tokens))
            {
                if (entry.Name.Text == "vertices"
                    && entry.Value is [var literal]
                    && GlslConstantEvaluator.EvaluateLiteral(literal) is { Kind: GlslConstantKind.Int or GlslConstantKind.Uint } value
                    && value.Integer > 0)
                {
                    return (int)value.Integer;
                }
            }
        }

        return null;
    }

    /// <summary>括弧を外す。</summary>
    private static ExpressionSyntax Strip(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    /// <summary>添字で引くものの形。配列の各次元の大きさ (分からなければ <see langword="null"/>) と、要素の型。</summary>
    private readonly record struct IndexedShape(ImmutableArray<int?> Dimensions, string ElementType);

    /// <summary>1 つの木の添字を検査する。</summary>
    private sealed class Checker(GlslRequirements requirement, GlslTypeSystem types, ExpressionTypeBinder binder, int? outputVertices)
    {
        private readonly GlslConstantEvaluator _evaluator = new(binder);

        public void Check(ElementAccessExpressionSyntax element, ExpressionSyntax index)
        {
            if (UsesSpecializationConstant(index)
                || _evaluator.Evaluate(index) is not { Kind: GlslConstantKind.Int or GlslConstantKind.Uint } constant)
            {
                return;
            }

            long value = constant.Integer;
            string text = value.ToString(CultureInfo.InvariantCulture);

            if (value < 0)
            {
                requirement.Report(GlslRuleDescriptors.IndexOutOfRange, element.OpenBracket, text, "0 以上でなければなりません");
                return;
            }

            if (ShapeOf(element.Target) is not { } shape)
            {
                return;
            }

            if (!shape.Dimensions.IsEmpty)
            {
                if (shape.Dimensions[0] is { } length && value >= length)
                {
                    requirement.Report(GlslRuleDescriptors.IndexOutOfRange, element.OpenBracket, text, $"配列の大きさは {length} です");
                }

                return;
            }

            if (!types.TryDescribeNumeric(shape.ElementType, out ShaderNumericShape numeric))
            {
                return;
            }

            // GLSL の行列は、Rows に列の数を持つ。
            if (numeric.Kind == ShaderNumericKind.Vector && value >= numeric.Columns)
            {
                requirement.Report(GlslRuleDescriptors.IndexOutOfRange, element.OpenBracket, text, $"{shape.ElementType} の成分は {numeric.Columns} つです");
            }
            else if (numeric.Kind == ShaderNumericKind.Matrix && value >= numeric.Rows)
            {
                requirement.Report(GlslRuleDescriptors.IndexOutOfRange, element.OpenBracket, text, $"{shape.ElementType} の列は {numeric.Rows} つです");
            }
        }

        /// <summary>添字で引くものの形。宣言の名前と、それを添字で引いた式だけを求める。</summary>
        private IndexedShape? ShapeOf(ExpressionSyntax expression)
        {
            switch (Strip(expression))
            {
                case IdentifierExpressionSyntax identifier when identifier.Name.StartsWith("gl_", StringComparison.Ordinal):
                    if (binder.ResolveName(identifier).Kind != DeclaredNameKind.NotFound)
                    {
                        return null;
                    }

                    if (identifier.Name == "gl_out" && outputVertices is { } vertices)
                    {
                        return new IndexedShape([vertices], "gl_PerVertex");
                    }

                    return BuiltinArrays.TryGetValue(identifier.Name, out (int Length, string Element) builtin)
                        ? new IndexedShape([builtin.Length], builtin.Element)
                        : null;

                case IdentifierExpressionSyntax identifier:
                    if (binder.ResolveName(identifier) is not { Kind: DeclaredNameKind.Local or DeclaredNameKind.Global, Candidates: [{ Declarator: VariableDeclaratorSyntax declarator }] }
                        || declarator.Parent is not VariableDeclarationSyntax declaration)
                    {
                        return null;
                    }

                    ImmutableArray<ShaderSyntaxToken> ranks = [.. declarator.ArrayRankTokens, .. declaration.Type.ArrayRanks];
                    ImmutableArray<int?> dimensions = ReadDimensions(ranks);

                    // 外側の大きさを省いて初期化した配列は、初期化子の大きさになる。
                    if (ranks is [{ Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.CloseBracketToken }, ..]
                        && InitializerLength(declarator.Initializer) is { } length)
                    {
                        dimensions = dimensions.SetItem(0, length);
                    }

                    return new IndexedShape(dimensions, declaration.Type.Name);

                case ElementAccessExpressionSyntax element:
                    if (ShapeOf(element.Target) is not { } outer)
                    {
                        return null;
                    }

                    if (!outer.Dimensions.IsEmpty)
                    {
                        return outer with { Dimensions = outer.Dimensions.RemoveAt(0) };
                    }

                    if (!types.TryDescribeNumeric(outer.ElementType, out ShaderNumericShape numeric))
                    {
                        return null;
                    }

                    return numeric.Kind switch
                    {
                        ShaderNumericKind.Vector => types.Compose(numeric with { Kind = ShaderNumericKind.Scalar, Rows = 1, Columns = 1 }) is { } scalar
                            ? new IndexedShape([], scalar)
                            : null,
                        ShaderNumericKind.Matrix => types.Compose(numeric with { Kind = ShaderNumericKind.Vector, Rows = 1 }) is { } column
                            ? new IndexedShape([], column)
                            : null,
                        _ => null,
                    };

                default:
                    return null;
            }
        }

        /// <summary>初期化子の外側の要素の数。配列の値の作成 (<c>int[](3, 2)</c>) と波括弧の初期化子だけを見る。</summary>
        private static int? InitializerLength(ExpressionSyntax? initializer) => initializer switch
        {
            InvocationExpressionSyntax { Target: ArrayTypeExpressionSyntax { ArrayRanks: [{ Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.CloseBracketToken }, ..] } } invocation
                => invocation.ArgumentExpressions.Count(),
            InitializerListExpressionSyntax list => list.Elements.Count(e => e.Node is not null),
            _ => null,
        };

        /// <summary>配列の次元の大きさを読む。<c>[</c> 数 <c>]</c> でない次元は分からない (<see langword="null"/>)。</summary>
        private static ImmutableArray<int?> ReadDimensions(ImmutableArray<ShaderSyntaxToken> ranks)
        {
            ImmutableArray<int?>.Builder dimensions = ImmutableArray.CreateBuilder<int?>();
            int start = -1;

            for (int i = 0; i < ranks.Length; i++)
            {
                switch (ranks[i].Kind)
                {
                    case ShaderSyntaxKind.OpenBracketToken:
                        start = i;
                        break;

                    case ShaderSyntaxKind.CloseBracketToken:
                        dimensions.Add(i == start + 2 && GlslConstantEvaluator.EvaluateLiteral(ranks[start + 1]) is { Kind: GlslConstantKind.Int or GlslConstantKind.Uint } size
                            ? (int)size.Integer
                            : null);
                        break;

                    default:
                        break;
                }
            }

            return dimensions.ToImmutable();
        }

        /// <summary>添字の式が特殊化の定数 (<c>layout(constant_id = …) const</c>) を使うか。front-end の定数ではない。</summary>
        private bool UsesSpecializationConstant(ExpressionSyntax index)
            => index.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>().Any(identifier =>
                binder.ResolveName(identifier).Candidates.Any(c =>
                    c.Declarator is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration }
                    && declaration.ModifierTokens.Any(t => t.Text is "constant_id" or "local_size_x_id" or "local_size_y_id" or "local_size_z_id")));
    }
}
