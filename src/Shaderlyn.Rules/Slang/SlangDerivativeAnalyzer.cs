using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の自動微分の書き方を検査する。微分する関数のループ (SG0350)、微分の関数の名前 (CF0310)、微分の属性の置き場所 (SG0102)、微分のグループのスレッドの数 (SG0304)。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>SG0350: <c>[Differentiable]</c>・<c>[ForwardDifferentiable]</c>・<c>[BackwardDifferentiable]</c> の関数の中の、繰り返しの回数を決められないループ (E30510)。
/// <c>[MaxIters(n)]</c> か <c>[ForceUnroll]</c> を付けていない <c>while</c> と <c>do</c>、変数を定数と比較して定数ずつ増減する形でない <c>for</c>、本体でその変数を書き換える <c>for</c></item>
/// <item>CF0310: <c>[BackwardDerivative(f)]</c> などの微分の属性に書いた、どこにも宣言されていない関数の名前 (E31148)</item>
/// <item>SG0102: インターフェイスの要件でない関数の <c>[MaybeDifferentiable]</c> (E30314) と、インターフェイスの要件の <c>[TreatAsDifferentiable]</c> (E30313)</item>
/// <item>SG0301: 微分する関数の中の、ベクトルか行列を条件にした <c>?:</c> (E30055)</item>
/// <item>SG0407: <c>[CudaKernel]</c> の戻り値 (E31213) と、微分する <c>[CudaKernel]</c> の微分する仮引数 (E31214)</item>
/// <item>SG0304: <c>[DerivativeGroupLinear]</c> の、スレッドの数の積が 4 の倍数でない <c>[numthreads]</c> (E31211) と、<c>[DerivativeGroupQuad]</c> の、x と y が 2 の倍数でない <c>[numthreads]</c> (E31210)</item>
/// </list>
/// </remarks>
internal sealed class SlangDerivativeAnalyzer : SemanticRuleAnalyzer
{
    private static readonly string[] DerivativeAttributes =
    [
        "ForwardDerivative", "BackwardDerivative", "PrimalSubstitute", "ForwardDerivativeOf", "BackwardDerivativeOf", "PrimalSubstituteOf",
    ];

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        SlangRuleDescriptors.LoopNeedsIterationCount,
        SlangRuleDescriptors.ModifierNotAllowed,
        SlangRuleDescriptors.InvalidAttributeValue,
        HlslRuleDescriptors.UndeclaredIdentifier,
        SlangRuleDescriptors.WrongOperandTypes,
        SlangRuleDescriptors.InvalidDeclarationType,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            CompilationUnitSyntax root = program.Tree.Root;
            SlangConstants constants = SlangConstants.Of(root);

            // 継承の並びに IDifferentiable を書いた構造体 (1 度だけ宣言したもの)。
            HashSet<string> differentiableStructs = [.. root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>()
                .Where(s => s.Keyword.Text == "struct" && s.NameToken is not null && s.BaseType?.NameToken.Text == "IDifferentiable")
                .GroupBy(s => s.Name).Where(g => g.Count() == 1).Select(g => g.Key)];

