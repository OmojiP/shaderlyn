using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、自分で書いた微分の関数と <c>no_diff</c> の書き方を検査する (SG0360)。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>[ForwardDerivative(g)]</c> の g の仮引数: 微分する型 (float・half・double とそのベクトル・行列) の仮引数 T は <c>DifferentialPair&lt;T&gt;</c>、ほかの型は T のまま。数が合わなければ E31150、型が合わなければ E31149</item>
/// <item><c>[BackwardDerivative(g)]</c> の g の仮引数: 微分する型の仮引数 T は <c>inout DifferentialPair&lt;T&gt;</c>、ほかの型は T のまま。戻り値が微分する型なら、最後にその微分 (T か T.Differential) の仮引数を足す</item>
/// <item><c>[ForwardDerivativeOf(f)]</c> と <c>[BackwardDerivativeOf(f)]</c> (微分の関数の側に書く属性) も、同じ形で見る</item>
/// <item>static のメソッドの微分に指した、static でないメソッド (E31156)</item>
/// <item>関数の呼び出しでない値の <c>no_diff x</c> (E38031) と、微分しない関数の中の <c>no_diff</c> (E38033)</item>
/// </list>
/// 見るのは、ジェネリックでなく、同じ名前の関数が 1 つだけで、仮引数の型がどれも組み込みの数の型 (微分するかどうかが決まるもの) で、
/// 元の関数の仮引数に <c>out</c>・<c>inout</c>・<c>no_diff</c> を書いていないものだけである。
/// </remarks>
internal sealed partial class SlangCustomDerivativeAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>微分する組み込みの型 (float・half・double とそのベクトル・行列)。</summary>
    [GeneratedRegex(@"^(float|half|double)([1-4](x[1-4])?)?$")]
    private static partial Regex DifferentiableType();

    /// <summary>微分しない組み込みの型 (整数と bool とそのベクトル・行列)。</summary>
    [GeneratedRegex(@"^(u?int|bool|u?int(8|16|32|64)_t)([1-4](x[1-4])?)?$")]
    private static partial Regex PlainType();

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.CustomDerivative];

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

            void Report(ShaderSyntaxToken at, string message)
            {
                if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.CustomDerivative, location, message));
                }
            }

            // 名前ごとの関数 (関数の外のものと、型のメソッド)。同じ入れ物の同じ名前が 2 つ以上あれば、どれか決めない。
            FunctionDeclarationSyntax[] all = [.. root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>()];

            FunctionDeclarationSyntax? Find(string name, object? container)
            {
                FunctionDeclarationSyntax[] found = [.. all.Where(f => f.Name == name && ReferenceEquals(f.Parent, container) && f.QualifierTokens.IsEmpty)];

                if (found.Length == 0 && container is not CompilationUnitSyntax)
                {
                    found = [.. all.Where(f => f.Name == name && f.Parent is CompilationUnitSyntax && f.QualifierTokens.IsEmpty)];
                }

                // 宣言と定義の 2 つ (前方宣言) は 1 つと数える。
                return found.Length == 1 || (found.Length == 2 && found.Count(f => f.Body is not null) == 1) ? found.First(f => f.Body is not null || found.Length == 1) : null;
            }

            // 微分の属性で指した関数の名前 (微分の関数として微分の文脈で使う)。
            HashSet<string> derivatives = [.. all.SelectMany(f => f.Attributes)
                .Where(a => a.Name.Contains("Derivative", StringComparison.Ordinal) || a.Name.Contains("PrimalSubstitute", StringComparison.Ordinal))
                .SelectMany(a => a.ArgumentTokens).Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken).Select(t => t.Text)];

            foreach (FunctionDeclarationSyntax function in all)
            {
                foreach (AttributeSyntax attribute in function.Attributes)
                {
                    if (attribute.Name is not ("ForwardDerivative" or "BackwardDerivative" or "ForwardDerivativeOf" or "BackwardDerivativeOf")
                        || attribute.ArgumentTokens is not [{ Kind: ShaderSyntaxKind.IdentifierToken } target]
                        || Find(target.Text, function.Parent) is not { } other)
                    {
                        continue;
                    }

                    bool of = attribute.Name.EndsWith("Of", StringComparison.Ordinal);
                    (FunctionDeclarationSyntax primal, FunctionDeclarationSyntax derivative) = of ? (other, function) : (function, other);
                    bool backward = attribute.Name.StartsWith("Backward", StringComparison.Ordinal);

                    // static のメソッドの微分は、static でなければならない (E31156)。
                    if (!of && primal.Parent is StructDeclarationSyntax && ReferenceEquals(primal.Parent, derivative.Parent)
                        && IsStatic(primal) && !IsStatic(derivative))
                    {
                        Report(attribute.NameToken, $"static のメソッド '{primal.Name}' の微分 '{derivative.Name}' も、static にしてください。");
                        continue;
                    }

                    // 型のメソッド (static でないもの) の微分は、最初に自分の DifferentialPair を受け取る。その形は見ない。
                    if (primal.Parent is StructDeclarationSyntax && !IsStatic(primal))
                    {
                        continue;
                    }

                    if (CheckSignature(primal, derivative, backward) is { } problem)
                    {
                        Report(attribute.NameToken, $"微分の関数 '{derivative.Name}' の形が、'{primal.Name}' から決まる形と合いません。{problem}");
                    }
                }

                CheckNoDiff(function, program.Tree.PreprocessResult.Tokens, derivatives.Contains(function.Name), Report);
            }
        }
    }

    private static bool IsStatic(FunctionDeclarationSyntax function) => function.ModifierTokens.Any(t => t.Text == "static");

    /// <summary>微分の関数の仮引数の、合わないところ。合うか、決められなければ <see langword="null"/>。</summary>
    private static string? CheckSignature(FunctionDeclarationSyntax primal, FunctionDeclarationSyntax derivative, bool backward)
    {
        if (!primal.GenericParameterTokens.IsDefaultOrEmpty || !derivative.GenericParameterTokens.IsDefaultOrEmpty
            || primal.ParameterList.Any(p => p.IsArray || p.ModifierTokens.Any(t => t.Text is "out" or "inout" or "no_diff" or "ref") || p.Type.ModifierTokens.Any(t => t.Text is "out" or "inout" or "no_diff"))
            || derivative.ParameterList.Any(p => p.IsArray))
        {
            return null;
        }

        List<(string Type, bool InOut)> expected = [];

        foreach (ParameterSyntax parameter in primal.ParameterList)
        {
            string type = TypeText(parameter.Type);

            if (DifferentiableType().IsMatch(type))
            {
                expected.Add(($"DifferentialPair<{type}>", backward));
            }
            else if (PlainType().IsMatch(type))
            {
                expected.Add((type, false));
            }
            else
            {
                return null;
            }
        }

        string returned = TypeText(primal.ReturnType);
        bool differentiableReturn = DifferentiableType().IsMatch(returned);

        if (!differentiableReturn && returned != "void" && !PlainType().IsMatch(returned))
        {
            return null;
        }

        if (backward && differentiableReturn)
        {
            expected.Add((returned, false));
        }

        ParameterSyntax[] actual = [.. derivative.ParameterList];

        if (actual.Length != expected.Count)
        {
            return $"仮引数は {expected.Count} 個 ({string.Join(", ", expected.Select(e => (e.InOut ? "inout " : string.Empty) + e.Type))}) ですが、{actual.Length} 個あります。";
        }

        for (int i = 0; i < actual.Length; i++)
        {
            string type = TypeText(actual[i].Type);
            bool inout = actual[i].ModifierTokens.Any(t => t.Text == "inout") || actual[i].Type.ModifierTokens.Any(t => t.Text == "inout");
            bool lastDifferential = backward && differentiableReturn && i == actual.Length - 1;
            bool typeMatches = type == expected[i].Type || (lastDifferential && type == expected[i].Type + ".Differential")
                               || type.Replace("DiffPair<", "DifferentialPair<", StringComparison.Ordinal) == expected[i].Type;

            // 型の名前が組み込みの型でも DifferentialPair でもなければ (別名など)、決めない。
            if (!typeMatches && !DifferentiableType().IsMatch(type) && !PlainType().IsMatch(type) && !type.StartsWith("DifferentialPair<", StringComparison.Ordinal)
                && !type.EndsWith(".Differential", StringComparison.Ordinal))
            {
                return null;
            }

            if (!typeMatches || (expected[i].InOut && !inout))
            {
                return $"{i + 1} 番目の仮引数は {(expected[i].InOut ? "inout " : string.Empty)}{expected[i].Type}{(lastDifferential ? $" (か {expected[i].Type}.Differential)" : string.Empty)} ですが、{(inout ? "inout " : string.Empty)}{type} です。";
            }
        }

        return null;
    }

    /// <summary>型の綴り (修飾子を除く)。</summary>
    private static string TypeText(TypeSyntax type)
        => string.Concat(type.DescendantTokens().Where(t => !type.ModifierTokens.Contains(t)).Select(t => t.Text));

    /// <summary>関数の本体の no_diff を見る。</summary>
    /// <remarks>構文木は no_diff を残さないので、本体の範囲のトークンを読む。</remarks>
    private static void CheckNoDiff(FunctionDeclarationSyntax function, ImmutableArray<ShaderSyntaxToken> all, bool isDerivative, Action<ShaderSyntaxToken, string> report)
    {
        if (function.Body is not { } body || !all.Any(t => t.Text == "no_diff"))
        {
            return;
        }

        ShaderSyntaxToken[] tokens = [.. all.Where(t => ReferenceEquals(t.Source, body.OpenBrace.Source)
                                                         && t.Span.Start > body.OpenBrace.Span.Start && t.Span.Start < body.CloseBrace.Span.Start)];
        // 微分する関数・微分の関数 (属性で微分を書いたもの、ほかの関数の微分の属性で指したもの)・微分するとみなす関数。
        bool differentiable = isDerivative
                              || function.Attributes.Any(a => a.Name.Contains("Differentiable", StringComparison.Ordinal) || a.Name.Contains("Derivative", StringComparison.Ordinal)
                                                              || a.Name.Contains("PrimalSubstitute", StringComparison.Ordinal) || a.Name == "PreferRecompute")
                              || function.ModifierTokens.Any(t => t.Text is "__differentiable");

        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Text != "no_diff" || tokens[i].Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            // no_diff float2 pos = …; (微分しない局所変数の宣言) は見ない。
            if (tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && i + 2 < tokens.Length && tokens[i + 2].Kind is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.LessThanToken)
            {
                continue;
            }

            // no_diff x; (名前だけ。x.y のように . でつないだものも)
            int j = i + 1;

            while (j + 1 < tokens.Length && tokens[j].Kind == ShaderSyntaxKind.IdentifierToken && tokens[j + 1].Kind == ShaderSyntaxKind.DotToken)
            {
                j += 2;
            }

            if (tokens[j].Kind == ShaderSyntaxKind.IdentifierToken && j + 1 < tokens.Length
                && tokens[j + 1].Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.CloseParenToken
                    or ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.SlashToken
                && i > 0 && (tokens[i - 1].Kind is ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.CommaToken || tokens[i - 1].Text == "return"))
            {
                report(tokens[i], "no_diff は、関数の呼び出しにだけ書けます (no_diff f(x))。");
                continue;
            }

            if (!differentiable)
            {
                report(tokens[i], $"微分しない関数 '{function.Name}' の中では、no_diff は使えません。");
            }
        }
    }
}
