using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、ジェネリックの制約を満たさない型引数を検査する (SG0349)。
/// </summary>
/// <remarks>
/// <para>
/// 制約 (<c>&lt;T : IFoo&gt;</c>・<c>where T : IFoo</c>) が利用者のインターフェイスで、型引数が利用者の構造体か組み込みの型のときに、
/// その型がインターフェイスに適合しなければ誤り (slangc 2026.19 の E38029「type argument doesn't conform to interface」)。
/// </para>
/// <para>
/// 型が適合するのは、型の継承の並び (<c>struct S : IFoo</c>) か、その型の <c>extension S : IFoo</c> に、そのインターフェイスか、それを継承したインターフェイスを書いたときだけである。
/// 基底の構造体の適合は受け継がない (<c>struct Child : Parent</c> の Parent が IFoo に適合していても、Child は適合しない。slangc で確かめた)。
/// </para>
/// <para>
/// 見る使い方は、型引数を書いた呼び出し (<c>f&lt;X&gt;(…)</c>)、仮引数の型がそのまま型の仮引数の呼び出し (<c>f(x)</c> の x の型)、型引数を書いた型 (<c>G&lt;X&gt;</c>) である。
/// 同じ名前の関数か型が 2 つ以上あるもの、ジェネリックのインターフェイス、インターフェイスそのものを型引数にしたもの (存在型) は見ない。
/// </para>
/// </remarks>
internal sealed class SlangConformanceAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>ジェネリックの宣言の仮引数と、その制約 (利用者のインターフェイスの名前)。</summary>
    private sealed record Generic(string Name, ImmutableArray<string> Parameters, ImmutableArray<ImmutableArray<string>> Constraints);

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.ConstraintNotSatisfied];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;
            CompilationUnitSyntax root = program.Tree.Root;

            // インターフェイスと型の継承の並び。extension の並びも型に足す。
            Dictionary<string, HashSet<string>?> interfaces = new(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>?> types = new(StringComparer.Ordinal);
            Dictionary<string, Generic?> functions = new(StringComparer.Ordinal);
            Dictionary<string, Generic?> structs = new(StringComparer.Ordinal);
            HashSet<string> extended = new(StringComparer.Ordinal);
            HashSet<string> blanket = new(StringComparer.Ordinal);

            Collect(tokens, interfaces, types, functions, structs, extended, blanket);

            // ジェネリックのインターフェイス (interface IFoo<T>) は、型引数で要件が変わるので見ない。
            if (interfaces.Count == 0)
            {
                continue;
            }

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            // 型が、インターフェイスに適合しないと言えるか。
            bool Violates(string type, string requirement)
            {
                if (!interfaces.TryGetValue(requirement, out HashSet<string>? _) || interfaces[requirement] is null
                    || interfaces.Keys.Any(i => blanket.Contains(i) && Derives(interfaces, i, requirement, 0)))
                {
                    return false;
                }

                bool builtin = binder.TypeSystem.TryDescribeNumeric(type, out _) || type is "bool" or "void";

                if (interfaces.ContainsKey(type) || root.TypeAliases.ContainsKey(type) || (!builtin && compilation.UnanalyzedIdentifiers.Contains(type) && !types.ContainsKey(type)))
                {
                    return false;
                }

                HashSet<string>? bases;

                if (types.TryGetValue(type, out bases))
                {
                    if (bases is null)
                    {
                        return false;
                    }
                }
                else if (builtin)
                {
                    bases = [];
                }
                else
                {
                    return false;
                }

                return !bases.Any(b => Derives(interfaces, b, requirement, 0));
            }

            void Report(ShaderSyntaxToken at, string type, string requirement, string parameter, string generic)
            {
                if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ConstraintNotSatisfied, location,
                        $"{type} はインターフェイス {requirement} に適合しないので、{generic} の型の仮引数 {parameter} にできません。"
                        + $"{type} の継承の並びか extension {type} : {requirement} に、{requirement} を書いてください。"));
                }
            }

            void CheckArguments(Generic generic, IReadOnlyList<string?> arguments, ShaderSyntaxToken at)
            {
                for (int i = 0; i < generic.Parameters.Length && i < arguments.Count; i++)
                {
                    if (arguments[i] is not { } argument)
                    {
                        continue;
                    }

                    foreach (string requirement in generic.Constraints[i])
                    {
                        if (Violates(argument, requirement))
                        {
                            Report(at, argument, requirement, generic.Parameters[i], generic.Name);
                            return;
                        }
                    }
                }
            }

            foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax { TargetName: { } name } call
                        when functions.TryGetValue(name, out Generic? generic) && generic is not null:
                        if (call.HasTypeArguments)
                        {
                            CheckArguments(generic, SimpleArguments(call.TypeArgumentTokens), call.Target.FirstToken ?? call.OpenParen);
                        }
                        else if (Inferred(compilation, program, binder, call, generic, literals: !types.Keys.Any(t => binder.TypeSystem.TryDescribeNumeric(t, out _))) is { } inferred)
                        {
                            CheckArguments(generic, inferred, call.Target.FirstToken ?? call.OpenParen);
                        }

                        break;

                    // f<X>(…) (構文解析が型引数つきの名前の式にしたもの)
                    case InvocationExpressionSyntax { Target: GenericNameExpressionSyntax named } genericCall
                        when functions.TryGetValue(named.Name, out Generic? explicitGeneric) && explicitGeneric is not null:
                        CheckArguments(explicitGeneric, SimpleArguments(named.TypeArguments), named.Identifier);
                        break;

                    case TypeSyntax type when type.OpenAngle is not null
                                              && structs.TryGetValue(type.NameToken.Text, out Generic? generic) && generic is not null
                                              && !type.TemplateArguments.IsDefaultOrEmpty:
                        CheckArguments(generic, SimpleArguments(type.TemplateArguments), type.NameToken);
                        break;
                }
            }
        }
    }

    /// <summary>インターフェイス <paramref name="name"/> が、<paramref name="requirement"/> そのものか、それを継承したものか。</summary>
    private static bool Derives(Dictionary<string, HashSet<string>?> interfaces, string name, string requirement, int depth)
        => name == requirement
           || (depth < 256 && interfaces.TryGetValue(name, out HashSet<string>? bases) && bases is not null && bases.Any(b => Derives(interfaces, b, requirement, depth + 1)));

    /// <summary>型引数の並び (<c>X, int</c>) を、名前 1 つだけの引数ごとに分ける。名前でない引数は <see langword="null"/>。</summary>
    private static List<string?> SimpleArguments(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        List<string?> arguments = [];
        List<ShaderSyntaxToken> current = [];
        int depth = 0;

        foreach (ShaderSyntaxToken token in tokens)
        {
            if (token.Kind == ShaderSyntaxKind.LessThanToken)
            {
                depth++;
            }
            else if (token.Kind == ShaderSyntaxKind.GreaterThanToken)
            {
                depth--;
            }
            else if (token.Kind == ShaderSyntaxKind.CommaToken && depth == 0)
            {
                arguments.Add(current is [{ Kind: ShaderSyntaxKind.IdentifierToken } single] ? single.Text : null);
                current.Clear();
                continue;
            }

            current.Add(token);
        }

        arguments.Add(current is [{ Kind: ShaderSyntaxKind.IdentifierToken } last] ? last.Text : null);
        return arguments;
    }

    /// <summary>仮引数の型が型の仮引数そのもの (<c>T x</c>) の位置の実引数の型から、型引数を求める。求まらない位置は <see langword="null"/>。</summary>
    private static List<string?>? Inferred(ShaderCompilation compilation, AnalyzedProgram program, ExpressionTypeBinder binder, InvocationExpressionSyntax call, Generic generic, bool literals)
    {
        ImmutableArray<FunctionDeclarationSyntax> declarations = compilation.GetFunctionDeclarations(generic.Name, program);

        if (declarations is not [var declaration])
        {
            return null;
        }

        ParameterSyntax[] parameters = [.. declaration.ParameterList];
        ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];

        if (parameters.Length != arguments.Length)
        {
            return null;
        }

        ExpressionTypeEvaluator evaluator = binder.GetEvaluatorFor(call);
        string?[] inferred = new string?[generic.Parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            int index = generic.Parameters.IndexOf(parameters[i].Type.NameToken.Text);

            if (index < 0 || parameters[i].IsArray || parameters[i].Type.OpenAngle is not null || !parameters[i].Type.NestedTypeTokens.IsDefaultOrEmpty)
            {
                continue;
            }

            // 数のリテラルは、制約に合う組み込みの型に変わりうる。extension で利用者のインターフェイスに適合させた組み込みの型が無いときだけ見る。
            if ((arguments[i] is LiteralExpressionSyntax && !literals) || evaluator.Evaluate(arguments[i]) is not { } type)
            {
                continue;
            }

            if (inferred[index] is { } earlier && earlier != type)
            {
                return null;
            }

            inferred[index] = type;
        }

        return [.. inferred];
    }

    /// <summary>トークンの並びから、インターフェイス・型の継承の並びと、ジェネリックの関数と構造体の制約を集める。2 度宣言した名前は null にする。</summary>
    private static void Collect(
        ImmutableArray<ShaderSyntaxToken> tokens,
        Dictionary<string, HashSet<string>?> interfaces,
        Dictionary<string, HashSet<string>?> types,
        Dictionary<string, Generic?> functions,
        Dictionary<string, Generic?> structs,
        HashSet<string> extended,
        HashSet<string> blanket)
    {
        HashSet<string> declaredTwice = new(StringComparer.Ordinal);

        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            // interface I : A, B { / struct S<T : I> : A { / extension S : A {
            // extension<T : IBar> T : IFoo と extension vector<T, N> : IFoo (ジェネリックの extension) は、どの型が適合するか決めない。その並びのインターフェイスは見ない。
            if (token.Text is "extension" or "__extension" && (tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken || (i + 2 < tokens.Length && tokens[i + 2].Kind == ShaderSyntaxKind.LessThanToken)))
            {
                for (int j = i + 1; j < tokens.Length && tokens[j].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); j++)
                {
                    if (tokens[j].Kind == ShaderSyntaxKind.IdentifierToken)
                    {
                        blanket.Add(tokens[j].Text);
                    }
                }

                continue;
            }

            if (token.Text is "interface" or "struct" or "class" or "extension" or "__extension" && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                string name = tokens[i + 1].Text;
                int j = i + 2;
                (List<string> parameters, List<List<string>> constraints, j) = ReadGenericParameters(tokens, j);
                bool generic = parameters.Count > 0;
                HashSet<string>? bases = new(StringComparer.Ordinal);

                if (j < tokens.Length && tokens[j].Kind == ShaderSyntaxKind.ColonToken)
                {
                    int angle = 0;

                    for (j++; j < tokens.Length && tokens[j].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken) && tokens[j].Text != "where"; j++)
                    {
                        angle += tokens[j].Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, _ => 0 };

                        // 修飾した基底 (outer::repro::ICollector) は、どのインターフェイスか決めない。
                        if (tokens[j].Kind is ShaderSyntaxKind.ColonColonToken or ShaderSyntaxKind.DotToken)
                        {
                            bases = null;
                            break;
                        }

                        if (angle == 0 && tokens[j].Kind == ShaderSyntaxKind.IdentifierToken
                            && tokens[j - 1].Kind is ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.AmpersandToken)
                        {
                            bases.Add(tokens[j].Text);
                        }
                    }
                }

                // where T : IFoo (型の宣言)
                ReadWhere(tokens, j, parameters, constraints);

                switch (token.Text)
                {
                    case "interface":
                        interfaces[name] = interfaces.ContainsKey(name) || generic ? null : bases;
                        break;

                    case "extension" or "__extension":
                        extended.Add(name);

                        if (bases is null)
                        {
                            types[name] = null;
                        }
                        else if (types.TryGetValue(name, out HashSet<string>? existing) && existing is not null)
                        {
                            existing.UnionWith(bases);
                        }
                        else if (!types.ContainsKey(name))
                        {
                            // 型より前の extension。型の宣言で足す。
                            types[name] = bases;
                            declaredTwice.Add("\u0001" + name);
                        }

                        break;

                    default:
                        if (types.TryGetValue(name, out HashSet<string>? before))
                        {
                            if (before is not null && bases is not null && declaredTwice.Remove("\u0001" + name))
                            {
                                before.UnionWith(bases);
                            }
                            else
                            {
                                types[name] = null;
                            }
                        }
                        else
                        {
                            types[name] = bases;
                        }

                        if (generic && token.Text is "struct" or "class")
                        {
                            structs[name] = structs.ContainsKey(name) ? null : new Generic(name, [.. parameters], [.. constraints.Select(c => c.ToImmutableArray())]);
                        }

                        break;
                }

                continue;
            }

            // R f<T : I>(…) where T : J { / func f<T : I>(…) -> R / __generic<T : I> R f(…)
            if (tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken
                && i > 0 && tokens[i - 1].Kind == ShaderSyntaxKind.IdentifierToken
                && tokens[i - 1].Text is not ("return" or "case" or "else" or "throw" or "new" or "in" or "out" or "inout" or "typealias" or "typedef")
                && token.Text is not ("struct" or "class" or "interface" or "extension"))
            {
                (List<string> parameters, List<List<string>> constraints, int close) = ReadGenericParameters(tokens, i + 1);

                if (parameters.Count == 0 || close >= tokens.Length || tokens[close].Kind != ShaderSyntaxKind.OpenParenToken)
                {
                    continue;
                }

                int end = Matching(tokens, close);

                if (end < 0)
                {
                    continue;
                }

                ReadWhere(tokens, end + 1, parameters, constraints);
                Add(functions, token.Text, parameters, constraints);
            }
        }
    }

    private static void Add(Dictionary<string, Generic?> functions, string name, List<string> parameters, List<List<string>> constraints)
        => functions[name] = functions.ContainsKey(name) ? null : new Generic(name, [.. parameters], [.. constraints.Select(c => c.ToImmutableArray())]);

    /// <summary>
    /// <c>&lt;T : I, let N : int, U&gt;</c> を読む。型の仮引数の名前と、それぞれの制約 (名前だけのもの)。<c>let</c> の仮引数は値なので、制約を持たない名前として数える。
    /// </summary>
    private static (List<string> Parameters, List<List<string>> Constraints, int Next) ReadGenericParameters(ImmutableArray<ShaderSyntaxToken> tokens, int start)
    {
        List<string> parameters = [];
        List<List<string>> constraints = [];

        if (start >= tokens.Length || tokens[start].Kind != ShaderSyntaxKind.LessThanToken)
        {
            return (parameters, constraints, start);
        }

        int depth = 0;
        int i = start;
        bool expectName = true;
        bool value = false;

        for (; i < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind == ShaderSyntaxKind.LessThanToken)
            {
                depth++;
                continue;
            }

            if (token.Kind == ShaderSyntaxKind.GreaterThanToken && --depth == 0)
            {
                i++;
                break;
            }

            if (depth != 1)
            {
                continue;
            }

            if (token.Kind == ShaderSyntaxKind.CommaToken)
            {
                expectName = true;
                value = false;
                continue;
            }

            if (token.Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken)
            {
                return ([], [], i);
            }

            if (expectName && token.Kind == ShaderSyntaxKind.IdentifierToken)
            {
                if (token.Text is "let" or "each")
                {
                    value = token.Text == "let";
                    continue;
                }

                parameters.Add(token.Text);
                constraints.Add([]);
                expectName = false;
                continue;
            }

            if (!value && token.Kind == ShaderSyntaxKind.ColonToken && i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken
                && !(i + 2 < tokens.Length && tokens[i + 2].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken)
                && constraints.Count > 0)
            {
                constraints[^1].Add(tokens[i + 1].Text);
            }
        }

        return (parameters, constraints, i);
    }

    /// <summary><c>where T : I</c> の並びを読み、仮引数の制約に足す。</summary>
    private static void ReadWhere(ImmutableArray<ShaderSyntaxToken> tokens, int start, List<string> parameters, List<List<string>> constraints)
    {
        for (int i = start; i + 2 < tokens.Length && tokens[i].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); i++)
        {
            if (tokens[i].Text == "where" && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 2].Kind == ShaderSyntaxKind.ColonToken
                && i + 3 < tokens.Length && tokens[i + 3].Kind == ShaderSyntaxKind.IdentifierToken
                && !(i + 4 < tokens.Length && tokens[i + 4].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken)
                && parameters.IndexOf(tokens[i + 1].Text) is var index and >= 0)
            {
                constraints[index].Add(tokens[i + 3].Text);
            }
        }
    }

    private static int Matching(ImmutableArray<ShaderSyntaxToken> tokens, int open)
    {
        int depth = 0;

        for (int i = open; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.OpenParenToken)
            {
                depth++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.CloseParenToken && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
