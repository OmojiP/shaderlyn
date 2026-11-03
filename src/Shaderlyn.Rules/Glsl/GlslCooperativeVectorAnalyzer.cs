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
/// 協調ベクトル (<c>GL_NV_cooperative_vector</c> の <c>coopvecNV&lt;T, N&gt;</c>) の使い方を検査する (GL0306 / GL0302 / GL0307 / CF0312)。
/// glslang 16.6.0 の ParseHelper.cpp と ParseContextBase.cpp の協調ベクトルの検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// <c>coopVecMatMulNV</c> と <c>coopVecMatMulAddNV</c> の解釈・M・K・行列の配置・転置の実引数、<c>coopVecOuterProductAccumulateNV</c> の行列の配置と解釈の実引数は、
/// 定数の式でなければならない。<c>coopVecOuterProductAccumulateNV</c> の 2 つの協調ベクトルは、要素の型が同じでなければならない。
/// </para>
/// <para>
/// 協調ベクトルの定数の添字は要素の数より小さくなければならず、成分の選択 (<c>.x</c>) は書けない。
/// 定数でないと言い切れるのは、const の付いていない変数の名前だけ (特殊化の定数の式などは報告しない)。
/// </para>
/// </remarks>
internal sealed class GlslCooperativeVectorAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [GlslRuleDescriptors.VariableArgumentRequired, GlslRuleDescriptors.NoMatchingBuiltin, GlslRuleDescriptors.IndexOutOfRange, HlslRuleDescriptors.MemberNotFound];

    /// <summary>協調ベクトルの型。</summary>
    private sealed record CoopVector(string Element, string Count)
    {
        public override string ToString() => $"coopvecNV<{Element}, {Count}>";
    }

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl || !compilation.CodeTokens.Any(t => t.Text == "coopvecNV"))
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (InvocationExpressionSyntax or ElementAccessExpressionSyntax or MemberAccessExpressionSyntax))
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
                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } call
                    when callee.Name.StartsWith("coopVec", StringComparison.Ordinal) && compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty:
                    CheckCall(requirement, binder, callee, [.. call.ArgumentExpressions]);
                    break;

                case ElementAccessExpressionSyntax element when TypeOf(binder, element.Target) is { } vector
                                                              && long.TryParse(vector.Count, NumberStyles.None, CultureInfo.InvariantCulture, out long count)
                                                              && element.Index is LiteralExpressionSyntax { Token: { Kind: ShaderSyntaxKind.NumericLiteralToken } token }
                                                              && long.TryParse(token.Text.TrimEnd('u', 'U'), NumberStyles.None, CultureInfo.InvariantCulture, out long index)
                                                              && index >= count:
                    requirement.Report(GlslRuleDescriptors.IndexOutOfRange, element.OpenBracket, index.ToString(CultureInfo.InvariantCulture), $"{vector} の要素は 0 から {count - 1}");
                    break;

                case MemberAccessExpressionSyntax member when member.Parent is not InvocationExpressionSyntax { Target: var target } || target != member:
                    if (TypeOf(binder, member.Target) is { } swizzled)
                    {
                        requirement.Report(HlslRuleDescriptors.MemberNotFound, member.NameToken, swizzled.ToString(), $"成分 '{member.Name}' ", "協調ベクトルには成分の選択を書けません。");
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>協調ベクトルの組み込み関数の実引数。</summary>
    private static void CheckCall(GlslRequirements requirement, ExpressionTypeBinder binder, IdentifierExpressionSyntax callee, ExpressionSyntax[] arguments)
    {
        (int Index, string Name)[] constants = callee.Name switch
        {
            "coopVecMatMulNV" when arguments.Length == 11 => [(2, "inputInterpretation"), (5, "matrixInterpretation"), (6, "M"), (7, "K"), (8, "matrixLayout"), (9, "transpose")],
            "coopVecMatMulAddNV" when arguments.Length == 14
                => [(2, "inputInterpretation"), (5, "matrixInterpretation"), (8, "biasInterpretation"), (9, "M"), (10, "K"), (11, "matrixLayout"), (12, "transpose")],
            "coopVecOuterProductAccumulateNV" when arguments.Length == 7 => [(5, "matrixLayout"), (6, "matrixInterpretation")],
            _ => [],
        };

        foreach ((int index, string name) in constants)
        {
            if (IsVariable(binder, arguments[index]))
            {
                requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, callee.Identifier, callee.Name, index + 1, $"定数の式 ({name}) でなければなりません");
            }
        }

        if (callee.Name == "coopVecOuterProductAccumulateNV" && arguments.Length == 7 && TypeOf(binder, arguments[0]) is { } first)
        {
            if (TypeOf(binder, arguments[1]) is { } second)
            {
                if (Canonical(first.Element) != Canonical(second.Element))
                {
                    requirement.Report(GlslRuleDescriptors.NoMatchingBuiltin, callee.Identifier, callee.Name, $"要素の型の違う協調ベクトル ({first}, {second})");
                }
            }
            else if (binder.GetTypesByConfiguration(arguments[1]) is [{ Type: { } other }] && binder.TypeSystem.TryDescribeNumeric(other, out _))
            {
                requirement.Report(GlslRuleDescriptors.NoMatchingBuiltin, callee.Identifier, callee.Name, $"{first}, {other}");
            }
        }
    }

    /// <summary>const の付いていない変数の名前か。</summary>
    private static bool IsVariable(ExpressionTypeBinder binder, ExpressionSyntax expression)
        => expression is IdentifierExpressionSyntax identifier
           && !identifier.Name.StartsWith("gl_", StringComparison.Ordinal)
           && binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }]
           && !declaration.HasModifier("const");

    private static string Canonical(string type) => type switch
    {
        "float32_t" => "float",
        "float64_t" => "double",
        "int32_t" => "int",
        "uint32_t" => "uint",
        _ => type,
    };

    private static CoopVector? TypeOf(ExpressionTypeBinder binder, ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => TypeOf(binder, parenthesized.Expression),
        IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates is [{ Declarator: var declarator }] ? FromDeclarator(declarator) : null,
        _ => null,
    };

    private static CoopVector? FromDeclarator(ShaderSyntaxNode declarator) => declarator switch
    {
        VariableDeclaratorSyntax { IsArray: false, Parent: VariableDeclarationSyntax { Type: { Name: "coopvecNV", OpenAngle: not null } type } } => FromArguments(type.TemplateArguments),
        ParameterSyntax { IsArray: false, Type: { Name: "coopvecNV", OpenAngle: not null } type } => FromArguments(type.TemplateArguments),
        _ => null,
    };

    /// <summary>型の引数 (要素の型、要素の数) から作る。</summary>
    private static CoopVector? FromArguments(ImmutableArray<ShaderSyntaxToken> tokens)
        => tokens is [var element, { Kind: ShaderSyntaxKind.CommaToken }, .. var rest] && rest.Length > 0 && rest.All(t => t.Kind != ShaderSyntaxKind.CommaToken)
            ? new CoopVector(element.Text, string.Concat(rest.Select(t => t.Text)))
            : null;
}