            void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken at, params object?[] arguments)
            {
                if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, arguments));
                }
            }

            foreach (FunctionDeclarationSyntax function in root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>())
            {
                bool inInterface = function.Parent is StructDeclarationSyntax { Keyword.Text: "interface" };

                foreach (AttributeSyntax attribute in function.Attributes)
                {
                    switch (attribute.Name)
                    {
                        case "MaybeDifferentiable" when !inInterface:
                            Report(SlangRuleDescriptors.ModifierNotAllowed, attribute.NameToken, "[MaybeDifferentiable] は、インターフェイスの要件にだけ書けます。");
                            break;

                        case "TreatAsDifferentiable" when inInterface:
                            Report(SlangRuleDescriptors.ModifierNotAllowed, attribute.NameToken, "[TreatAsDifferentiable] は、インターフェイスの要件には書けません。");
                            break;

                        case var name when DerivativeAttributes.Contains(name)
                                           && attribute.ArgumentTokens is [{ Kind: ShaderSyntaxKind.IdentifierToken } target]
                                           && IsUndeclaredFunction(compilation, program, target.Text):
                            Report(HlslRuleDescriptors.UndeclaredIdentifier, target, target.Text, "どこにも");
                            break;
                    }
                }

                CheckDerivativeGroup(function, Report);
                CheckCudaKernel(function, differentiableStructs, Report);

                if (function.Body is { } body
                    && function.Attributes.Any(a => a.Name is "Differentiable" or "ForwardDifferentiable" or "BackwardDifferentiable"))
                {
                    CheckLoops(body, constants, Report);
                    CheckVectorConditionals(body, compilation.GetExpressionTypeBinder(program), Report);
                }
            }
        }
    }

    /// <summary>関数の名前が、解析するファイル・取り込んだモジュール (型のメソッドを含む)・標準ライブラリ・読み飛ばした宣言のどこにも無いか。</summary>
    private static bool IsUndeclaredFunction(ShaderCompilation compilation, AnalyzedProgram program, string name)
        => compilation.GetFunctionDeclarations(name, program).IsEmpty
           && !compilation.Programs.Any(p => p.Tree.Root.DescendantNodesAndSelf().Any(n => n switch
           {
               FunctionDeclarationSyntax f => f.Name == name,
               IncompleteDeclarationSyntax d => d.Tokens.Any(t => t.Text == name),
               _ => false,
           }))
           // __ で始まる名前は、型の仕組みがコンパイラの組み込みとして通すので、標準ライブラリの関数の名前の一覧で見る。
           && !(name.StartsWith("__", StringComparison.Ordinal)
               ? compilation.TypeSystem.ForTree(program.Tree).FunctionNames.Contains(name)
               : compilation.TypeSystem.ForTree(program.Tree).IsKnownFunction(name, program.Tree))
           && !compilation.UnanalyzedIdentifiers.Contains(name)
           && !program.Tree.PreprocessResult.Macros.ContainsKey(name)
           && compilation.HasCompleteDependencies;

    /// <summary>
    /// 微分する関数の中の、ベクトルか行列を条件にした ?: (成分ごとに選ぶので、短絡しない)。slangc 2026.19 は E30055 にする (no_diff の中も同じ)。
    /// </summary>
    private static void CheckVectorConditionals(BlockStatementSyntax body, ExpressionTypeBinder binder, Action<DiagnosticDescriptor, ShaderSyntaxToken, object?[]> report)
    {
        foreach (ConditionalExpressionSyntax conditional in body.DescendantNodesAndSelf().OfType<ConditionalExpressionSyntax>())
        {
            ExpressionSyntax condition = conditional.Condition;

            while (condition is ParenthesizedExpressionSyntax parenthesized)
            {
                condition = parenthesized.Expression;
            }

            if (binder.GetTypesByConfiguration(condition) is [{ Type: { } name }]
                && binder.TypeSystem.TryDescribeNumeric(name, out ShaderNumericShape shape)
                && shape.Kind is ShaderNumericKind.Vector or ShaderNumericKind.Matrix)
            {
                report(SlangRuleDescriptors.WrongOperandTypes, conditional.QuestionToken,
                    [$"微分する関数の中では、{(shape.Kind == ShaderNumericKind.Vector ? "ベクトル" : "行列")} ({name}) を条件にした ?: は使用できません。select(条件, 真の値, 偽の値) を使用してください。"]);
            }
        }
    }

    /// <summary>
    /// <c>[CudaKernel]</c> の関数の戻り値の型 (void だけ。E31213) と、微分する <c>[CudaKernel]</c> の関数の微分する仮引数 (E31214)。
    /// 微分する仮引数は、浮動小数 (float・half・double とそのベクトル・行列) と、IDifferentiable を継承の並びの先頭に書いた構造体で、no_diff を書いていないもの。
    /// </summary>
    private static void CheckCudaKernel(FunctionDeclarationSyntax function, HashSet<string> differentiableStructs, Action<DiagnosticDescriptor, ShaderSyntaxToken, object?[]> report)
    {
        if (!function.Attributes.Any(a => a.Name == "CudaKernel"))
        {
            return;
        }

        if (function.ReturnType.NameToken.Text != "void" && function.ReturnType.OpenAngle is null)
        {
            report(SlangRuleDescriptors.InvalidDeclarationType, function.NameToken, [$"[CudaKernel] の関数 '{function.Name}' の戻り値は void にしてください ({function.ReturnType.NameToken.Text} は使用できません)。"]);
        }

        if (!function.Attributes.Any(a => a.Name is "Differentiable" or "ForwardDifferentiable" or "BackwardDifferentiable"))
        {
            return;
        }

        foreach (ParameterSyntax parameter in function.ParameterList)
        {
            string type = parameter.Type.NameToken.Text;
            bool noDiff = parameter.ModifierTokens.Concat(parameter.Type.ModifierTokens).Any(t => t.Text == "no_diff");
            bool floating = parameter.Type.OpenAngle is null
                            && System.Text.RegularExpressions.Regex.IsMatch(type, @"^(float|half|double)([1-4](x[1-4])?)?$");

            if (!noDiff && (floating || differentiableStructs.Contains(type)))
            {
                report(SlangRuleDescriptors.InvalidDeclarationType, parameter.NameToken,
                    [$"微分する [CudaKernel] の関数の仮引数 '{parameter.Name}' は、微分する型 ({type}) にできません。no_diff を付けるか、TensorView などで渡してください。"]);
            }
        }
    }

    /// <summary>[DerivativeGroupLinear] と [DerivativeGroupQuad] の [numthreads] の値。</summary>
    private static void CheckDerivativeGroup(FunctionDeclarationSyntax function, Action<DiagnosticDescriptor, ShaderSyntaxToken, object?[]> report)
    {
        bool linear = function.Attributes.Any(a => a.Name == "DerivativeGroupLinear");
        bool quad = function.Attributes.Any(a => a.Name == "DerivativeGroupQuad");

        if ((!linear && !quad)
            || function.Attributes.FirstOrDefault(a => a.Name == "numthreads") is not { } threads
            || Values(threads.ArgumentTokens) is not [long x, long y, long z])
        {
            return;
        }

        if (linear && x * y * z % 4 != 0)
        {
            report(SlangRuleDescriptors.InvalidAttributeValue, function.NameToken, [$"[DerivativeGroupLinear] の [numthreads] は、スレッドの数の積 ({x * y * z}) が 4 の倍数でなければなりません。"]);
        }
        else if (quad && (x % 2 != 0 || y % 2 != 0))
        {
            report(SlangRuleDescriptors.InvalidAttributeValue, function.NameToken, [$"[DerivativeGroupQuad] の [numthreads] は、x と y ({x}, {y}) が 2 の倍数でなければなりません。"]);
        }
    }

    /// <summary>数のリテラルだけを , で並べた実引数の値。それ以外の形なら <see langword="null"/>。</summary>
    private static long[]? Values(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        List<long> values = [];

        for (int i = 0; i < tokens.Length; i++)
        {
            if (i % 2 == 1)
            {
                if (tokens[i].Kind != ShaderSyntaxKind.CommaToken)
                {
                    return null;
                }

                continue;
            }

            if (!ShaderLiteral.TryGetInt64(tokens[i], out long value))
            {
                return null;
            }

            values.Add(value);
        }

        return [.. values];
    }

    /// <summary>繰り返しの回数を決められないループを報告する。</summary>
    private static void CheckLoops(BlockStatementSyntax body, SlangConstants constants, Action<DiagnosticDescriptor, ShaderSyntaxToken, object?[]> report)
    {
        foreach (ShaderSyntaxNode node in body.DescendantNodesAndSelf())
        {
            (ImmutableArray<AttributeSyntax> attributes, ShaderSyntaxToken keyword, string? reason) = node switch
            {
                WhileStatementSyntax loop => (loop.Attributes, loop.WhileKeyword, "while のループ"),
                DoWhileStatementSyntax loop => (loop.Attributes, loop.DoKeyword, "do のループ"),
                ForStatementSyntax loop => (loop.Attributes, loop.ForKeyword, DescribeFor(loop, constants)),
                _ => (default, null!, null),
            };

            if (reason is null)
            {
                continue;
            }

            // 条件の無い for (;;) と、break / return で抜けるループは、slangc が回数を別に決めることがあるので見ない。
            if (node is ForStatementSyntax { Condition: null }
                || node.DescendantNodesAndSelf().Skip(1).Any(n => n is ReturnStatementSyntax or JumpStatementSyntax { Keyword.Text: "break" }))
            {
                continue;
            }

            // [MaxIters(n)] と [ForceUnroll] で回数が決まる。ほかの属性 ([unroll] など) の付いたループは、決められないので見ない。
            if (!attributes.IsDefaultOrEmpty)
            {
                continue;
            }

            report(SlangRuleDescriptors.LoopNeedsIterationCount, keyword,
                [$"微分する関数の中の{reason}は、繰り返しの回数を決められません。[MaxIters(n)] か [ForceUnroll] を付けるか、変数を定数と比較して定数ずつ増減する for にしてください。"]);
        }
    }

    /// <summary>for の繰り返しの回数を決められない理由。決められれば <see langword="null"/>。</summary>
    private static string? DescribeFor(ForStatementSyntax loop, SlangConstants constants)
    {
        if (loop.Initializer is not VariableDeclarationSyntax declaration || declaration.Variables.ToArray() is not [var variable] || variable.Initializer is not { } start || constants.Evaluate(start) is null)
        {
            return "for の、初期化で定数を入れた変数が存在しないループ";
        }

        string name = variable.Name;

        bool IsVariable(ExpressionSyntax expression) => expression is IdentifierExpressionSyntax { Name: var n } && n == name;

        if (loop.Condition is not BinaryExpressionSyntax
            {
                OperatorToken.Kind: ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.LessThanEqualsToken or ShaderSyntaxKind.GreaterThanToken
                    or ShaderSyntaxKind.GreaterThanEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken,
            } condition
            || !((IsVariable(condition.Left) && constants.Evaluate(condition.Right) is not null)
                 || (IsVariable(condition.Right) && constants.Evaluate(condition.Left) is not null)))
        {
            return $"for の、条件が '{name}' と定数の比較でないループ";
        }

        if (loop.Incrementors.Select(i => i.Node).OfType<ExpressionSyntax>().ToArray() is not [var step]
            || !(step switch
            {
                PostfixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } post => IsVariable(post.Operand),
                PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } pre => IsVariable(pre.Operand),
                AssignmentExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken } add => IsVariable(add.Left) && constants.Evaluate(add.Right) is not null,
                _ => false,
            }))
        {
            return $"for の、'{name}' を定数ずつ増減しないループ";
        }

        bool modified = loop.Body.DescendantNodesAndSelf().Any(n => n switch
        {
            AssignmentExpressionSyntax assignment => IsVariable(assignment.Left),
            PostfixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } post => IsVariable(post.Operand),
            PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } pre => IsVariable(pre.Operand),
            _ => false,
        });

        return modified ? $"for の、本体で '{name}' を書き換えるループ" : null;
    }
}
