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
/// <item>エントリポイントの仮引数と、それらの名前を使う初期値の局所変数 (初期値の位置で伝える)</item>
/// </list>
/// ほかの関数の仮引数は、関数を展開すれば定数になりうるので見ない。ByteAddressBuffer の LoadAligned・Store・StoreAligned の揃えも同じに見る。
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
        ["Load"] = new() { [2] = 1 },
    };

    /// <summary>ByteAddressBuffer のメソッドの名前と、実引数の数ごとの、揃え (alignment) の位置 (0 から)。</summary>
    private static readonly Dictionary<string, Dictionary<int, int>> Alignments = new(StringComparer.Ordinal)
    {
        ["LoadAligned"] = new() { [2] = 1 },
        ["Store"] = new() { [3] = 2 },
        ["StoreAligned"] = new() { [3] = 2 },
    };

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.ArgumentMustBeConstant, SlangRuleDescriptors.LoopNeedsIterationCount];

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

            foreach (ForStatementSyntax loop in program.Tree.Root.DescendantNodesAndSelf().OfType<ForStatementSyntax>())
            {
                CheckForceUnroll(context, compilation, binder, loop);
            }

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

                // constexpr の仮引数 (int f(constexpr int c)) への実引数 (E40013・E40012)。ループの変数は slangc が展開するので通る。
                // 呼ぶ側の関数の constexpr でない仮引数は、その関数を定数で呼んでいても誤り (slangc は関数ごとに見る)。
                if (call.Target is IdentifierExpressionSyntax
                    && compilation.ResolveCall(call, program).Declaration is { } callee
                    && callee.ParameterList.Any(p => p.ModifierTokens.Any(t => t.Text == "constexpr")))
                {
                    CheckConstexprArguments(context, compilation, binder, call, callee);
                    continue;
                }

                // t.Sample(s, uv, offset) の offset と、buf.LoadAligned<T>(offset, alignment) の alignment。
                if (call.Target is not MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access)
                {
                    continue;
                }

                ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];
                string? receiver = binder.GetEvaluatorFor(access.Target).Evaluate(access.Target);
                (int Position, string Role)? slot = receiver switch
                {
                    not null when receiver.StartsWith("Texture", StringComparison.Ordinal) && !receiver.Contains("MS", StringComparison.Ordinal)
                                  && Offsets.TryGetValue(access.Name, out Dictionary<int, int>? offsets) && offsets.TryGetValue(arguments.Length, out int o) => (o, "offset"),
                    not null when receiver.EndsWith("ByteAddressBuffer", StringComparison.Ordinal)
                                  && Alignments.TryGetValue(access.Name, out Dictionary<int, int>? alignments) && alignments.TryGetValue(arguments.Length, out int a) => (a, "揃え (alignment)"),
                    _ => null,
                };

                if (slot is not { } found
                    || call.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is not { } function
                    || FindNonConstant(binder, function, arguments[found.Position], 0) is not { } nonConstant
                    || !compilation.IsReportable(nonConstant.At)
                    || nonConstant.At.GetLocation() is not { } location)
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ArgumentMustBeConstant, location,
                    $"{access.Name} の {found.Role} の実引数は、コンパイル時の定数でなければなりません ({nonConstant.What}を使用しています)。"));
            }
        }
    }

    /// <summary>
    /// [ForceUnroll] の for のうち、展開できないものを報告する (SG0350。slangc の E40020)。
    /// 回数に負の数を書いたものと、条件で変数と比較する値が定数でないもの (uniform・エントリポイントの仮引数・展開しない外側のループの変数など) である。
    /// 外側の [ForceUnroll] のループの変数は、外側を展開すれば定数になるので通る。break と return で抜けるループは見ない。
    /// </summary>
    private static void CheckForceUnroll(SyntaxTreeAnalysisContext context, ShaderCompilation compilation, ExpressionTypeBinder binder, ForStatementSyntax loop)
    {
        if (loop.Attributes.IsDefaultOrEmpty
            || loop.Attributes.FirstOrDefault(a => a.Name == "ForceUnroll") is not { } attribute
            || !compilation.IsReportable(loop.ForKeyword)
            || loop.ForKeyword.GetLocation() is not { } location)
        {
            return;
        }

        if (attribute.ArgumentTokens is [{ Kind: ShaderSyntaxKind.MinusToken }, { Kind: ShaderSyntaxKind.NumericLiteralToken } count])
        {
            context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.LoopNeedsIterationCount, location,
                $"[ForceUnroll] の回数 (-{count.Text}) が負の数なので、ループを展開できません。"));
            return;
        }

        // 条件 (i < n など) の、ループの変数でない側。
        HashSet<string> variables = [.. loop.Initializer?.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>().Select(v => v.Name) ?? []];

        if (loop.Condition is not BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.LessThanEqualsToken or ShaderSyntaxKind.GreaterThanToken or ShaderSyntaxKind.GreaterThanEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken } comparison
            || loop.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is not { } function)
        {
            return;
        }

        ExpressionSyntax? bound = comparison.Left is IdentifierExpressionSyntax { Name: var left } && variables.Contains(left) ? comparison.Right
            : comparison.Right is IdentifierExpressionSyntax { Name: var right } && variables.Contains(right) ? comparison.Left
            : null;

        // break と return で抜けるループは、抜ける条件で回数が決まることがあるので見ない (外側を展開すれば定数になる値と比べる break など)。
        bool exits = loop.Body.DescendantNodesAndSelf().Any(n => n is ReturnStatementSyntax or JumpStatementSyntax { Keyword.Text: "break" });

        if (exits || bound is null || FindNonConstant(binder, function, bound, 0) is not { } nonConstant)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.LoopNeedsIterationCount, location,
            $"[ForceUnroll] のループの回数が決まらないので、展開できません (条件で比較する値に{nonConstant.What}を使用しています)。回数が決まらない場合は [ForceUnroll] を外してください。"));
    }

    /// <summary>constexpr の仮引数への実引数を検査する。</summary>
    private static void CheckConstexprArguments(SyntaxTreeAnalysisContext context, ShaderCompilation compilation, ExpressionTypeBinder binder, InvocationExpressionSyntax call, FunctionDeclarationSyntax callee)
    {
        ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];
        ParameterSyntax[] parameters = [.. callee.ParameterList];

        if (call.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is not { } caller)
        {
            return;
        }

        for (int i = 0; i < Math.Min(arguments.Length, parameters.Length); i++)
        {
            if (!parameters[i].ModifierTokens.Any(t => t.Text == "constexpr"))
            {
                continue;
            }

            (string What, ShaderSyntaxNode At)? nonConstant = FindNonConstant(binder, caller, arguments[i], 0, loops: false)
                ?? NonConstexprParameter(binder, caller, arguments[i]);

            if (nonConstant is not { } found || !compilation.IsReportable(found.At) || found.At.GetLocation() is not { } location)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ArgumentMustBeConstant, location,
                $"'{callee.Name}' の {i + 1} 番目の仮引数は constexpr なので、実引数はコンパイル時の定数でなければなりません ({found.What}を使用しています)。"));
        }
    }

    /// <summary>式が使う、constexpr でない仮引数 (エントリポイントでない関数のもの)。無ければ <see langword="null"/>。</summary>
    private static (string What, ShaderSyntaxNode At)? NonConstexprParameter(ExpressionTypeBinder binder, FunctionDeclarationSyntax function, ExpressionSyntax expression)
    {
        foreach (IdentifierExpressionSyntax identifier in expression.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
        {
            if (identifier.Parent is MemberAccessExpressionSyntax access && !ReferenceEquals(access.Target, identifier)
                || identifier.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Target, identifier)
                || binder.ResolveName(identifier).Candidates is not [var candidate])
            {
                continue;
            }

            if (candidate.Declarator is ParameterSyntax parameter && function.ParameterList.Contains(parameter)
                && !parameter.ModifierTokens.Any(t => t.Text == "constexpr"))
            {
                return ($"constexpr でない仮引数 '{parameter.Name}' ", expression);
            }
        }

        return null;
    }

    /// <summary>
    /// 式の中の、定数でないと言える名前の説明と、報告する位置。無ければ <see langword="null"/>。
    /// 局所変数の初期値が定数でなければ、その初期値の位置で伝える (slangc と同じ)。
    /// </summary>
    private static (string What, ShaderSyntaxNode At)? FindNonConstant(ExpressionTypeBinder binder, FunctionDeclarationSyntax function, ExpressionSyntax expression, int depth, bool loops = true)
    {
        if (depth > 8)
        {
            return null;
        }

        foreach (IdentifierExpressionSyntax identifier in expression.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
        {
            if (identifier.Parent is MemberAccessExpressionSyntax access && !ReferenceEquals(access.Target, identifier)
                || identifier.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Target, identifier)
                || binder.ResolveName(identifier).Candidates is not [var candidate])
            {
                continue;
            }

            // エントリポイントの仮引数 (SV_DispatchThreadID など)。ほかの関数の仮引数は、展開すれば定数になりうるので見ない。
            if (candidate.Declarator is ParameterSyntax parameter && function.ParameterList.Contains(parameter)
                && function.Attributes.Any(a => a.Name is "shader" or "numthreads"))
            {
                return ($"エントリポイントの仮引数 '{parameter.Name}' ", expression);
            }

            if (candidate.Declarator is not VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } variable)
            {
                continue;
            }

            bool isConst = declaration.ModifierTokens.Any(t => t.Text is "const" or "constexpr");

            switch (declaration.Parent)
            {
                case ConstantBufferDeclarationSyntax:
                    return ($"定数バッファーのメンバー '{variable.Name}' ", expression);

                case CompilationUnitSyntax or NamespaceDeclarationSyntax when !(isConst && declaration.ModifierTokens.Any(t => t.Text == "static")):
                    return ($"uniform の '{variable.Name}' ", expression);

                case ForStatementSyntax loop when loops && (loop.Attributes.IsDefaultOrEmpty || !loop.Attributes.Any(a => a.Name is "unroll" or "ForceUnroll")):
                    return ($"ループの変数 '{variable.Name}' ", expression);

                case LocalDeclarationStatementSyntax when !isConst && IsReassigned(function, variable):
                    return ($"代入し直す変数 '{variable.Name}' ", expression);

                // uint a = tid.x; (定数でない初期値の局所変数)。初期値の位置で伝える。
                case LocalDeclarationStatementSyntax when variable.Initializer is { } initializer
                                                          && FindNonConstant(binder, function, initializer, depth + 1, loops) is { } inner:
                    return (inner.What, initializer);
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
