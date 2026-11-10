using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、形の合わない値どうしの組み込みの演算子を検査する (SG0301)。
/// </summary>
/// <remarks>
/// <para>
/// Slang の組み込みの二項演算子 (算術・比較・ビット・シフト・<c>&amp;&amp;</c> / <c>||</c>) と複合代入は、スカラーでない値どうしなら形 (ベクトルの長さ、行列の行と列) が同じでなければならない
/// (slangc 2026.19 の「no overload for '+' applicable to arguments of type (vector&lt;float,3&gt;, vector&lt;float,4&gt;)」)。
/// HLSL は短い方に切り詰めて通すが、Slang は通さない。ベクトルと行列の組み合わせも誤りである (行列の積は <c>mul</c> で書く)。
/// </para>
/// <para>
/// スカラーと 1 成分の値 (<c>float1</c>) は、どの形とも組める。型の分からない値、構成で型が変わる値、配列は見ない。
/// 利用者が演算子を定義したファイル (<c>operator+</c> など) では、その演算子を見ない。
/// <c>import glsl;</c> を書いたファイルの行列とベクトルの <c>*</c> は行列の積なので見ない。GLSL の書き方のファイル (#version のあるもの) も見ない。
/// </para>
/// </remarks>
internal sealed class SlangOperatorAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.WrongOperandTypes, SlangRuleDescriptors.ScalarConditionExpected];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(program.Tree.Text.ToString(), @"^\s*#\s*version\b", System.Text.RegularExpressions.RegexOptions.Multiline))
            {
                continue;
            }

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
            ShaderTypeSystem types = binder.TypeSystem;

            // 利用者が定義した演算子 (operator+ など)。その演算子の組み込みの決まりは見ない。
            HashSet<string> userOperators = [.. program.Tree.Root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>()
                .Where(f => f.Name.StartsWith("operator", StringComparison.Ordinal))
                .Select(f => f.Name["operator".Length..].Trim())];

            HashSet<string> structs = [.. program.Tree.Root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>()
                .Where(s => s.Keyword.Text is "struct" or "class" && s.NameToken is not null)
                .Select(s => s.NameToken!.Text)];

            foreach (ShaderSyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                // 条件 (if・while・for・do の条件) は、スカラーでなければならない (E30019)。?: の条件と ! の値は、ベクトルと行列なら成分ごとに働くが、構造体と資源の型は誤り (E39999)。
                (ShaderSyntaxToken at, ExpressionSyntax value)? condition = node switch
                {
                    IfStatementSyntax statement => (statement.IfKeyword, statement.Condition),
                    WhileStatementSyntax statement => (statement.WhileKeyword, statement.Condition),
                    DoWhileStatementSyntax statement => (statement.WhileKeyword, statement.Condition),
                    ForStatementSyntax { Condition: { } forCondition } statement => (statement.ForKeyword, forCondition),
                    ConditionalExpressionSyntax conditional => (conditional.QuestionToken, conditional.Condition),
                    PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.ExclamationToken } not => (not.OperatorToken, not.Operand),
                    _ => null,
                };

                if (condition is var (conditionAt, conditionValue)
                    && !userOperators.Contains("!")
                    && DescribeNonScalar(binder, types, structs, conditionValue, numericAllowed: conditionAt.Text is "!" or "?") is { } what
                    && compilation.IsReportable(conditionAt)
                    && conditionAt.GetLocation() is { } conditionLocation)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        SlangRuleDescriptors.ScalarConditionExpected,
                        conditionLocation,
                        $"{(conditionAt.Text == "!" ? "演算子 '!' の値" : conditionAt.Text == "?" ? "?: の条件" : $"{conditionAt.Text} の条件")}はスカラーでなければなりません (今は{what})。"));
                }

                (ShaderSyntaxToken op, ExpressionSyntax left, ExpressionSyntax right)? operation = node switch
                {
                    BinaryExpressionSyntax binary when IsChecked(binary.OperatorToken.Kind) => (binary.OperatorToken, binary.Left, binary.Right),
                    AssignmentExpressionSyntax assignment when IsCheckedAssignment(assignment.OperatorToken.Kind) => (assignment.OperatorToken, assignment.Left, assignment.Right),
                    _ => null,
                };

                if (operation is not var (token, leftExpression, rightExpression)
                    || userOperators.Contains(token.Text)
                    || userOperators.Contains(token.Text.TrimEnd('='))
                    || ShapeOf(binder, types, leftExpression) is not { } left
                    || ShapeOf(binder, types, rightExpression) is not { } right
                    || IsScalarLike(left)
                    || IsScalarLike(right)
                    || (left.Kind == right.Kind && left.Rows == right.Rows && left.Columns == right.Columns)
                    || (types.MultipliesMatricesAlgebraically && token.Kind is ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.AsteriskEqualsToken
                        && (left.Kind == ShaderNumericKind.Matrix || right.Kind == ShaderNumericKind.Matrix))
                    || !compilation.IsReportable(token)
                    || token.GetLocation() is not { } location)
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    SlangRuleDescriptors.WrongOperandTypes,
                    location,
                    $"演算子 '{token.Text}' は {Describe(left)} と {Describe(right)} に使えません。形 (成分の数) をそろえてください{(left.Kind != right.Kind ? " (行列の積は mul で書きます)" : "")}。"));
            }
        }
    }

    private static bool IsChecked(ShaderSyntaxKind kind) => kind is ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.AsteriskToken
        or ShaderSyntaxKind.SlashToken or ShaderSyntaxKind.PercentToken or ShaderSyntaxKind.AmpersandToken or ShaderSyntaxKind.BarToken or ShaderSyntaxKind.CaretToken
        or ShaderSyntaxKind.LessThanLessThanToken or ShaderSyntaxKind.GreaterThanGreaterThanToken
        or ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.GreaterThanToken or ShaderSyntaxKind.LessThanEqualsToken or ShaderSyntaxKind.GreaterThanEqualsToken
        or ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken
        or ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken;

    private static bool IsCheckedAssignment(ShaderSyntaxKind kind) => kind is ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken
        or ShaderSyntaxKind.AsteriskEqualsToken or ShaderSyntaxKind.SlashEqualsToken or ShaderSyntaxKind.PercentEqualsToken
        or ShaderSyntaxKind.AmpersandEqualsToken or ShaderSyntaxKind.BarEqualsToken or ShaderSyntaxKind.CaretEqualsToken
        or ShaderSyntaxKind.LessThanLessThanEqualsToken or ShaderSyntaxKind.GreaterThanGreaterThanEqualsToken;

    /// <summary>スカラーか 1 成分の値か。どの形とも組める。</summary>
    private static bool IsScalarLike(ShaderNumericShape shape) => shape.Kind == ShaderNumericKind.Scalar || shape.Rows * shape.Columns == 1;

    /// <summary>式の数の形。型が分からないか、構成で変わるか、配列なら <see langword="null"/>。</summary>
    private static ShaderNumericShape? ShapeOf(ExpressionTypeBinder binder, ShaderTypeSystem types, ExpressionSyntax expression)
    {
        if (expression is IdentifierExpressionSyntax identifier
            && binder.ResolveName(identifier).Candidates is { IsEmpty: false } candidates
            && candidates.Any(c => c.Type.IsArray))
        {
            return null;
        }

        return binder.GetTypesByConfiguration(expression) is [{ Type: { } name }] && types.TryDescribeNumeric(name, out ShaderNumericShape shape)
            ? shape
            : null;
    }

    /// <summary>条件にできない値なら、その説明。スカラーか、分からなければ <see langword="null"/>。</summary>
    private static string? DescribeNonScalar(ExpressionTypeBinder binder, ShaderTypeSystem types, HashSet<string> structs, ExpressionSyntax expression, bool numericAllowed)
    {
        if (ShapeOf(binder, types, expression) is { } shape)
        {
            return IsScalarLike(shape) || numericAllowed ? null : $" {Describe(shape)}";
        }

        if (expression is IdentifierExpressionSyntax identifier
            && binder.ResolveName(identifier).Candidates is { IsEmpty: false } candidates
            && candidates.Any(c => c.Type.IsArray))
        {
            return null;
        }

        if (binder.GetTypesByConfiguration(expression) is not [{ Type: { } name }])
        {
            return null;
        }

        if (structs.Contains(name))
        {
            return $"構造体 {name}";
        }

        return types.Classify(name) is ShaderTypeClass.Texture2D or ShaderTypeClass.Texture3D or ShaderTypeClass.TextureCube or ShaderTypeClass.Texture2DArray or ShaderTypeClass.TextureCubeArray or ShaderTypeClass.Buffer or ShaderTypeClass.Sampler
            ? $"資源の型 {name}"
            : null;
    }

    private static string Describe(ShaderNumericShape shape) => shape.Kind switch
    {
        ShaderNumericKind.Matrix => $"{shape.BaseName}{shape.Rows}x{shape.Columns}",
        _ => $"{shape.BaseName}{shape.Columns}",
    };
}
