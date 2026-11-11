using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、コンパイル時の定数でなければならない実引数を検査する (SG0352)。テクスチャの <c>Sample</c> などの offset (E40012)。
/// </summary>
/// <remarks>
/// <para>
/// offset の実引数 (<c>t.Sample(s, uv, offset)</c> の 3 つ目など) に、定数でないと言える値を使っていれば誤り。定数でないと言えるのは、次の名前を使う式である。
/// </para>
/// <list type="bullet">
/// <item>定数バッファーのメンバーと、関数の外の static const でない変数 (uniform)</item>
/// <item>ループの中で増減する変数 (<c>[ForceUnroll]</c> の無いループの for の変数)</item>
/// <item>宣言の後に、if などの中で代入し直す局所変数</item>
/// </list>
/// 仮引数は、関数を展開すれば定数になりうるので見ない。
/// </remarks>
internal sealed class SlangConstantArgumentAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>メソッドの名前と、実引数の数ごとの offset の位置 (0 から)。</summary>
    private static readonly Dictionary<string, Dictionary<int, int>> Offsets = new(StringComparer.Ordinal)
    {
        ["Sample"] = new() { [3] = 2, [4] = 2 },
        ["SampleBias"] = new() { [4] = 3, [5] = 3 },
        ["SampleLevel"] = new() { [4] = 3, [5] = 3 },
        ["SampleCmp"] = new() { [4] = 3, [5] = 3 },
        ["SampleCmpLevelZero"] = new() { [4] = 3, [5] = 3 },
        ["SampleGrad"] = new() { [5] = 4, [6] = 4 },
    };

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.ArgumentMustBeConstant];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            foreach (InvocationExpressionSyntax call in program.Tree.Root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                // __floatAsInt(1.5) の実引数は、浮動小数のリテラル (と、その符号を変えたもの) でなければならない (E30084・E30085)。
                if (call.TargetName == "__floatAsInt" && call.ArgumentExpressions.ToArray() is [var bits])
                {
                    ExpressionSyntax value = bits is PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.PlusToken } signed ? signed.Operand : bits;

                    // 浮動小数の型へ変換したリテラル (float(1)・(float)1) も定数である。
                    if ((value is LiteralExpressionSyntax { Token.Kind: ShaderSyntaxKind.NumericLiteralToken } literal && ShaderLiteral.GetKind(literal.Token) == ShaderLiteralKind.Floating)
                        || (value is InvocationExpressionSyntax { TargetName: "float" or "half" or "double" } converted && converted.ArgumentExpressions.ToArray() is [LiteralExpressionSyntax])
                        || value is CastExpressionSyntax { Type.Name: "float" or "half" or "double", Operand: LiteralExpressionSyntax })
                    {
                        continue;
                    }

                    string problem = value is LiteralExpressionSyntax || (value is InvocationExpressionSyntax { TargetName: "int" or "uint" or "bool" })
                        ? "浮動小数でない値"
                        : "コンパイル時の定数でない値";

                    if (compilation.IsReportable(bits) && bits.GetLocation() is { } bitsLocation)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ArgumentMustBeConstant, bitsLocation,
                            $"__floatAsInt の実引数は、浮動小数のリテラルでなければなりません ({problem}を渡しています)。"));
                    }

                    continue;
                }

                if (call.Target is not MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access
                    || !Offsets.TryGetValue(access.Name, out Dictionary<int, int>? positions))
                {
                    continue;
                }

                ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];

                if (!positions.TryGetValue(arguments.Length, out int position)
                    || binder.GetEvaluatorFor(access.Target).Evaluate(access.Target) is not { } receiver
                    || !receiver.StartsWith("Texture", StringComparison.Ordinal)
                    || call.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is not { } function
                    || FindNonConstant(binder, function, arguments[position]) is not { } what
                    || !compilation.IsReportable(arguments[position])
                    || arguments[position].GetLocation() is not { } location)
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ArgumentMustBeConstant, location,
                    $"{access.Name} の offset の実引数は、コンパイル時の定数でなければなりません ({what}を使っています)。"));
            }
        }
    }

    /// <summary>式の中の、定数でないと言える名前の説明。無ければ <see langword="null"/>。</summary>
    private static string? FindNonConstant(ExpressionTypeBinder binder, FunctionDeclarationSyntax function, ExpressionSyntax expression)
    {
        foreach (IdentifierExpressionSyntax identifier in expression.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
        {
            if (identifier.Parent is MemberAccessExpressionSyntax access && !ReferenceEquals(access.Target, identifier)
                || identifier.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Target, identifier)
                || binder.ResolveName(identifier).Candidates is not [var candidate]
                || candidate.Declarator is not VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } variable)
            {
                continue;
            }

            bool isConst = declaration.ModifierTokens.Any(t => t.Text is "const" or "constexpr");

            switch (declaration.Parent)
            {
                case ConstantBufferDeclarationSyntax:
                    return $"定数バッファーのメンバー '{variable.Name}' ";

                case CompilationUnitSyntax or NamespaceDeclarationSyntax when !(isConst && declaration.ModifierTokens.Any(t => t.Text == "static")):
                    return $"uniform の '{variable.Name}' ";

                case ForStatementSyntax loop when loop.Attributes.IsDefaultOrEmpty:
                    return $"ループの変数 '{variable.Name}' ";

                case LocalDeclarationStatementSyntax when !isConst && IsReassigned(function, variable):
                    return $"代入し直す変数 '{variable.Name}' ";
            }
        }

        return null;
    }

    /// <summary>局所変数を、宣言の後に代入し直すか (+= と ++ を含む)。</summary>
    private static bool IsReassigned(FunctionDeclarationSyntax function, VariableDeclaratorSyntax variable)
        => function.Body is { } body && body.DescendantNodesAndSelf().Any(n => n switch
        {
            AssignmentExpressionSyntax { Left: IdentifierExpressionSyntax target } => target.Name == variable.Name,
            PostfixUnaryExpressionSyntax { Operand: IdentifierExpressionSyntax target, OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } => target.Name == variable.Name,
            PrefixUnaryExpressionSyntax { Operand: IdentifierExpressionSyntax target, OperatorToken.Kind: ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken } => target.Name == variable.Name,
            _ => false,
        });
}
