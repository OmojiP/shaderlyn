using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 式の演算子とリテラルを、使える版と拡張で検査する (GL0001)。
/// </summary>
/// <remarks>
/// glslang.y の式の規則の動作を移したもの。
/// <list type="bullet">
///   <item><description>整数の演算子 (<c>%</c>、<c>&lt;&lt;</c>、<c>&amp;</c>、<c>~</c>、<c>%=</c> など) と <c>uint</c> のリテラルは、デスクトップの 1.30・ES の 3.00 から (fullIntegerCheck)</description></item>
///   <item><description>配列 (と配列を含む構造体) の比較と代入は、デスクトップの 1.20・ES の 3.00 から (arrayObjectCheck)</description></item>
///   <item><description>64 ビット・16 ビットの整数、double、half のリテラルに要る拡張と版</description></item>
/// </list>
/// </remarks>
internal sealed class GlslExpressionRequirementAnalyzer : SemanticRuleAnalyzer
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

        // 行の継続 (行末の \)。プリプロセッサの指令の行も含めて、書かれたとおりのトークンで探す (glslang の lineContinuationCheck)。
        if (compilation.Programs.FirstOrDefault() is { } first
            && GlslRequirements.Create(context, compilation, first, reported) is { } fileRequirement)
        {
            foreach (ShaderSyntaxToken token in compilation.CodeTokens)
            {
                if (token.TrailingTrivia.Concat(token.LeadingTrivia).Any(t => t.Kind == ShaderSyntaxKind.LineContinuationTrivia))
                {
                    fileRequirement.ProfileRequires(token, GlslProfileMask.Es, 300, [], "行の継続 (行末の \\)");
                    fileRequirement.ProfileRequires(token, GlslProfileMask.NotEs, 420, ["GL_ARB_shading_language_420pack"], "行の継続 (行末の \\)");
                }
            }
        }

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null)
            {
                continue;
            }

            switch (node)
            {
                case LiteralExpressionSyntax literal:
                    AnalyzeLiteral(requirement, literal.Token);
                    break;

                case PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.TildeToken } unary:
                    requirement.FullIntegerCheck(unary.OperatorToken, "bitwise not");
                    break;

                case BinaryExpressionSyntax binary when IntegerOperation(binary.OperatorToken.Kind) is { } feature:
                    requirement.FullIntegerCheck(binary.OperatorToken, feature);
                    break;

                case BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken } comparison
                    when ContainsArray(compilation, program, comparison.Left):
                    requirement.ArrayObjectCheck(comparison.OperatorToken, "array comparison");
                    break;

                case AssignmentExpressionSyntax assignment when IntegerOperation(assignment.OperatorToken.Kind) is { } feature:
                    requirement.FullIntegerCheck(assignment.OperatorToken, feature);
                    break;

                case AssignmentExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsToken } assignment
                    when ContainsArray(compilation, program, assignment.Left):
                    requirement.ArrayObjectCheck(assignment.OperatorToken, "array assignment");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>整数にしか使えない演算子の、glslang の機能の名前。</summary>
    private static string? IntegerOperation(ShaderSyntaxKind kind) => kind switch
    {
        ShaderSyntaxKind.PercentToken => "%",
        ShaderSyntaxKind.LessThanLessThanToken => "bit shift left",
        ShaderSyntaxKind.GreaterThanGreaterThanToken => "bit shift right",
        ShaderSyntaxKind.AmpersandToken => "bitwise and",
        ShaderSyntaxKind.CaretToken => "bitwise exclusive or",
        ShaderSyntaxKind.BarToken => "bitwise inclusive or",
        ShaderSyntaxKind.PercentEqualsToken => "%=",
        ShaderSyntaxKind.LessThanLessThanEqualsToken => "bit-shift left assign",
        ShaderSyntaxKind.GreaterThanGreaterThanEqualsToken => "bit-shift right assign",
        ShaderSyntaxKind.AmpersandEqualsToken => "bitwise-and assign",
        ShaderSyntaxKind.CaretEqualsToken => "bitwise-xor assign",
        ShaderSyntaxKind.BarEqualsToken => "bitwise-or assign",
        _ => null,
    };

    /// <summary>リテラルの接尾辞に要る版と拡張 (glslang の primary_expression の規則)。</summary>
    private static void AnalyzeLiteral(GlslRequirements requirement, ShaderSyntaxToken token)
    {
        if (token.Kind != ShaderSyntaxKind.NumericLiteralToken)
        {
            return;
        }

        bool floating = ShaderLiteral.GetKind(token) == ShaderLiteralKind.Floating;

        switch (ShaderLiteral.GetSuffix(token).ToLowerInvariant())
        {
            case "u" when !floating:
                requirement.FullIntegerCheck(token, "unsigned literal");
                break;

            case "l" when !floating:
                requirement.Int64Check(token, "64-bit integer literal");
                break;

            case "ul" or "uls" when !floating:
                requirement.Int64Check(token, "64-bit unsigned integer literal");
                break;

            case "s" when !floating:
                requirement.ExplicitInt16Check(token, "16-bit integer literal");
                break;

            case "us" when !floating:
                requirement.ExplicitInt16Check(token, "16-bit unsigned integer literal");
                break;

            case "lf":
                requirement.RequireProfile(token, GlslProfileMask.Core | GlslProfileMask.Compatibility, "'double literal'");
                requirement.DoubleCheck(token, "double literal");
                break;

            case "hf":
                requirement.Float16Check(token, "half float literal");
                break;

            default:
                break;
        }
    }

    /// <summary>式の値が配列か、配列を含む構造体か (glslang の containsArray)。</summary>
    private static bool ContainsArray(ShaderCompilation compilation, AnalyzedProgram program, ExpressionSyntax expression)
    {
        ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

        if (expression is IdentifierExpressionSyntax identifier && binder.ResolveName(identifier).Candidates.Any(c => c.Type.IsArray))
        {
            return true;
        }

        return binder.GetEvaluatorFor(expression).Evaluate(expression) is { } type && StructContainsArray(program, type, depth: 0);
    }

    /// <summary>構造体が配列のメンバーを持つか。入れ子の構造体も見る。</summary>
    private static bool StructContainsArray(AnalyzedProgram program, string type, int depth)
    {
        if (depth > 8 || program.Structs.FirstOrDefault(s => s.Name == type) is not { } structure)
        {
            return false;
        }

        foreach (VariableDeclarationSyntax field in structure.Fields)
        {
            if (field.Variables.Any(v => v.IsArray) || StructContainsArray(program, field.Type.Name, depth + 1))
            {
                return true;
            }
        }

        return false;
    }
}
