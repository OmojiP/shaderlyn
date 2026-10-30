using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 配列の代入と初期化で、大きさが合っているかを検査する (CF0410)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>addConversion</c> が、配列どうしの型を完全に一致させることを移したもの。
/// </summary>
/// <remarks>
/// <para>
/// GLSL の配列は、大きさまで同じでなければ代入できない。大きさを書かない配列 (<c>float u[];</c>) へも、それからも代入できない。
/// 初期化では、宣言の外側の大きさを省けば値の大きさになる (<c>float a[] = float[](1.0, 2.0);</c>)。
/// </para>
/// <para>
/// 大きさが分かるものだけを見る。名前 (変数と仮引数)・配列の値の作成 (<c>float[3](…)</c>)・波括弧の初期化子 (外側の要素の数だけ) である。
/// 大きさが数で書かれていない次元 (定数の名前、特殊化定数) と、次元の数が違うものは見ない。
/// </para>
/// </remarks>
internal sealed class GlslArrayAssignmentAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>大きさを書かない次元。</summary>
    private const int Unsized = -1;

    /// <summary>名前から宣言の初期化子をたどる深さの上限。</summary>
    private const int MaxDepth = 4;

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.ValueNotConvertible];

    /// <summary>配列の型。</summary>
    /// <param name="Element">要素の型の名前。分からなければ <see langword="null"/>。</param>
    /// <param name="Dimensions">外側からの各次元の大きさ。<see cref="Unsized"/> は大きさを書かない次元、<see langword="null"/> は分からない次元。</param>
    /// <param name="IsList">波括弧の初期化子か。外側の次元だけが分かる。</param>
    private sealed record ArrayShape(string? Element, int?[] Dimensions, bool IsList = false)
    {
        public override string ToString()
            => IsList
                ? $"要素が {Dimensions[0]} 個の初期化子"
                : Element + string.Concat(Dimensions.Select(d => d switch { Unsized => "[]", null => "[…]", _ => $"[{d}]" }));
    }

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
            if (node is not (AssignmentExpressionSyntax { OperatorToken.Text: "=" } or VariableDeclaratorSyntax { Initializer: not null }))
            {
                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null)
            {
                continue;
            }

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            switch (node)
            {
                case AssignmentExpressionSyntax assignment
                    when ShapeOf(binder, assignment.Left, 0) is { IsList: false } target
                         && ShapeOf(binder, assignment.Right, 0) is { } value
                         && Mismatches(target, value, initializing: false):
                    Report(requirement, assignment.OperatorToken, target, value);
                    break;

                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration, Initializer: { } initializer } variable
                    when DeclaredShape(declaration, variable) is { } target
                         && ShapeOf(binder, initializer, 0) is { } value
                         && Mismatches(target, value, initializing: true):
                    Report(requirement, variable.EqualsToken ?? variable.NameToken, target, value);
                    break;

                default:
                    break;
            }
        }
    }

    private static void Report(GlslRequirements requirement, ShaderSyntaxToken at, ArrayShape target, ArrayShape value)
        => requirement.Report(HlslRuleDescriptors.ValueNotConvertible, at, target.ToString(), value.ToString(), "配列は大きさまで同じでなければ代入できません。");

    /// <summary>値の配列の型が、代入先の型と合わないか。分からない次元は比べない。</summary>
    private static bool Mismatches(ArrayShape target, ArrayShape value, bool initializing)
    {
        if (value.IsList)
        {
            return target.Dimensions[0] is > 0 and int size && size != value.Dimensions[0];
        }

        if (target.Dimensions.Length != value.Dimensions.Length)
        {
            return false;
        }

        for (int i = 0; i < target.Dimensions.Length; i++)
        {
            // 初期化では、外側の大きさを省けば値の大きさになる。
            if (initializing && i == 0 && target.Dimensions[0] == Unsized)
            {
                continue;
            }

            if (target.Dimensions[i] is { } expected && value.Dimensions[i] is { } actual && expected != actual)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>式の配列の型。配列でないか、分からなければ <see langword="null"/>。</summary>
    private static ArrayShape? ShapeOf(ExpressionTypeBinder binder, ExpressionSyntax expression, int depth)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return ShapeOf(binder, parenthesized.Expression, depth);

            case IdentifierExpressionSyntax identifier:
                NameResolution resolution = binder.ResolveName(identifier);

                if (resolution.Kind is not (DeclaredNameKind.Local or DeclaredNameKind.Global) || resolution.Candidates.IsEmpty)
                {
                    return null;
                }

                ArrayShape?[] shapes = [.. resolution.Candidates.Select(c => c.Declarator switch
                {
                    VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } variable => ResolvedShape(binder, declaration, variable, depth),
                    ParameterSyntax parameter => Shape(parameter.Type.Name, [.. parameter.ArrayRankTokens, .. parameter.Type.ArrayRanks]),
                    _ => null,
                })];

                return shapes.All(s => s is not null && s.Element == shapes[0]!.Element && s.Dimensions.SequenceEqual(shapes[0]!.Dimensions))
                    ? shapes[0]
                    : null;

            case InvocationExpressionSyntax { Target: ArrayTypeExpressionSyntax type } invocation
                when Shape(type.ElementType.Name, type.ArrayRanks) is { } constructed:
                int count = invocation.ArgumentExpressions.Count();

                if (constructed.Dimensions[0] == Unsized)
                {
                    constructed.Dimensions[0] = count;
                }

                return constructed.Dimensions.Skip(1).Any(d => d == Unsized) ? null : constructed;

            case InitializerListExpressionSyntax list:
                return new ArrayShape(null, [list.Elements.Count(e => e.Node is not null)], IsList: true);

            default:
                return null;
        }
    }

    /// <summary>名前の宣言の配列の型。外側の大きさを省いた初期化つきの宣言は、初期化子の大きさにする。</summary>
    private static ArrayShape? ResolvedShape(ExpressionTypeBinder binder, VariableDeclarationSyntax declaration, VariableDeclaratorSyntax variable, int depth)
    {
        if (DeclaredShape(declaration, variable) is not { } shape)
        {
            return null;
        }

        if (shape.Dimensions[0] == Unsized && variable.Initializer is { } initializer)
        {
            if (depth >= MaxDepth || ShapeOf(binder, initializer, depth + 1) is not { Dimensions: [{ } size, ..] })
            {
                return null;
            }

            shape.Dimensions[0] = size;
        }

        return shape;
    }

    /// <summary>宣言の配列の型。変数の名前の後ろの次元が外側、型の側 (<c>float[3] a</c>) の次元が内側になる。</summary>
    private static ArrayShape? DeclaredShape(VariableDeclarationSyntax declaration, VariableDeclaratorSyntax variable)
        => Shape(declaration.Type.Name, [.. variable.ArrayRankTokens, .. declaration.Type.ArrayRanks]);

    /// <summary>角括弧の並びから配列の型を作る。配列でなければ <see langword="null"/>。</summary>
    private static ArrayShape? Shape(string element, ImmutableArray<ShaderSyntaxToken> ranks)
    {
        List<int?> dimensions = [];

        for (int i = 0; i < ranks.Length; i++)
        {
            if (ranks[i].Kind != ShaderSyntaxKind.OpenBracketToken)
            {
                continue;
            }

            int close = i + 1;
            int depth = 0;

            while (close < ranks.Length && (ranks[close].Kind != ShaderSyntaxKind.CloseBracketToken || depth > 0))
            {
                depth += ranks[close].Kind switch
                {
                    ShaderSyntaxKind.OpenBracketToken => 1,
                    ShaderSyntaxKind.CloseBracketToken => -1,
                    _ => 0,
                };
                close++;
            }

            dimensions.Add(
                close == i + 1 ? Unsized
                : close == i + 2 && ShaderLiteral.TryGetInt64(ranks[i + 1], out long size) && size is > 0 and <= int.MaxValue ? (int)size
                : null);
            i = close;
        }

        return dimensions.Count == 0 ? null : new ArrayShape(element, [.. dimensions]);
    }
}
