using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// 微分する関数の戻り値に流れ込む、微分しない関数の呼び出し (E41022)。
/// </summary>
internal sealed partial class SlangCustomDerivativeAnalyzer
{
    /// <summary>
    /// 微分する関数 (<c>[ForwardDifferentiable]</c>・<c>[Differentiable]</c>) の中で、微分しない (か弱い微分の属性の) 利用者の関数を呼び、
    /// その結果が戻り値に流れ込むものを報告する (slangc の E41022)。
    /// </summary>
    /// <remarks>
    /// slangc 2026.19 で確かめた条件。
    /// <list type="bullet">
    /// <item>結果が戻り値に流れ込む呼び出しだけが誤り。文として書いた呼び出し、使わない局所変数への代入、if の条件、out の仮引数への代入は通る</item>
    /// <item>微分しない仮引数 (no_diff) でない仮引数への実引数がすべてリテラル (か、リテラルで初期化して代入し直さない局所変数) なら通る。static const の値や uniform は誤り</item>
    /// <item><c>no_diff g(x)</c>・<c>no_diff(g(x))</c> と書けば通る</item>
    /// <item>前向きの微分の属性の関数は、後ろ向きに微分する関数からは呼べない</item>
    /// </list>
    /// 見るのは、戻り値が微分する組み込みの型 (float・half・double とそのベクトル・行列) で、ジェネリックでない関数の外の関数だけである。
    /// 自分で書いた微分 (<c>[ForwardDerivative]</c> など) と <c>[PrimalSubstitute]</c> のある関数は、その微分で満たすので見ない。組み込みの関数は見ない。
    /// </remarks>
    private static void CheckNonDifferentiableCalls(ShaderCompilation compilation, AnalyzedProgram program, FunctionDeclarationSyntax[] all, ImmutableArray<ShaderSyntaxToken> tokens, SyntaxTreeAnalysisContext context)
    {
        Dictionary<ShaderSyntaxToken, int> positions = new(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < tokens.Length; i++)
        {
            positions[tokens[i]] = i;
        }

        // 自分で書いた微分の関数を指す属性 ([ForwardDerivativeOf(f)] など) の、指された関数の名前と強さ。
        Dictionary<string, int> customOf = new(StringComparer.Ordinal);

        foreach (AttributeSyntax attribute in all.SelectMany(f => f.Attributes))
        {
            if (attribute.Name is "ForwardDerivativeOf" or "BackwardDerivativeOf" or "PrimalSubstituteOf"
                && attribute.ArgumentTokens is [{ Kind: ShaderSyntaxKind.IdentifierToken } target])
            {
                customOf[target.Text] = customOf.GetValueOrDefault(target.Text) | (attribute.Name switch { "ForwardDerivativeOf" => 1, "BackwardDerivativeOf" => 2, _ => 4 });
            }
        }

        foreach (FunctionDeclarationSyntax caller in all)
        {
            if (caller.Body is not { } body || DifferentiabilityLevel(caller) is not (1 or 2) || !caller.GenericParameterTokens.IsDefaultOrEmpty)
            {
                continue;
            }

            int required = DifferentiabilityLevel(caller);

            foreach (InvocationExpressionSyntax call in FlowingCalls(body))
            {
                if (call.Target is not IdentifierExpressionSyntax target
                    || compilation.ResolveCall(call, program).Declaration is not { Parent: CompilationUnitSyntax or NamespaceDeclarationSyntax } callee
                    || !callee.GenericParameterTokens.IsDefaultOrEmpty
                    || !DifferentiableType().IsMatch(callee.ReturnType.Name)
                    || callee.Attributes.Any(a => a.Name is "ForwardDerivative" or "BackwardDerivative" or "PrimalSubstitute"))
                {
                    continue;
                }

                int custom = customOf.GetValueOrDefault(callee.Name);

                if ((custom & 4) != 0 || Math.Max(DifferentiabilityLevel(callee), (custom & 2) != 0 ? 2 : custom & 1) >= required
                    || OnlyLiterals(call, callee, body)
                    || IsNoDiff(call, tokens, positions)
                    || !compilation.IsReportable(target)
                    || target.GetLocation() is not { } location)
                {
                    continue;
                }

                string kind = required == 2 ? "後ろ向き" : "前向き";
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.CustomDerivative, location,
                    $"{kind}に微分する関数 '{caller.Name}' の戻り値に、{kind}に微分しない関数 '{callee.Name}' の結果が流れ込みます。"
                    + $"'{callee.Name}' に [{(required == 2 ? "Differentiable" : "ForwardDifferentiable")}] を書くか、微分しない場合は no_diff {callee.Name}(…) と書いてください。"));
            }
        }
    }

    /// <summary>戻り値に流れ込む式の中の呼び出し (比較・条件・添字・整数への変換の中のものを除く)。</summary>
    private static IEnumerable<InvocationExpressionSyntax> FlowingCalls(BlockStatementSyntax body)
    {
        List<ExpressionSyntax> roots = [.. body.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Select(r => r.Expression).OfType<ExpressionSyntax>()];
        HashSet<string> flowing = new(StringComparer.Ordinal);
        VariableDeclaratorSyntax[] locals = [.. body.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>()];
        AssignmentExpressionSyntax[] assignments = [.. body.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>()];

        // 戻り値の式が使う局所変数と、その初期値・代入の式が使う局所変数を、増えなくなるまで集める。
        bool changed = true;

        while (changed)
        {
            changed = false;

            foreach (string name in roots.SelectMany(r => r.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>()).Select(i => i.Name).ToArray())
            {
                if (!flowing.Add(name))
                {
                    continue;
                }

                changed = true;
                roots.AddRange(locals.Where(l => l.Name == name).Select(l => l.Initializer).OfType<ExpressionSyntax>());
                roots.AddRange(assignments.Where(a => a.Left is IdentifierExpressionSyntax { Name: var left } && left == name).Select(a => a.Right));
            }
        }

        HashSet<InvocationExpressionSyntax> seen = new(ReferenceEqualityComparer.Instance);

        foreach (ExpressionSyntax root in roots)
        {
            foreach (InvocationExpressionSyntax call in root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                if (seen.Add(call) && Flows(call, root))
                {
                    yield return call;
                }
            }
        }
    }

    /// <summary>呼び出しの値が、根の式の値まで微分を運ぶか (比較・論理・条件・添字・整数への変換を通らないか)。</summary>
    private static bool Flows(ExpressionSyntax call, ExpressionSyntax root)
    {
        Shaderlyn.Core.Syntax.SyntaxNode child = call;

        for (Shaderlyn.Core.Syntax.SyntaxNode? parent = call.Parent; parent is not null && !ReferenceEquals(child, root); child = parent, parent = parent.Parent)
        {
            switch (parent)
            {
                case BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.LessThanEqualsToken or ShaderSyntaxKind.GreaterThanToken
                    or ShaderSyntaxKind.GreaterThanEqualsToken or ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken
                    or ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken }:
                case ConditionalExpressionSyntax conditional when ReferenceEquals(conditional.Condition, child):
                case ElementAccessExpressionSyntax access when ReferenceEquals(access.Index, child):
                case CastExpressionSyntax cast when !DifferentiableType().IsMatch(cast.Type.Name):
                case InvocationExpressionSyntax { TargetName: { } name } when PlainType().IsMatch(name):
                    return false;
            }
        }

        return true;
    }

    /// <summary>微分する仮引数への実引数が、どれもリテラル (か、リテラルで初期化して代入し直さない局所変数) か。</summary>
    private static bool OnlyLiterals(InvocationExpressionSyntax call, FunctionDeclarationSyntax callee, BlockStatementSyntax body)
    {
        ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];
        ParameterSyntax[] parameters = [.. callee.ParameterList];

        for (int i = 0; i < arguments.Length; i++)
        {
            if (i < parameters.Length && (parameters[i].ModifierTokens.Any(t => t.Text == "no_diff") || parameters[i].Type.ModifierTokens.Any(t => t.Text == "no_diff")))
            {
                continue;
            }

            if (!IsLiteral(arguments[i])
                && !(arguments[i] is IdentifierExpressionSyntax { Name: var name }
                     && body.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>().Where(v => v.Name == name).ToArray() is [{ Initializer: { } initializer }]
                     && IsLiteral(initializer)
                     && !body.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>().Any(a => a.Left is IdentifierExpressionSyntax { Name: var left } && left == name)))
            {
                return false;
            }
        }

        return true;

        static bool IsLiteral(ExpressionSyntax expression) => expression switch
        {
            LiteralExpressionSyntax => true,
            PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax } => true,
            ParenthesizedExpressionSyntax { Expression: var inner } => IsLiteral(inner),
            _ => false,
        };
    }

    /// <summary>呼び出しの前に no_diff を書いたか (<c>no_diff g(x)</c>・<c>no_diff(g(x))</c>)。構文木は no_diff を残さないので、トークンを読む。</summary>
    private static bool IsNoDiff(InvocationExpressionSyntax call, ImmutableArray<ShaderSyntaxToken> tokens, Dictionary<ShaderSyntaxToken, int> positions)
    {
        if (call.FirstToken is not { } first || !positions.TryGetValue(first, out int index))
        {
            return true;
        }

        for (int i = index - 1; i >= 0; i--)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.OpenParenToken)
            {
                continue;
            }

            return tokens[i].Text == "no_diff";
        }

        return false;
    }
}
