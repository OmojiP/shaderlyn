using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>
/// WGSL の定数式と、値の範囲を検査する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>WG0315: 型の範囲を超える数のリテラル (<c>0x80000000i</c>) と、負のシフトの量</item>
/// <item>WG0336: 左の型のビットの幅以上のシフトの量 (<c>x &lt;&lt; 32</c>) と、定数式の演算のあふれ (<c>1i &lt;&lt; 31</c>)</item>
/// <item>WG0308: <c>array&lt;T, N&gt;</c> の N が 0 以下か、整数でない</item>
/// <item>WG0307: 範囲の外か負の定数の添字、整数でない添字</item>
/// <item>WG0317: const の初期値と const_assert の条件の、実行時の値</item>
/// <item>WG0311 / WG0337: const_assert の条件が bool でないか、偽。if・while・for・break if・select の条件が bool でない</item>
/// <item>WG0303: subgroupBroadcast と quadBroadcast の呼び出しの番号が定数式でない</item>
/// </list>
/// naga 30 はどれもコンパイルの誤りにする。
/// </remarks>
internal sealed class WgslConstantAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesWgsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        WgslRuleDescriptors.ValueOutOfRange,
        WgslRuleDescriptors.ArithmeticOverflow,
        WgslRuleDescriptors.InvalidArraySize,
        WgslRuleDescriptors.IndexOutOfRange,
        WgslRuleDescriptors.ConstantExpressionRequired,
        WgslRuleDescriptors.ConditionNotBool,
        WgslRuleDescriptors.ConstAssertFailed,
        WgslRuleDescriptors.ConstantArgumentRequired,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsWgsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Id)> reported = [];
        Dictionary<AnalyzedProgram, WgslConstants> constantsByProgram = [];
        Dictionary<AnalyzedProgram, WgslTypes> typesByProgram = [];

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxNode? node, ShaderSyntaxToken? token, string message)
        {
            Location? location = node?.GetLocation() ?? token?.GetLocation();

            if (location is not null
                && (node is null || compilation.IsReportable(node))
                && (token is null || compilation.IsReportable(token))
                && reported.Add((location.FilePath, location.Span.Start, descriptor.Id)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
            }
        }

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            if (!constantsByProgram.TryGetValue(program, out WgslConstants? constants))
            {
                constants = constantsByProgram[program] = new WgslConstants(binder);
                AnalyzeGlobalAssertions(program, constants, Report);
                WgslTypes types = typesByProgram[program] = new WgslTypes(program.Tree.Root);

                // 別名の型 (alias A = array<f32, 0>;) はトークンのまま木にある。
                foreach (TypedefDeclarationSyntax alias in program.Tree.Root.Declarations.OfType<TypedefDeclarationSyntax>())
                {
                    if (alias is { Keyword.Text: "alias", Tokens: [_, { Kind: ShaderSyntaxKind.EqualsToken }, .. var aliased] })
                    {
                        AnalyzeArraySize(constants, types.Parse([.. aliased]), Report);
                    }
                }
            }

            switch (node)
            {
                case LiteralExpressionSyntax literal:
                    AnalyzeLiteral(literal, Report);
                    break;

                case BinaryExpressionSyntax binary:
                    AnalyzeBinary(binder, constants, binary, Report);
                    break;

                case TypeSyntax type:
                    AnalyzeArraySize(constants, typesByProgram[program].Of(type), Report);
                    break;

                case ElementAccessExpressionSyntax element:
                    AnalyzeIndex(binder, constants, element, Report);
                    break;

                case VariableDeclarationSyntax declaration when WgslConstants.Keyword(declaration) == "const":
                    foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
                    {
                        if (declarator.Initializer is { } initializer && constants.FindRuntimeName(initializer) is { } kind)
                        {
                            Report(WgslRuleDescriptors.ConstantExpressionRequired, initializer, null,
                                $"const の '{declarator.Name}' の初期値には定数式が必要ですが、{kind} の値を使っています。");
                        }
                    }

                    break;

                case VariableDeclarationSyntax declaration when declaration.Type.Name is { } target && WgslConstants.RangeOf(target) is { } range:
                    // let x: u32 = -1; (型を書かない整数を、範囲に収まらない型に変換する)
                    foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
                    {
                        if (declarator.Initializer is { } initializer
                            && constants.Evaluate(initializer) is { Kind: WgslValueKind.Integer, Type: WgslTypeSystem.AbstractInt } value
                            && (value.Integer < range.Min || value.Integer > range.Max))
                        {
                            Report(WgslRuleDescriptors.ValueOutOfRange, initializer, null, $"{value} は {target} に収まりません。");
                        }
                    }

                    break;

                case InvocationExpressionSyntax { TargetName: "const_assert" } assertion when assertion.ArgumentExpressions.FirstOrDefault() is { } condition:
                    AnalyzeAssertion(constants, condition, constants.Evaluate(condition), Report, constants.FindRuntimeName(condition));
                    break;

                case InvocationExpressionSyntax { TargetName: "subgroupBroadcast" or "quadBroadcast" } broadcast
                    when broadcast.ArgumentExpressions.ElementAtOrDefault(1) is { } id && constants.FindRuntimeName(id) is { } kind:
                    Report(WgslRuleDescriptors.ConstantArgumentRequired, id, null,
                        $"{broadcast.TargetName} の 2 つ目の実引数 (値を受け取る呼び出しの番号) は定数式でなければなりませんが、{kind} の値を使っています。");
                    break;

                case InvocationExpressionSyntax { TargetName: "select" } select when select.ArgumentExpressions.ElementAtOrDefault(2) is { } selector:
                    if (TypeOf(binder, selector) is { } selectorType && !IsBool(binder, selectorType, allowVector: true))
                    {
                        Report(WgslRuleDescriptors.ConditionNotBool, selector, null, $"select の 3 つ目の実引数は bool か vecN<bool> でなければなりませんが、{selectorType} です。");
                    }

                    break;

                case IfStatementSyntax ifStatement:
                    AnalyzeCondition(binder, ifStatement.Condition, ifStatement.IfKeyword.Text, Report);
                    break;

                case WhileStatementSyntax { WhileKeyword.Text: "while" } whileStatement:
                    AnalyzeCondition(binder, whileStatement.Condition, "while", Report);
                    break;

                case ForStatementSyntax { Condition: { } forCondition }:
                    AnalyzeCondition(binder, forCondition, "for", Report);
                    break;
            }
        }

        foreach (WgslConstants constants in constantsByProgram.Values)
        {
            // naga は、const の初期値の中のあふれだけを誤りにする (関数の中の 31li - … + -0x7fffffffffffffffli は通す)。
            foreach ((ExpressionSyntax expression, string message) in constants.Overflows)
            {
                if (expression.Ancestors().OfType<VariableDeclarationSyntax>().FirstOrDefault() is { } declaration && WgslConstants.Keyword(declaration) == "const")
                {
                    Report(WgslRuleDescriptors.ArithmeticOverflow, expression, null, $"定数式の {message}。");
                }
            }
        }
    }

    /// <summary>関数の外の <c>const_assert</c> (トークンのまま木にある)。</summary>
    private static void AnalyzeGlobalAssertions(AnalyzedProgram program, WgslConstants constants, Action<DiagnosticDescriptor, ShaderSyntaxNode?, ShaderSyntaxToken?, string> report)
    {
        foreach (IncompleteDeclarationSyntax declaration in program.Tree.Root.Declarations.OfType<IncompleteDeclarationSyntax>())
        {
            if (declaration.Tokens is not [{ Text: "const_assert" }, .. var rest, { Kind: ShaderSyntaxKind.SemicolonToken }] || rest.Length == 0)
            {
                continue;
            }

            ImmutableArray<ShaderSyntaxToken> condition = [.. rest];
            WgslValue? value = constants.EvaluateTokens(condition);

            if (value is not { } known)
            {
                continue;
            }

            if (known.Kind != WgslValueKind.Bool)
            {
                report(WgslRuleDescriptors.ConditionNotBool, null, condition[0], $"const_assert の条件は bool でなければなりませんが、{known.Type} です。");
            }
            else if (!known.Bool)
            {
                report(WgslRuleDescriptors.ConstAssertFailed, null, condition[0], "const_assert の条件が false になります。");
            }
        }
    }

    private static void AnalyzeAssertion(
        WgslConstants constants,
        ExpressionSyntax condition,
        WgslValue? value,
        Action<DiagnosticDescriptor, ShaderSyntaxNode?, ShaderSyntaxToken?, string> report,
        string? runtime)
    {
        _ = constants;

        if (runtime is not null)
        {
            report(WgslRuleDescriptors.ConstantExpressionRequired, condition, null, $"const_assert の条件には定数式が必要ですが、{runtime} の値を使っています。");
            return;
        }

        if (value is not { } known)
        {
            return;
        }

        if (known.Kind != WgslValueKind.Bool)
        {
            report(WgslRuleDescriptors.ConditionNotBool, condition, null, $"const_assert の条件は bool でなければなりませんが、{known.Type} です。");
        }
        else if (!known.Bool)
        {
            report(WgslRuleDescriptors.ConstAssertFailed, condition, null, "const_assert の条件が false になります。");
        }
    }

    /// <summary>数のリテラルが型の範囲に収まるか。</summary>
    private static void AnalyzeLiteral(LiteralExpressionSyntax literal, Action<DiagnosticDescriptor, ShaderSyntaxNode?, ShaderSyntaxToken?, string> report)
    {
        if (WgslConstants.ParseLiteral(literal.Token) is { Kind: WgslValueKind.Integer } value
            && WgslConstants.RangeOf(value.Type) is { } range
            && (value.Integer < range.Min || value.Integer > range.Max))
        {
            report(WgslRuleDescriptors.ValueOutOfRange, literal, null,
                $"'{literal.Token.Text}' は {(value.Type == WgslTypeSystem.AbstractInt ? "64 ビットの整数" : value.Type)} に収まりません。");
        }
    }

    /// <summary>シフトの量。</summary>
    private static void AnalyzeBinary(
        ExpressionTypeBinder binder,
        WgslConstants constants,
        BinaryExpressionSyntax binary,
        Action<DiagnosticDescriptor, ShaderSyntaxNode?, ShaderSyntaxToken?, string> report)
    {
        // あふれは、定数式の外側の式から求める (内側の式は、そのときに一緒に求まる)。
        if (binary.Parent is not (BinaryExpressionSyntax or ParenthesizedExpressionSyntax or PrefixUnaryExpressionSyntax))
        {
            _ = constants.Evaluate(binary);
        }

        if (binary.OperatorToken.Kind is not (ShaderSyntaxKind.LessThanLessThanToken or ShaderSyntaxKind.GreaterThanGreaterThanToken)
            || constants.Evaluate(binary.Right) is not { Kind: WgslValueKind.Integer } amount)
        {
            return;
        }

        if (amount.Integer < 0)
        {
            report(WgslRuleDescriptors.ValueOutOfRange, binary.Right, null, $"シフトの量 {amount} は u32 に収まりません。");
            return;
        }

        if (TypeOf(binder, binary.Left) is { } left
            && binder.TypeSystem.TryDecomposeNumeric(left, out string element, out _)
            && WgslConstants.WidthOf(element) is { } width
            && element != WgslTypeSystem.AbstractInt
            && amount.Integer >= width)
        {
            report(WgslRuleDescriptors.ArithmeticOverflow, binary.Right, null, $"シフトの量 {amount} が {element} のビットの幅 ({width}) 以上です。");
        }
    }

    /// <summary>array&lt;T, N&gt; の N。</summary>
    private static void AnalyzeArraySize(WgslConstants constants, WgslTypeRef type, Action<DiagnosticDescriptor, ShaderSyntaxNode?, ShaderSyntaxToken?, string> report)
    {
        foreach (WgslTypeRef argument in type.Arguments.Where(a => a.Name.Length > 0))
        {
            AnalyzeArraySize(constants, argument, report);
        }

        if (type.Name is not ("array" or "binding_array") || type.Arguments is not [_, { Tokens: { Length: > 0 } size }, ..])
        {
            return;
        }

        if (constants.EvaluateTokens(size) is not { } value)
        {
            return;
        }

        if (value.Kind != WgslValueKind.Integer || value.Type is not ("i32" or "u32" or WgslTypeSystem.AbstractInt))
        {
            report(WgslRuleDescriptors.InvalidArraySize, null, size[0], $"配列の大きさは整数 (i32 か u32) の定数式でなければなりませんが、{value.Type} です。");
        }
        else if (value.Integer <= 0)
        {
            report(WgslRuleDescriptors.InvalidArraySize, null, size[0], $"配列の大きさは 1 以上でなければなりませんが、{value} です。");
        }
    }

    /// <summary>型引数の 2 つ目 (配列の大きさ) のトークン。</summary>
    internal static ImmutableArray<ShaderSyntaxToken> SizeTokens(ImmutableArray<ShaderSyntaxToken> arguments)
    {
        int depth = 0;

        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i].Kind)
            {
                case ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.OpenParenToken:
                    depth++;
                    break;
                case ShaderSyntaxKind.GreaterThanToken or ShaderSyntaxKind.CloseParenToken:
                    depth--;
                    break;
                case ShaderSyntaxKind.CommaToken when depth == 0:
                    int end = i + 1;
                    while (end < arguments.Length && !(arguments[end].Kind == ShaderSyntaxKind.CommaToken))
                    {
                        end++;
                    }

                    return [.. arguments.Skip(i + 1).Take(end - i - 1)];
            }
        }

        return [];
    }

    /// <summary>添字。</summary>
    private static void AnalyzeIndex(
        ExpressionTypeBinder binder,
        WgslConstants constants,
        ElementAccessExpressionSyntax element,
        Action<DiagnosticDescriptor, ShaderSyntaxNode?, ShaderSyntaxToken?, string> report)
    {
        if (TypeOf(binder, element.Index) is { } indexType
            && binder.TypeSystem.TryDescribeNumeric(indexType, out ShaderNumericShape indexShape)
            && (indexShape.Kind != ShaderNumericKind.Scalar || indexShape.BaseName is "f32" or "f16" or "f64" or WgslTypeSystem.AbstractFloat or "bool"))
        {
            report(WgslRuleDescriptors.IndexOutOfRange, element.Index, null, $"添字は整数 (i32 か u32) でなければなりませんが、{indexType} です。");
            return;
        }

        if (constants.Evaluate(element.Index) is not { Kind: WgslValueKind.Integer } index)
        {
            return;
        }

        if (index.Integer < 0)
        {
            report(WgslRuleDescriptors.IndexOutOfRange, element.Index, null, $"添字 {index} は負の数です。");
            return;
        }

        if (LengthOf(binder, constants, element.Target) is { } length && index.Integer >= length)
        {
            report(WgslRuleDescriptors.IndexOutOfRange, element.Index, null, $"添字 {index} が範囲の外です (大きさは {length})。");
        }
    }

    /// <summary>ベクトル・行列・大きさの決まった配列の、要素の数。</summary>
    private static int? LengthOf(ExpressionTypeBinder binder, WgslConstants constants, ExpressionSyntax target)
    {
        if (TypeOf(binder, target) is { } type && binder.TypeSystem.TryDescribeNumeric(type, out ShaderNumericShape shape))
        {
            return shape.Kind switch
            {
                ShaderNumericKind.Vector => shape.Columns,
                ShaderNumericKind.Matrix => shape.Rows,
                _ => null,
            };
        }

        // 配列の変数: 型か初期値に書いた大きさ。
        if (target is not IdentifierExpressionSyntax identifier
            || binder.ResolveName(identifier).Candidates is not [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } declarator }])
        {
            return null;
        }

        if (declaration.Type.NameToken.Text == "array")
        {
            return constants.EvaluateTokens(SizeTokens(declaration.Type.TemplateArguments)) is { Kind: WgslValueKind.Integer } size ? (int)size.Integer : null;
        }

        if (declarator.Initializer is InvocationExpressionSyntax { TargetName: "array" } array)
        {
            return array.TypeArgumentTokens.IsDefaultOrEmpty
                ? array.ArgumentExpressions.Count()
                : constants.EvaluateTokens(SizeTokens(array.TypeArgumentTokens)) is { Kind: WgslValueKind.Integer } size ? (int)size.Integer : null;
        }

        return null;
    }

    /// <summary>条件が bool か。</summary>
    private static void AnalyzeCondition(
        ExpressionTypeBinder binder,
        ExpressionSyntax condition,
        string keyword,
        Action<DiagnosticDescriptor, ShaderSyntaxNode?, ShaderSyntaxToken?, string> report)
    {
        if (TypeOf(binder, condition) is { } type && !IsBool(binder, type, allowVector: false))
        {
            string what = keyword == "break" ? "break if" : keyword;
            report(WgslRuleDescriptors.ConditionNotBool, condition, null, $"{what} の条件は bool でなければなりませんが、{type} です。");
        }
    }

    private static bool IsBool(ExpressionTypeBinder binder, string type, bool allowVector)
        => binder.TypeSystem.TryDecomposeNumeric(type, out string element, out int components)
            ? element == "bool" && (components == 1 || allowVector)
            : true;

    private static string? TypeOf(ExpressionTypeBinder binder, ExpressionSyntax expression)
        => binder.GetEvaluatorFor(expression).Evaluate(expression);
}
