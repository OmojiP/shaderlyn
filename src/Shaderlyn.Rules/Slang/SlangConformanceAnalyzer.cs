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
    private sealed record Generic(string Name, ImmutableArray<string> Parameters, ImmutableArray<ImmutableArray<string>> Constraints)
    {
        /// <summary>仮引数ごとの既定の型引数 (名前 1 つのもの)。無ければ <see langword="null"/>。</summary>
        public ImmutableArray<ShaderSyntaxToken?> Defaults { get; init; } = [];

        /// <summary>仮引数ごとの、関数の仮引数の並び (型と配列の大きさ) に出てくるか。関数でなければ空。</summary>
        public ImmutableArray<bool> Mentioned { get; init; } = [];

        /// <summary>インターフェイスでない制約 (<c>where T == int</c>・<c>where int(T)</c>・<c>where __hasDiffTypeInfo(T)</c>)。仮引数の位置と、種類 (==・()・diff) と、右の型。</summary>
        public ImmutableArray<(int Index, string Kind, string Target)> Special { get; init; } = [];

        /// <summary>関連型の制約 (<c>where T.Element : IMarker</c>・<c>where T.Element == int</c>)。仮引数の位置と、関連型の名前と、種類 (: か ==) と、右の型。</summary>
        public ImmutableArray<(int Index, string Member, string Kind, string Target)> Associated { get; init; } = [];
    }

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [SlangRuleDescriptors.ConstraintNotSatisfied, SlangRuleDescriptors.GenericArgumentNotInferred, HlslRuleDescriptors.ArgumentNotConvertible,
         SlangRuleDescriptors.InterfaceRequirement, SlangRuleDescriptors.CyclicConstant];

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

            // 見えないモジュール (取り込んだモジュールが __exported でなく取り込んだもの) の extension は、このファイルの型の適合に足さない。
            if (compilation.Text.FilePath is { } rootPath && SlangImportScopeAnalyzer.HiddenFiles(rootPath, program) is { Count: > 0 } hidden)
            {
                tokens = WithoutExtensions(tokens, hidden);
            }

            // インターフェイスと型の継承の並び。extension の並びも型に足す。
            Dictionary<string, HashSet<string>?> interfaces = new(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>?> types = new(StringComparer.Ordinal);
            Dictionary<string, Generic?> functions = new(StringComparer.Ordinal);
            Dictionary<string, Generic?> structs = new(StringComparer.Ordinal);
            HashSet<string> extended = new(StringComparer.Ordinal);
            HashSet<string> blanket = new(StringComparer.Ordinal);

            Dictionary<string, Generic?> genericInterfaces = new(StringComparer.Ordinal);
            Collect(tokens, interfaces, types, functions, structs, extended, blanket, genericInterfaces);

            // 構造体の中の関連型 (typealias Element = X;) と、本体の無い extern の構造体。
            (Dictionary<(string, string), string> aliases, HashSet<string> externs) = CollectMembers(tokens);

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
            bool versioned = System.Text.RegularExpressions.Regex.IsMatch(program.Tree.Text.ToString(), @"^\s*#\s*lang(uage)?\s+(slang\s+)?(20\d\d|latest)\b", System.Text.RegularExpressions.RegexOptions.Multiline);

            // 型の適合の表 (利用者の宣言と標準ライブラリ)。どの型にも当たる extension (extension<T : IBar> T : IFoo) も、制約を確かめて足す。
            SlangConformanceModel model = SlangConformanceModel.Build(tokens, compilation.TypeSystem.ForTree(program.Tree));

            // 型が、インターフェイスに適合しないと言えるか。
            bool Violates(string type, string requirement)
            {
                // 適合を全部表に書いた標準ライブラリの型 (RWBuffer など) も、組み込みの数の型と同じく、宣言を読み飛ばした型ではない。
                bool builtin = binder.TypeSystem.TryDescribeNumeric(type, out _) || type is "bool" or "void";
                builtin |= SlangStandardConformance.Instance.Closed.ContainsKey(type);

                // インターフェイスの型 (存在型。別に見る)・型の別名・読み飛ばした宣言の型は決めない。
                if (interfaces.ContainsKey(type) || root.TypeAliases.ContainsKey(type) || (!builtin && compilation.UnanalyzedIdentifiers.Contains(type) && !types.ContainsKey(type)))
                {
                    return false;
                }

                // 要件が、1 度だけ宣言した利用者のインターフェイスか、標準ライブラリのインターフェイスでなければ (関連型の制約など) 決めない。
                bool userRequirement = interfaces.TryGetValue(requirement, out HashSet<string>? declared) && declared is not null;
                bool standardRequirement = !interfaces.ContainsKey(requirement) && binder.TypeSystem.IsBuiltInTypeName(requirement)
                                           && requirement.Length > 1 && requirement[0] is 'I' or '_';

                if (!userRequirement && !standardRequirement)
                {
                    return false;
                }

                return model.Conforms(type, requirement) == false;
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

            void CheckArguments(Generic generic, IReadOnlyList<string?> arguments, ShaderSyntaxToken at, ShaderSyntaxNode site)
            {
                // 呼び出しを書いた関数の、型の仮引数と制約 (where optional は数えない)。
                Generic? enclosing = site.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is { } outer
                                     && functions.TryGetValue(outer.Name, out Generic? found) ? found : null;

                for (int i = 0; i < generic.Parameters.Length && i < arguments.Count; i++)
                {
                    if (arguments[i] is not { } argument)
                    {
                        continue;
                    }

                    // 外の関数の型の仮引数 T。T の制約から届くインターフェイスにだけ適合する。
                    if (enclosing is not null && enclosing.Parameters.IndexOf(argument) is var outerIndex and >= 0)
                    {
                        foreach (string requirement in generic.Constraints[i])
                        {
                            if ((interfaces.TryGetValue(requirement, out HashSet<string>? known) && known is not null) && !model.ConformsThrough(enclosing.Constraints[outerIndex], requirement)
                                && !at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                            {
                                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ConstraintNotSatisfied, location,
                                    $"型の仮引数 {argument} の制約から、インターフェイス {requirement} に届かないので、{generic.Name} の型の仮引数 {generic.Parameters[i]} にできません。"
                                    + $"where {argument} : {requirement} を書いてください (where optional では不十分です)。"));
                                return;
                            }
                        }

                        continue;
                    }

                    // インターフェイスの型を型引数に書いた呼び出し (f<IFoo>(…)) は特殊化できない (E33180)。言語のバージョンを書いたファイル (#lang slang 2025 以降) の、
                    // エントリポイントから届く関数の中だけ (特殊化は、コードを作るときに起きる)。書かなければ古い書き方として、動的なディスパッチで通る。
                    // 実引数から決めたインターフェイスの型は、値の出どころで見る (CheckExistentialFunctionCall)。
                    if (interfaces.TryGetValue(argument, out HashSet<string>? existential) && existential is not null)
                    {
                        if (versioned && site is InvocationExpressionSyntax { HasTypeArguments: true } or InvocationExpressionSyntax { Target: GenericNameExpressionSyntax } && Reachable(site)
                            && !at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } existentialLocation)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ConstraintNotSatisfied, existentialLocation,
                                $"インターフェイスの型 {argument} では、{generic.Name} の型の仮引数 {generic.Parameters[i]} を特殊化できません。具体的な型の値を渡してください。"));
                        }

                        return;
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

            // f() (実引数から決まらず、既定の値も無い型の仮引数のある呼び出し。E30439)
            // foo(a, b) の T に、違う型 (片方が構造体) が当たる (E30442)。bar(x, y) の N に、違う大きさの配列が当たる。組み込みの数の型どうしは、変換して合わせるので見ない。
            void CheckConflicts(Generic generic, InvocationExpressionSyntax call)
            {
                if (call.HasTypeArguments || call.Target is not IdentifierExpressionSyntax target
                    || compilation.GetFunctionDeclarations(generic.Name, program) is not [var declaration])
                {
                    return;
                }

                ParameterSyntax[] parameters = [.. declaration.ParameterList];
                ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];

                if (parameters.Length != arguments.Length)
                {
                    return;
                }

                ExpressionTypeEvaluator evaluator = binder.GetEvaluatorFor(call);
                Dictionary<int, string> seen = [];

                for (int i = 0; i < parameters.Length; i++)
                {
                    ParameterSyntax parameter = parameters[i];
                    string? found = null;
                    int index;

                    if (parameter.IsArray)
                    {
                        // int a[N] の N (値の仮引数) と、実引数の配列の大きさ。
                        index = parameter.ArrayRankTokens is [{ Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.IdentifierToken } size, { Kind: ShaderSyntaxKind.CloseBracketToken }]
                            ? generic.Parameters.IndexOf(size.Text)
                            : -1;

                        if (index >= 0 && arguments[i] is IdentifierExpressionSyntax array
                            && binder.ResolveName(array).Candidates is [{ Declarator: VariableDeclaratorSyntax { ArrayRankTokens: [_, { Kind: ShaderSyntaxKind.NumericLiteralToken } length, _] } }])
                        {
                            found = length.Text;
                        }
                    }
                    else
                    {
                        index = parameter.Type.OpenAngle is null && parameter.Type.NestedTypeTokens.IsDefaultOrEmpty ? generic.Parameters.IndexOf(parameter.Type.NameToken.Text) : -1;

                        if (index >= 0)
                        {
                            found = evaluator.Evaluate(arguments[i]);
                        }
                    }

                    if (index < 0 || found is null)
                    {
                        continue;
                    }

                    if (seen.TryGetValue(index, out string? earlier) && earlier != found
                        && (parameter.IsArray || IsStruct(earlier) || IsStruct(found))
                        && compilation.IsReportable(target.Identifier) && target.Identifier.GetLocation() is { } location)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.GenericArgumentNotInferred, location,
                            $"{generic.Name} の仮引数 {generic.Parameters[index]} に、違う{(parameter.IsArray ? "大きさ" : "型")} ({earlier} と {found}) が当たるので、決まりません。"));
                        return;
                    }

                    seen.TryAdd(index, found);
                }
            }

            bool IsStruct(string name) => types.TryGetValue(name, out HashSet<string>? b) && b is not null && !interfaces.ContainsKey(name);
            bool IsNumber(string name) => binder.TypeSystem.TryDescribeNumeric(name, out _) || name == "bool";

            // where T == int・where int(T)・where __hasDiffTypeInfo(T) を満たさない型引数 (E30440)。
            void CheckSpecial(Generic generic, IReadOnlyList<string?> arguments, ShaderSyntaxToken at)
            {
                foreach ((int index, string kind, string target) in generic.Special)
                {
                    // where Box<X> == Box<X>.Nested<X> (違う構造体どうしの等しさ。型引数によらず満たさない)
                    if (kind == "types" && target.Split('|') is [var leftName, var rightName, var leftText, var rightText])
                    {
                        if (leftName != rightName && IsStruct(leftName) && IsStruct(rightName) && !extended.Contains(leftName) && !extended.Contains(rightName)
                            && !root.TypeAliases.ContainsKey(leftName) && !root.TypeAliases.ContainsKey(rightName)
                            && !at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } typesLocation)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ConstraintNotSatisfied, typesLocation,
                                $"{leftText} と {rightText} は違う型なので、{generic.Name} の制約 where {leftText} == {rightText} を満たしません。"));
                            return;
                        }

                        continue;
                    }

                    if (index >= arguments.Count || arguments[index] is not { } argument || generic.Parameters.Contains(argument))
                    {
                        continue;
                    }

                    string parameter = generic.Parameters[index];
                    string? message = kind switch
                    {
                        "==" when argument != target && ((IsStruct(argument) && (IsStruct(target) || IsNumber(target))) || (IsNumber(argument) && (IsNumber(target) || IsStruct(target))))
                            => $"{argument} は {target} ではないので、{generic.Name} の制約 where {parameter} == {target} を満たしません。",
                        "()" when IsStruct(argument) && IsNumber(target) && !extended.Contains(target) && blanket.Count == 0
                            => $"構造体 {argument} は {target} に変換できないので、{generic.Name} の制約 where {target}({parameter}) を満たしません。",
                        "diff" when IsStruct(argument) && model.Conforms(argument, "IDifferentiable") == false
                            => $"構造体 {argument} は IDifferentiable に適合しないので、{generic.Name} の制約 where __hasDiffTypeInfo({parameter}) を満たしません。",
                        _ => null,
                    };

                    if (message is not null && !at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ConstraintNotSatisfied, location, message));
                        return;
                    }
                }
            }

            // RequiresIntElement<FloatContainer> (where T.Element == int の T に、Element が float の型を渡す)
            void CheckAssociated(Generic generic, IReadOnlyList<string?> arguments, ShaderSyntaxToken at)
            {
                foreach ((int index, string member, string kind, string target) in generic.Associated)
                {
                    if (index >= arguments.Count || arguments[index] is not { } argument || !IsStruct(argument) || extended.Contains(argument) || blanket.Count > 0)
                    {
                        continue;
                    }

                    string? message = null;

                    if (aliases.TryGetValue((argument, member), out string? actual))
                    {
                        if (kind == ":" && Violates(actual, target))
                        {
                            message = $"{argument}.{member} ({actual}) はインターフェイス {target} に適合しないので、{generic.Name} の制約 where {generic.Parameters[index]}.{member} : {target} を満たしません。";
                        }
                        else if (kind == "==" && actual != target && (IsStruct(actual) || IsNumber(actual)) && (IsStruct(target) || IsNumber(target)))
                        {
                            message = $"{argument}.{member} は {actual} なので、{generic.Name} の制約 where {generic.Parameters[index]}.{member} == {target} を満たしません。";
                        }
                    }
                    else if (kind == "==" && externs.Contains(argument) && (IsStruct(target) || IsNumber(target)))
                    {
                        message = $"extern の構造体 {argument} の {member} は決まっていないので、{generic.Name} の制約 where {generic.Parameters[index]}.{member} == {target} を満たしません。";
                    }

                    if (message is not null && !at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ConstraintNotSatisfied, location, message));
                        return;
                    }
                }
            }

            // foo(() => Array<int, 2>()) と foo(functor) の foo が f: functype()->vector<float, N> (関数の型の戻り値の vector の大きさ N を、
            // 数でも数のベクトルでもない戻り値の型 (Array・構造体) からは決められない。E30439)。N がほかの仮引数の型に出てくれば、そちらから決まるので見ない。
            void CheckFunctionTypeInference(Generic generic, InvocationExpressionSyntax call)
            {
                if (call.HasTypeArguments || call.Target is not IdentifierExpressionSyntax target
                    || compilation.GetFunctionDeclarations(generic.Name, program) is not [var declaration])
                {
                    return;
                }

                ParameterSyntax[] parameters = [.. declaration.ParameterList];
                ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];

                if (parameters.Length != arguments.Length)
                {
                    return;
                }

                // 仮引数の書いたとおりのトークン (構文木は functype の型引数と戻り値の型を持たないので、トークンの並びから読む)。
                List<string> Written(ParameterSyntax parameter)
                {
                    List<string> texts = [];

                    if (parameter.FirstToken is not { } first || tokens.IndexOf(first) is var at && at < 0)
                    {
                        return texts;
                    }

                    for (int depth = 0; at < tokens.Length; at++)
                    {
                        ShaderSyntaxKind kind = tokens[at].Kind;

                        if (depth == 0 && kind is ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken)
                        {
                            break;
                        }

                        // -> の > は山括弧の閉じでない。
                        bool arrow = kind == ShaderSyntaxKind.GreaterThanToken && tokens[at - 1].Kind == ShaderSyntaxKind.MinusToken;

                        depth += kind switch
                        {
                            ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.LessThanToken => 1,
                            ShaderSyntaxKind.GreaterThanToken when arrow => 0,
                            ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.GreaterThanToken => -1,
                            ShaderSyntaxKind.GreaterThanGreaterThanToken => -2,
                            _ => 0,
                        };
                        texts.Add(tokens[at].Text);
                    }

                    return texts;
                }

                for (int i = 0; i < parameters.Length; i++)
                {
                    string written = string.Concat(Written(parameters[i]));
                    System.Text.RegularExpressions.Match shape = System.Text.RegularExpressions.Regex.Match(written, @"functype\(.*\)->vector<[^<>]+,([A-Za-z_]\w*)>");

                    if (!shape.Success || shape.Groups[1].Value is not { } size || !generic.Parameters.Contains(size)
                        || parameters.Where((_, k) => k != i).Any(other => Written(other).Contains(size)))
                    {
                        continue;
                    }

                    if (FunctionResultHead(arguments[i]) is { } head && compilation.IsReportable(target.Identifier) && target.Identifier.GetLocation() is { } location)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.GenericArgumentNotInferred, location,
                            $"{generic.Name} の型の仮引数 {size} は、実引数から決まりません (実引数の関数の戻り値の型 {head} は、vector<…, {size}> になりません)。{generic.Name}<…>(…) と型引数を書くか、vector を返す関数を渡してください。"));
                        return;
                    }
                }
            }

            // ラムダ式 (() => Array<int, 2>()) か IFunc<R> に適合する構造体の値の、戻り値の型の名前。数でも数のベクトルでもないと言える型 (Array・利用者の構造体) だけ。
            string? FunctionResultHead(ExpressionSyntax argument)
            {
                string? head = null;

                if (argument is IncompleteExpressionSyntax lambda)
                {
                    ShaderSyntaxToken[] written = [.. lambda.DescendantTokens()];
                    int arrow = Array.FindIndex(written, t => t.Kind == ShaderSyntaxKind.EqualsToken);

                    if (arrow > 0 && arrow + 3 < written.Length && written[0].Kind == ShaderSyntaxKind.OpenParenToken && written[arrow + 1].Kind == ShaderSyntaxKind.GreaterThanToken
                        && written[arrow + 2].Kind == ShaderSyntaxKind.IdentifierToken && written[arrow + 3].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.OpenParenToken)
                    {
                        head = written[arrow + 2].Text;
                    }
                }
                else if (binder.GetEvaluatorFor(argument).Evaluate(argument) is { } type && IsStruct(type))
                {
                    // struct Functor : IFunc<Array<int, 2>> の IFunc の最初の型引数
                    for (int k = 0; k + 4 < tokens.Length && head is null; k++)
                    {
                        if (tokens[k].Text is "struct" or "class" && tokens[k + 1].Text == type)
                        {
                            for (int m = k + 2; m + 2 < tokens.Length && tokens[m].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); m++)
                            {
                                if (tokens[m].Text == "IFunc" && tokens[m + 1].Kind == ShaderSyntaxKind.LessThanToken && tokens[m + 2].Kind == ShaderSyntaxKind.IdentifierToken)
                                {
                                    head = tokens[m + 2].Text;
                                    break;
                                }
                            }
                        }
                    }
                }

                return head is not null && (head == "Array" || (IsStruct(head) && !root.TypeAliases.ContainsKey(head))) ? head : null;
            }

            void CheckNotInferred(Generic generic, InvocationExpressionSyntax call)
            {
                if (call.HasTypeArguments || call.Target is not IdentifierExpressionSyntax target || generic.Mentioned.Length != generic.Parameters.Length)
                {
                    return;
                }

                for (int i = 0; i < generic.Parameters.Length; i++)
                {
                    if (!generic.Mentioned[i] && (i >= generic.Defaults.Length || generic.Defaults[i] is null)
                        && compilation.IsReportable(target.Identifier) && target.Identifier.GetLocation() is { } location)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.GenericArgumentNotInferred, location,
                            $"{generic.Name} の型の仮引数 {generic.Parameters[i]} は、実引数から決まりません。{generic.Name}<…>(…) と型引数を書いてください。"));
                        return;
                    }
                }
            }

            CheckInterfaceConstraints();
            CheckOutputInterfaceArguments();
            CheckAnyValueSizes();
            CheckRecursiveGenericConstants();

            // f(concrete) の f が inout IThing x (インターフェイスの型の out・inout の仮引数に、具体的な型の値を渡す。E30077)。
            void CheckOutputInterfaceArguments()
            {
                foreach (InvocationExpressionSyntax call in root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                {
                    if (call.HasTypeArguments || call.TargetName is not { } name || compilation.GetFunctionDeclarations(name, program) is not [var declaration]
                        || !declaration.GenericParameterTokens.IsDefaultOrEmpty)
                    {
                        continue;
                    }

                    ParameterSyntax[] parameters = [.. declaration.ParameterList];
                    ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];

                    if (parameters.Length != arguments.Length)
                    {
                        continue;
                    }

                    ExpressionTypeEvaluator? evaluator = null;

                    for (int i = 0; i < parameters.Length; i++)
                    {
                        ParameterSyntax parameter = parameters[i];

                        if (!parameter.ModifierTokens.Any(m => m.Text is "out" or "inout") || parameter.IsArray || parameter.Type.OpenAngle is not null
                            || !interfaces.TryGetValue(parameter.Type.Name, out HashSet<string>? known) || known is null)
                        {
                            continue;
                        }

                        evaluator ??= binder.GetEvaluatorFor(call);

                        if (evaluator.Evaluate(arguments[i]) is { } argument && IsStruct(argument) && arguments[i].FirstToken is { } at
                            && compilation.IsReportable(at) && at.GetLocation() is { } location)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.ArgumentNotConvertible, location, name, i + 1, argument, parameter.Type.Name,
                                $"インターフェイスの型の {string.Join(" ", parameter.ModifierTokens.Select(m => m.Text))} の仮引数には、具体的な型の値を渡せません。"
                                + $"インターフェイスの型の値を渡すか、仮引数を T {parameter.Name} と where T : {parameter.Type.Name} のジェネリックにしてください。"));
                        }
                    }
                }
            }

            // [anyValueSize(8)] interface I に適合する構造体の大きさ (E41019)。フィールドが 4 バイトのスカラー・ベクトルとその配列だけの構造体を見る。
            void CheckAnyValueSizes()
            {
                Dictionary<string, int> limits = new(StringComparer.Ordinal);

                // [anyValueSize(8)] interface I (属性の ] のすぐ後ろの interface)
                for (int i = 0; i + 6 < tokens.Length; i++)
                {
                    if (tokens[i].Text == "anyValueSize" && tokens[i + 1].Kind == ShaderSyntaxKind.OpenParenToken && tokens[i + 2].Kind == ShaderSyntaxKind.NumericLiteralToken
                        && tokens[i + 3].Kind == ShaderSyntaxKind.CloseParenToken && tokens[i + 4].Kind == ShaderSyntaxKind.CloseBracketToken
                        && tokens[i + 5].Text == "interface" && tokens[i + 6].Kind == ShaderSyntaxKind.IdentifierToken && int.TryParse(tokens[i + 2].Text, out int limit))
                    {
                        limits[tokens[i + 6].Text] = limits.ContainsKey(tokens[i + 6].Text) ? -1 : limit;
                    }
                }

                if (limits.Count == 0)
                {
                    return;
                }

                // 大きさは、インターフェイスの型の値を作るコード (エントリポイントから届く関数か、グローバルの宣言) があるときに見る。
                FunctionDeclarationSyntax[] entries = [.. root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>()
                    .Where(f => f.Body is not null && f.Attributes.Any(a => a.Name is "shader" or "numthreads"))];
                HashSet<FunctionDeclarationSyntax> reachable = [.. entries];

                foreach (FunctionDeclarationSyntax entry in entries)
                {
                    reachable.UnionWith(compilation.EnumerateCallees(entry, program, int.MaxValue).Select(c => c.Function));
                }

                HashSet<string> used = new(StringComparer.Ordinal);

                foreach (FunctionDeclarationSyntax function in reachable)
                {
                    used.UnionWith(function.DescendantTokens().Where(t => limits.ContainsKey(t.Text)).Select(t => t.Text));
                }

                foreach (VariableDeclarationSyntax global in root.Declarations.OfType<VariableDeclarationSyntax>())
                {
                    used.UnionWith(global.DescendantTokens().Where(t => limits.ContainsKey(t.Text)).Select(t => t.Text));
                }

                foreach (string unused in limits.Keys.Where(k => !used.Contains(k)).ToList())
                {
                    limits.Remove(unused);
                }

                foreach (StructDeclarationSyntax declared in root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
                {
                    if (declared.Keyword.Text is not ("struct" or "class") || declared.NameToken is not { } structName || extended.Contains(structName.Text)
                        || !types.TryGetValue(structName.Text, out HashSet<string>? bases) || bases is null
                        || bases.Where(limits.ContainsKey).Select(b => limits[b]).DefaultIfEmpty(-1).Min() is not (>= 0 and var limit))
                    {
                        continue;
                    }

                    int size = 0;
                    bool known = true;

                    foreach (VariableDeclarationSyntax field in declared.Members.OfType<VariableDeclarationSyntax>())
                    {
                        if (field.ModifierTokens.Any(m => m.Text is "static" or "const"))
                        {
                            continue;
                        }

                        int components = binder.TypeSystem.TryDescribeNumeric(field.Type.Name, out ShaderNumericShape shape) && shape.Rows == 1
                                         && field.Type.Name.TrimEnd('1', '2', '3', '4') is "int" or "uint" or "float" or "dword"
                            ? shape.Columns
                            : 0;

                        foreach (VariableDeclaratorSyntax variable in field.Variables)
                        {
                            int count = 1;

                            if (variable.IsArray && !(variable.ArrayDimensions == 1 && variable.TryGetArrayLength(out count)))
                            {
                                known = false;
                            }

                            size += 4 * components * count;
                        }

                        known &= components > 0;
                    }

                    if (known && size > limit && compilation.IsReportable(structName) && structName.GetLocation() is { } location)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.InterfaceRequirement, location,
                            $"{structName.Text} の大きさ ({size} バイト) が、インターフェイスの [anyValueSize({limit})] を超えています。"));
                    }
                }
            }

            // struct Loop<let x : int> { static const int value = Loop<x + 1>::value; } を、Loop<0>::value で使う (E39998。値を求めるのが終わらない)。
            void CheckRecursiveGenericConstants()
            {
                foreach ((string name, Generic? generic) in structs)
                {
                    if (generic is null)
                    {
                        continue;
                    }

                    for (int i = 0; i + 4 < tokens.Length; i++)
                    {
                        // static const int value = …; の初期値の中の name<…>::member (同じ構造体の中)
                        if (tokens[i].Text != "static" || tokens[i + 1].Text != "const")
                        {
                            continue;
                        }

                        int equals = i + 2;

                        while (equals < tokens.Length && tokens[equals].Kind is not (ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken))
                        {
                            equals++;
                        }

                        if (equals + 1 >= tokens.Length || tokens[equals].Kind != ShaderSyntaxKind.EqualsToken || Owner(i) != name)
                        {
                            continue;
                        }

                        int end = equals + 1;

                        while (end < tokens.Length && tokens[end].Kind != ShaderSyntaxKind.SemicolonToken)
                        {
                            end++;
                        }

                        // 条件で止めるもの (? :) は見ない。
                        if (tokens.Skip(equals + 1).Take(end - equals - 1).Any(t => t.Kind == ShaderSyntaxKind.QuestionToken))
                        {
                            continue;
                        }

                        // name<…>::constant (定義している定数そのものを、同じ構造体の別の特殊化から読む)
                        string constant = tokens[equals - 1].Text;
                        ShaderSyntaxToken? self = null;

                        for (int k = equals + 1; k + 1 < end && self is null; k++)
                        {
                            if (tokens[k].Text != name || tokens[k + 1].Kind != ShaderSyntaxKind.LessThanToken)
                            {
                                continue;
                            }

                            int depth = 0;
                            int m = k + 1;

                            for (; m < end; m++)
                            {
                                depth += tokens[m].Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, _ => 0 };

                                if (depth == 0)
                                {
                                    break;
                                }
                            }

                            if (m + 2 < end + 1 && tokens[m + 1].Kind is ShaderSyntaxKind.ColonColonToken or ShaderSyntaxKind.DotToken && tokens[m + 2].Text == constant)
                            {
                                self = tokens[k];
                            }
                        }

                        if (self is null)
                        {
                            continue;
                        }

                        // 構造体の外での name<…>:: の使用があるときだけ (使わなければ値を求めない)。
                        bool used = false;

                        for (int k = 0; k + 1 < tokens.Length && !used; k++)
                        {
                            used = tokens[k].Text == name && tokens[k + 1].Kind == ShaderSyntaxKind.LessThanToken && Owner(k) != name
                                   && tokens[k - 1 < 0 ? 0 : k - 1].Text is not ("struct" or "class")
                                   && tokens.Skip(k + 1).TakeWhile(t => t.Kind is not (ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken)).Any(t => t.Kind == ShaderSyntaxKind.ColonColonToken);
                        }

                        if (used && compilation.IsReportable(self) && self.GetLocation() is { } location)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.CyclicConstant, location,
                                $"static const の初期値が、同じ構造体 {name} の別の型引数の値をたどり続けるので、値が決まりません (終わりの条件が書けません)。"));
                        }
                    }
                }
            }

            // 位置 index の、すぐ外の struct の名前。
            string? Owner(int index)
            {
                int depth = 0;

                for (int k = index; k > 0; k--)
                {
                    if (tokens[k].Kind == ShaderSyntaxKind.CloseBraceToken)
                    {
                        depth++;
                    }
                    else if (tokens[k].Kind == ShaderSyntaxKind.OpenBraceToken && --depth < 0)
                    {
                        for (int j = k - 1; j > 0 && tokens[j].Kind is not (ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.OpenBraceToken); j--)
                        {
                            if (tokens[j - 1].Text is "struct" or "class")
                            {
                                return tokens[j].Text;
                            }
                        }

                        return null;
                    }
                }

                return null;
            }

            // インターフェイスの __constraint A == This と、関連型を型引数にしたジェネリックのインターフェイス (associatedtype First : IConsumer<Context>)。
            void CheckInterfaceConstraints()
            {
                Dictionary<string, List<string>> thisConstraints = new(StringComparer.Ordinal);

                for (int i = 0; i + 2 < tokens.Length; i++)
                {
                    if (tokens[i].Text != "interface" || tokens[i + 1].Kind != ShaderSyntaxKind.IdentifierToken || !interfaces.ContainsKey(tokens[i + 1].Text))
                    {
                        continue;
                    }

                    int open = i + 2;

                    while (open < tokens.Length && tokens[open].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
                    {
                        open++;
                    }

                    if (open >= tokens.Length || tokens[open].Kind != ShaderSyntaxKind.OpenBraceToken)
                    {
                        continue;
                    }

                    // 本体の深さ 1 の associatedtype と、その制約。読めない制約のあるものは null。
                    Dictionary<string, List<string>?> associated = new(StringComparer.Ordinal);
                    List<(ShaderSyntaxToken Generic, string Argument)> uses = [];
                    int depth = 0;

                    for (int k = open; k < tokens.Length; k++)
                    {
                        if (tokens[k].Kind == ShaderSyntaxKind.OpenBraceToken)
                        {
                            depth++;
                        }
                        else if (tokens[k].Kind == ShaderSyntaxKind.CloseBraceToken && --depth == 0)
                        {
                            break;
                        }

                        if (depth != 1)
                        {
                            continue;
                        }

                        // __constraint DataType == This;
                        if (tokens[k].Text == "__constraint" && k + 4 < tokens.Length && tokens[k + 1].Kind == ShaderSyntaxKind.IdentifierToken
                            && tokens[k + 2].Kind == ShaderSyntaxKind.EqualsEqualsToken && tokens[k + 3].Text == "This" && tokens[k + 4].Kind == ShaderSyntaxKind.SemicolonToken)
                        {
                            if (!thisConstraints.TryGetValue(tokens[i + 1].Text, out List<string>? list))
                            {
                                thisConstraints[tokens[i + 1].Text] = list = [];
                            }

                            list.Add(tokens[k + 1].Text);
                        }

                        if (tokens[k].Text != "associatedtype" || k + 1 >= tokens.Length || tokens[k + 1].Kind != ShaderSyntaxKind.IdentifierToken)
                        {
                            continue;
                        }

                        string name = tokens[k + 1].Text;
                        List<string>? bounds = [];
                        int m = k + 2;

                        if (m < tokens.Length && tokens[m].Kind == ShaderSyntaxKind.ColonToken)
                        {
                            for (m++; m < tokens.Length && tokens[m].Kind is not ShaderSyntaxKind.SemicolonToken && tokens[m].Text != "where"; m++)
                            {
                                if (tokens[m].Kind != ShaderSyntaxKind.IdentifierToken || tokens[m - 1].Kind is not (ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.AmpersandToken))
                                {
                                    continue;
                                }

                                // IConsumer<Context> (ジェネリックのインターフェイスに、関連型 1 つを渡すもの)
                                if (m + 3 < tokens.Length && tokens[m + 1].Kind == ShaderSyntaxKind.LessThanToken
                                    && tokens[m + 2].Kind == ShaderSyntaxKind.IdentifierToken && tokens[m + 3].Kind == ShaderSyntaxKind.GreaterThanToken)
                                {
                                    uses.Add((tokens[m], tokens[m + 2].Text));
                                    bounds = null;
                                }
                                else if (bounds is not null && !(m + 1 < tokens.Length && tokens[m + 1].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken))
                                {
                                    bounds.Add(tokens[m].Text);
                                }
                                else
                                {
                                    bounds = null;
                                }
                            }
                        }

                        // where の制約は読まない (決めない)。
                        if (m < tokens.Length && tokens[m].Text == "where")
                        {
                            bounds = null;
                        }

                        associated[name] = associated.ContainsKey(name) ? null : bounds;
                    }

                    foreach ((ShaderSyntaxToken genericToken, string argument) in uses)
                    {
                        if (!genericInterfaces.TryGetValue(genericToken.Text, out Generic? target) || target is not { Parameters.Length: 1 }
                            || !associated.TryGetValue(argument, out List<string>? bounds) || bounds is null)
                        {
                            continue;
                        }

                        foreach (string requirement in target.Constraints[0])
                        {
                            if (interfaces.TryGetValue(requirement, out HashSet<string>? known) && known is not null && !model.ConformsThrough(bounds, requirement)
                                && compilation.IsReportable(genericToken) && genericToken.GetLocation() is { } location)
                            {
                                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ConstraintNotSatisfied, location,
                                    $"関連型 {argument} の制約から、インターフェイス {requirement} に届かないので、{target.Name} の型の仮引数 {target.Parameters[0]} にできません。"
                                    + $"associatedtype {argument} : {requirement} と書いてください。"));
                                break;
                            }
                        }
                    }
                }

                if (thisConstraints.Count == 0)
                {
                    return;
                }

                // struct Bad : IDerived (IDerived の __constraint DataType == This に、typealias DataType = int で合わない)
                for (int i = 0; i + 2 < tokens.Length; i++)
                {
                    if (tokens[i].Text is not ("struct" or "class") || tokens[i + 1].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i + 2].Kind != ShaderSyntaxKind.ColonToken)
                    {
                        continue;
                    }

                    string type = tokens[i + 1].Text;

                    if (extended.Contains(type) || !IsStruct(type))
                    {
                        continue;
                    }

                    for (int k = i + 3; k < tokens.Length && tokens[k].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken) && tokens[k].Text != "where"; k++)
                    {
                        if (tokens[k].Kind != ShaderSyntaxKind.IdentifierToken || tokens[k - 1].Kind is not (ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.CommaToken))
                        {
                            continue;
                        }

                        foreach ((string owner, List<string> members) in thisConstraints)
                        {
                            if (!Derives(interfaces, tokens[k].Text, owner, 0))
                            {
                                continue;
                            }

                            foreach (string member in members)
                            {
                                if (aliases.TryGetValue((type, member), out string? actual) && actual != type && actual != "This" && (IsStruct(actual) || IsNumber(actual))
                                    && compilation.IsReportable(tokens[k]) && tokens[k].GetLocation() is { } location)
                                {
                                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ConstraintNotSatisfied, location,
                                        $"{type}.{member} は {actual} なので、インターフェイス {owner} の制約 __constraint {member} == This を満たしません ({member} は {type} にしてください)。"));
                                }
                            }
                        }
                    }
                }
            }
            // インターフェイスの本体のメソッドの戻り値の型と関連型、どの型にも当たる extension のメソッド、ジェネリックのメソッドの形。
            (Dictionary<string, Dictionary<string, string?>> returns, Dictionary<string, Dictionary<string, string?>> associatedTypes) = CollectInterfaceMembers(tokens, interfaces);
            (HashSet<string> blanketMethods, HashSet<string> declaredMethods) = CollectExtensionMethods(tokens);

            // ジェネリックでないメソッドと関数の名前 (R name( の形の宣言)。
            HashSet<string> nonGenericMethods = [.. tokens.Select((t, k) => (t, k))
                .Where(x => x.k > 0 && x.k + 1 < tokens.Length && x.t.Kind == ShaderSyntaxKind.IdentifierToken && tokens[x.k + 1].Kind == ShaderSyntaxKind.OpenParenToken
                            && tokens[x.k - 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[x.k - 1].Text is not ("return" or "else" or "case" or "new" or "throw" or "in" or "out" or "inout"))
                .Select(x => x.t.Text)];
            Dictionary<string, List<(List<string> TypeParameters, List<string?> ParameterTypes, List<List<string>> Constraints)>> shapes = CollectGenericShapes(tokens);

            // ジェネリックの関数も型も、どの型にも当たる extension のメソッドも、Load の呼び出しも、標準ライブラリの制約のあるジェネリックの型も無ければ、見る使い方が無い。
            if (functions.Count == 0 && structs.Count == 0 && blanketMethods.Count == 0 && !tokens.Any(t => t.Text == "Load" || SlangStandardConformance.Instance.Generics.ContainsKey(t.Text)))
            {
                continue;
            }

            bool IsUserInterface(string? name) => name is not null && interfaces.TryGetValue(name, out HashSet<string>? b) && b is not null;

            // 値の型が存在型 (インターフェイスの型) か。インターフェイスそのものなら (I, false)、インターフェイスの関連型なら (関連型の最初の制約か空, true)。
            (string Interface, bool Associated)? Existential(ExpressionSyntax expression, ExpressionTypeEvaluator evaluator, int depth)
            {
                if (depth > 8)
                {
                    return null;
                }

                switch (expression)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        return Existential(parenthesized.Expression, evaluator, depth + 1);

                    // let x = …・IFoo x = … (右辺の値の出どころ)。インターフェイスの型の仮引数は、#lang slang 2025 ではジェネリック (some IFoo) と同じなので、存在型でない。
                    case IdentifierExpressionSyntax identifier
                        when binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax { Initializer: { } initializer } declarator }]
                             && declarator.Ancestors().OfType<VariableDeclarationSyntax>().FirstOrDefault() is { } declaration
                             && (declaration.Type.Name is "let" or "var" || IsUserInterface(declaration.Type.Name)):
                        return Existential(initializer, evaluator, depth + 1);

                    // IFoo obj (インターフェイスの型の仮引数)。呼び出しのどれかが、どの型か決まらない値を渡すとき。
                    case IdentifierExpressionSyntax parameterUse
                        when binder.ResolveName(parameterUse).Candidates is [{ Declarator: ParameterSyntax parameter }] && IsUserInterface(parameter.Type.Name) && parameter.Type.OpenAngle is null
                             && parameter.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is { } owner
                             && compilation.GetFunctionDeclarations(owner.Name, program) is [_]
                             && owner.ParameterList.ToList().IndexOf(parameter) is var position and >= 0:
                        foreach (InvocationExpressionSyntax caller in root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Where(c => c.TargetName == owner.Name && !c.HasTypeArguments))
                        {
                            if (caller.ArgumentExpressions.ElementAtOrDefault(position) is { } passed && Existential(passed, binder.GetEvaluatorFor(caller), depth + 1) is ({ } passedType, false))
                            {
                                return (passedType, false);
                            }
                        }

                        return null;

                    // createDynamicObject<IFoo>(id, data)
                    case InvocationExpressionSyntax { Target: GenericNameExpressionSyntax { Name: "createDynamicObject" } created } when SimpleArguments(created.TypeArguments) is [{ } dynamicType] && IsUserInterface(dynamicType):
                        return (dynamicType, false);

                    case InvocationExpressionSyntax { TargetName: "createDynamicObject", HasTypeArguments: true } createdCall when SimpleArguments(createdCall.TypeArgumentTokens) is [{ } createdType] && IsUserInterface(createdType):
                        return (createdType, false);

                    // factory(id) (インターフェイスの型を返す関数で、2 つ以上の違う型の値を返すもの。1 つだけなら、slangc はその型に決める)
                    case InvocationExpressionSyntax { TargetName: { } factoryName, HasTypeArguments: false } factory
                        when compilation.GetFunctionDeclarations(factoryName, program) is [{ Body: { } factoryBody } factoryDeclaration]
                             && IsUserInterface(factoryDeclaration.ReturnType.Name) && factoryDeclaration.ReturnType.OpenAngle is null:
                        HashSet<string> returned = new(StringComparer.Ordinal);
                        ExpressionTypeEvaluator inner = binder.GetEvaluatorFor(factoryBody);

                        foreach (ReturnStatementSyntax statement in factoryBody.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>())
                        {
                            if (statement.Expression is not { } value || inner.Evaluate(value) is not { } valueType || !IsStruct(valueType))
                            {
                                return null;
                            }

                            returned.Add(valueType);
                        }

                        return returned.Count >= 2 ? (factoryDeclaration.ReturnType.Name, false) : null;

                    // obj.method(…) と obj.Config() (存在型のメソッドの戻り値の関連型と、関連型の値の作成)
                    case InvocationExpressionSyntax { Target: MemberAccessExpressionSyntax member, HasTypeArguments: false }
                        when Existential(member.Target, evaluator, depth + 1) is ({ Length: > 0 } owner, false):
                        if (associatedTypes.TryGetValue(owner, out Dictionary<string, string?>? owned))
                        {
                            if (owned.TryGetValue(member.Name, out string? constructed))
                            {
                                return (constructed ?? string.Empty, true);
                            }

                            if (returns.TryGetValue(owner, out Dictionary<string, string?>? methods) && methods.TryGetValue(member.Name, out string? result) && result is not null)
                            {
                                if (owned.TryGetValue(result, out string? bound))
                                {
                                    return (bound ?? string.Empty, true);
                                }

                                return IsUserInterface(result) ? (result, false) : null;
                            }
                        }

                        return null;

                    // 関連型の値 (inner.make() の inner が、制約 IInner の関連型)
                    case InvocationExpressionSyntax { Target: MemberAccessExpressionSyntax member, HasTypeArguments: false }
                        when Existential(member.Target, evaluator, depth + 1) is ({ Length: > 0 } bound, true) && IsUserInterface(bound):
                        if (returns.TryGetValue(bound, out Dictionary<string, string?>? boundMethods) && boundMethods.TryGetValue(member.Name, out string? boundResult) && boundResult is not null
                            && associatedTypes.TryGetValue(bound, out Dictionary<string, string?>? boundOwned) && boundOwned.TryGetValue(boundResult, out string? nested))
                        {
                            return (nested ?? string.Empty, true);
                        }

                        return null;
                }

                return null;
            }

            // エントリポイント ([shader]・[numthreads] の関数) から届く関数の中か。
            HashSet<FunctionDeclarationSyntax>? reachableFunctions = null;

            bool Reachable(ShaderSyntaxNode site)
            {
                if (reachableFunctions is null)
                {
                    FunctionDeclarationSyntax[] entries = [.. root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>()
                        .Where(f => f.Body is not null && f.Attributes.Any(a => a.Name is "shader" or "numthreads"))];
                    reachableFunctions = [.. entries];

                    foreach (FunctionDeclarationSyntax entry in entries)
                    {
                        reachableFunctions.UnionWith(compilation.EnumerateCallees(entry, program, int.MaxValue).Select(c => c.Function));
                    }
                }

                return site.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is { } function && reachableFunctions.Contains(function);
            }

            // 存在型の値で特殊化するメソッドの呼び出し (E33180)。
            void CheckExistentialMethodCall(InvocationExpressionSyntax call, MemberAccessExpressionSyntax member)
            {
                ExpressionTypeEvaluator evaluator = binder.GetEvaluatorFor(call);
                string? message = null;

                // d.doThing(W()) (どのジェネリックのメソッドの宣言も、型の仮引数の制約に W が適合しない。E39999)。
                // W は、適合するものの決まった利用者の構造体だけ。同じ名前のジェネリックでないメソッドがあれば見ない。
                if (message is null && !call.HasTypeArguments && shapes.TryGetValue(member.Name, out var generics) && !nonGenericMethods.Contains(member.Name)
                    && blanket.Count == 0)
                {
                    ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];

                    for (int i = 0; i < arguments.Length; i++)
                    {
                        if (evaluator.Evaluate(arguments[i]) is not { } argument || !IsStruct(argument) || extended.Contains(argument)
                            || !generics.All(g => g.ParameterTypes.Count == arguments.Length && g.ParameterTypes[i] is { } p && g.TypeParameters.IndexOf(p) is var at and >= 0
                                                  && g.Constraints[at].Count > 0 && g.Constraints[at].Any(c => model.Conforms(argument, c) == false)))
                        {
                            continue;
                        }

                        if (arguments[i].FirstToken is { } first && compilation.IsReportable(first) && first.GetLocation() is { } argumentLocation)
                        {
                            context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.ArgumentNotConvertible, argumentLocation, member.Name, i + 1, argument,
                                string.Join(" / ", generics.Select(g => g.ParameterTypes[i]).Distinct()),
                                $"どの宣言の型の仮引数の制約 ({string.Join("・", generics.SelectMany(g => g.Constraints[g.TypeParameters.IndexOf(g.ParameterTypes[i]!)]).Distinct())}) にも、{argument} は適合しません。"));
                        }

                        return;
                    }
                }


                // buffer.Load<IFoo>(0) (ByteAddressBuffer から存在型は読めない。言語のバージョンによらない)。
                // 特殊化はエントリポイント ([shader]・[numthreads] の関数) から届く関数を作るときに起きるので、届く関数の中だけを見る。
                if (call.HasTypeArguments && member.Name == "Load" && SimpleArguments(call.TypeArgumentTokens) is [{ } loaded] && IsUserInterface(loaded)
                    && evaluator.Evaluate(member.Target) is "ByteAddressBuffer" or "RWByteAddressBuffer"
                    && Reachable(call))
                {
                    message = $"インターフェイスの型 {loaded} では、Load を特殊化できません。具体的な型で読み込んでください。";
                }
                else if (!versioned || !Reachable(call))
                {
                    return;
                }

                // b.eval<IFoo>(df) (ジェネリックのメソッドに、インターフェイスの型を型引数に書く)
                else if (call.HasTypeArguments && shapes.ContainsKey(member.Name) && SimpleArguments(call.TypeArgumentTokens).FirstOrDefault(IsUserInterface) is { } written)
                {
                    message = $"インターフェイスの型 {written} では、{member.Name} の型の仮引数を特殊化できません。具体的な型を渡してください。";
                }

                // b.eval(df) (ジェネリックのメソッドの型の仮引数に、存在型の値が当たる)
                else if (!call.HasTypeArguments && shapes.TryGetValue(member.Name, out var candidates))
                {
                    ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];

                    for (int i = 0; i < arguments.Length && message is null; i++)
                    {
                        if (candidates.All(c => c.ParameterTypes.Count == arguments.Length && c.ParameterTypes[i] is { } p && c.TypeParameters.Contains(p))
                            && Existential(arguments[i], evaluator, 0) is ({ } kind, false))
                        {
                            message = $"インターフェイスの型 {kind} の値では、{member.Name} の型の仮引数を特殊化できません。具体的な型の値を渡してください。";
                        }
                    }
                }

                // obj.calcTwice(y) (どの型にも当たる extension のメソッドを、存在型の値で呼ぶ)
                if (message is null && blanketMethods.Contains(member.Name) && !declaredMethods.Contains(member.Name)
                    && Existential(member.Target, evaluator, 0) is ({ } receiver, false))
                {
                    message = $"extension のメソッド {member.Name} は、インターフェイスの型 {receiver} の値では呼べません (型を特殊化できません)。具体的な型の値で呼んでください。";
                }

                if (message is not null && compilation.IsReportable(member.NameToken) && member.NameToken.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ConstraintNotSatisfied, location, message));
                }
            }

            // genericFunc(obj, 3.0) と process(result) の obj・result が、存在型の値か存在型の関連型の値 (E33180)。
            void CheckExistentialFunctionCall(Generic generic, InvocationExpressionSyntax call)
            {
                if (!versioned || call.HasTypeArguments || call.Target is not IdentifierExpressionSyntax target
                    || !shapes.TryGetValue(generic.Name, out var candidates) || candidates is not [var shape] || !Reachable(call))
                {
                    return;
                }

                ExpressionTypeEvaluator evaluator = binder.GetEvaluatorFor(call);
                ExpressionSyntax[] arguments = [.. call.ArgumentExpressions];

                for (int i = 0; i < arguments.Length && i < shape.ParameterTypes.Count; i++)
                {
                    if (shape.ParameterTypes[i] is { } type && shape.TypeParameters.Contains(type)
                        && Existential(arguments[i], evaluator, 0) is ({ } kind, bool associated)
                        && compilation.IsReportable(target.Identifier) && target.Identifier.GetLocation() is { } location)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ConstraintNotSatisfied, location, associated
                            ? $"実引数の値の型は存在型の関連型 (インターフェイスの型の値のメソッドが返す関連型) なので、{generic.Name} の型の仮引数 {type} を特殊化できません。"
                            : $"実引数は、どの型か決まらないインターフェイスの型 {kind} の値なので、{generic.Name} の型の仮引数 {type} を特殊化できません。具体的な型の値を渡してください。"));
                        return;
                    }
                }
            }
            foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax { Target: MemberAccessExpressionSyntax member } methodCall:
                        CheckExistentialMethodCall(methodCall, member);
                        break;

                    case InvocationExpressionSyntax { TargetName: { } name } call
                        when functions.TryGetValue(name, out Generic? generic) && generic is not null:
                        if (call.HasTypeArguments)
                        {
                            CheckArguments(generic, SimpleArguments(call.TypeArgumentTokens), call.Target.FirstToken ?? call.OpenParen, call);
                            CheckSpecial(generic, SimpleArguments(call.TypeArgumentTokens), call.Target.FirstToken ?? call.OpenParen);
                        }
                        else if (Inferred(compilation, program, binder, call, generic, literals: !types.Keys.Any(t => binder.TypeSystem.TryDescribeNumeric(t, out _))) is { } inferred)
                        {
                            CheckArguments(generic, WithDefaults(generic, inferred), call.Target.FirstToken ?? call.OpenParen, call);
                        }

                        // リテラルは制約に合わせて型が変わりうるので、リテラルでない実引数から決めた型だけを見る。
                        if (!call.HasTypeArguments && !generic.Special.IsDefaultOrEmpty && Inferred(compilation, program, binder, call, generic, literals: false) is { } strict)
                        {
                            CheckSpecial(generic, strict, call.Target.FirstToken ?? call.OpenParen);
                        }

                        CheckNotInferred(generic, call);
                        CheckFunctionTypeInference(generic, call);
                        CheckConflicts(generic, call);
                        CheckExistentialFunctionCall(generic, call);

                        break;

                    // f<X>(…) (構文解析が型引数つきの名前の式にしたもの)
                    case InvocationExpressionSyntax { Target: GenericNameExpressionSyntax named } genericCall
                        when functions.TryGetValue(named.Name, out Generic? explicitGeneric) && explicitGeneric is not null:
                        CheckArguments(explicitGeneric, SimpleArguments(named.TypeArguments), named.Identifier, genericCall);
                        CheckSpecial(explicitGeneric, SimpleArguments(named.TypeArguments), named.Identifier);
                        break;

                    case TypeSyntax type when type.OpenAngle is not null
                                              && structs.TryGetValue(type.NameToken.Text, out Generic? generic) && generic is not null
                                              && !type.TemplateArguments.IsDefaultOrEmpty:
                        CheckArguments(generic, SimpleArguments(type.TemplateArguments), type.NameToken, type);
                        CheckAssociated(generic, SimpleArguments(type.TemplateArguments), type.NameToken);
                        break;

                    // DescriptorHandle<ParameterBlock<U>> (標準ライブラリのジェネリックの型の、型の仮引数の制約)
                    case TypeSyntax type when type.OpenAngle is not null && !type.TemplateArguments.IsDefaultOrEmpty
                                              && !structs.ContainsKey(type.NameToken.Text) && !interfaces.ContainsKey(type.NameToken.Text):
                        List<string?> standardArguments = SimpleArguments(type.TemplateArguments);

                        foreach ((int index, string standardParameter, string constraint) in model.StandardConstraints(type.NameToken.Text))
                        {
                            if (index < standardArguments.Count && standardArguments[index] is { } standardArgument && Violates(standardArgument, constraint))
                            {
                                Report(type.NameToken, standardArgument, constraint, standardParameter, type.NameToken.Text);
                                break;
                            }
                        }

                        break;

                    // GenStruct gs (型引数を書かない、既定の型引数を使う型)。既定の型引数が制約に合わなければ、既定の値の位置で伝える。
                    case TypeSyntax { OpenAngle: null } bare when structs.TryGetValue(bare.NameToken.Text, out Generic? defaulted) && defaulted is not null
                                                                  && defaulted.Defaults.Length == defaulted.Parameters.Length && defaulted.Defaults.All(d => d is not null):
                        for (int d = 0; d < defaulted.Parameters.Length; d++)
                        {
                            ShaderSyntaxToken defaultToken = defaulted.Defaults[d]!;

                            if (defaulted.Constraints[d].FirstOrDefault(r => Violates(defaultToken.Text, r)) is { } broken)
                            {
                                Report(defaultToken, defaultToken.Text, broken, defaulted.Parameters[d], defaulted.Name);
                            }
                        }

                        break;
                }
            }
        }
    }

    /// <summary>
    /// 構造体の本体の深さ 1 の関連型 (<c>typealias Element = X;</c>・<c>typedef X Element;</c>) と、本体の無い extern の構造体 (<c>extern struct S : I;</c>)。
    /// 2 度宣言した型の関連型は読まない。
    /// </summary>
    private static (Dictionary<(string, string), string> Aliases, HashSet<string> Externs) CollectMembers(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        Dictionary<(string, string), string> aliases = [];
        HashSet<string> externs = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> twice = new(StringComparer.Ordinal);

        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Text is not ("struct" or "class") || tokens[i + 1].Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            string type = tokens[i + 1].Text;

            if (!seen.Add(type))
            {
                twice.Add(type);
            }

            int open = i + 2;

            while (open < tokens.Length && tokens[open].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
            {
                open++;
            }

            if (open >= tokens.Length)
            {
                continue;
            }

            if (tokens[open].Kind == ShaderSyntaxKind.SemicolonToken)
            {
                if (i > 0 && tokens[i - 1].Text == "extern")
                {
                    externs.Add(type);
                }

                continue;
            }

            int depth = 0;

            for (int k = open; k + 3 < tokens.Length; k++)
            {
                if (tokens[k].Kind == ShaderSyntaxKind.OpenBraceToken)
                {
                    depth++;
                }
                else if (tokens[k].Kind == ShaderSyntaxKind.CloseBraceToken && --depth == 0)
                {
                    break;
                }

                if (depth != 1)
                {
                    continue;
                }

                if (tokens[k].Text == "typealias" && tokens[k + 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[k + 2].Kind == ShaderSyntaxKind.EqualsToken
                    && tokens[k + 3].Kind == ShaderSyntaxKind.IdentifierToken && k + 4 < tokens.Length && tokens[k + 4].Kind == ShaderSyntaxKind.SemicolonToken)
                {
                    aliases[(type, tokens[k + 1].Text)] = tokens[k + 3].Text;
                }
                else if (tokens[k].Text == "typedef" && tokens[k + 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[k + 2].Kind == ShaderSyntaxKind.IdentifierToken
                         && tokens[k + 3].Kind == ShaderSyntaxKind.SemicolonToken)
                {
                    aliases[(type, tokens[k + 2].Text)] = tokens[k + 1].Text;
                }
            }
        }

        foreach ((string type, string member) in aliases.Keys.Where(k => twice.Contains(k.Item1)).ToList())
        {
            aliases.Remove((type, member));
        }

        externs.ExceptWith(twice);
        return (aliases, externs);
    }

    /// <summary>型の宣言の <c>where T.Element : I</c>・<c>where T.Element == X</c> を読む。</summary>
    private static ImmutableArray<(int Index, string Member, string Kind, string Target)> ReadAssociated(ImmutableArray<ShaderSyntaxToken> tokens, int start, List<string> parameters)
    {
        ImmutableArray<(int, string, string, string)>.Builder associated = ImmutableArray.CreateBuilder<(int, string, string, string)>();

        for (int i = start; i + 5 < tokens.Length && tokens[i].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); i++)
        {
            if (tokens[i].Text == "where" && parameters.IndexOf(tokens[i + 1].Text) is var index and >= 0 && tokens[i + 2].Kind == ShaderSyntaxKind.DotToken
                && tokens[i + 3].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 4].Kind is ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.EqualsEqualsToken
                && tokens[i + 5].Kind == ShaderSyntaxKind.IdentifierToken
                && !(i + 6 < tokens.Length && tokens[i + 6].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken))
            {
                associated.Add((index, tokens[i + 3].Text, tokens[i + 4].Kind == ShaderSyntaxKind.ColonToken ? ":" : "==", tokens[i + 5].Text));
            }
        }

        return associated.ToImmutable();
    }
    /// <summary>
    /// 利用者のインターフェイスの本体の深さ 1 の、メソッドの名前と戻り値の型 (名前 1 つのもの) と、関連型の名前と最初の制約。2 度出てくる名前は null。
    /// </summary>
    private static (Dictionary<string, Dictionary<string, string?>> Returns, Dictionary<string, Dictionary<string, string?>> Associated) CollectInterfaceMembers(
        ImmutableArray<ShaderSyntaxToken> tokens, Dictionary<string, HashSet<string>?> interfaces)
    {
        Dictionary<string, Dictionary<string, string?>> returns = new(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, string?>> associated = new(StringComparer.Ordinal);

        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i].Text != "interface" || tokens[i + 1].Kind != ShaderSyntaxKind.IdentifierToken
                || !interfaces.TryGetValue(tokens[i + 1].Text, out HashSet<string>? bases) || bases is null)
            {
                continue;
            }

            int open = i + 2;

            while (open < tokens.Length && tokens[open].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
            {
                open++;
            }

            if (open >= tokens.Length || tokens[open].Kind != ShaderSyntaxKind.OpenBraceToken)
            {
                continue;
            }

            Dictionary<string, string?> methods = new(StringComparer.Ordinal);
            Dictionary<string, string?> types = new(StringComparer.Ordinal);
            returns[tokens[i + 1].Text] = methods;
            associated[tokens[i + 1].Text] = types;
            int depth = 0;

            for (int k = open; k + 2 < tokens.Length; k++)
            {
                if (tokens[k].Kind == ShaderSyntaxKind.OpenBraceToken)
                {
                    depth++;
                }
                else if (tokens[k].Kind == ShaderSyntaxKind.CloseBraceToken && --depth == 0)
                {
                    break;
                }

                if (depth != 1)
                {
                    continue;
                }

                if (tokens[k].Text == "associatedtype" && tokens[k + 1].Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    string? bound = tokens[k + 2].Kind == ShaderSyntaxKind.ColonToken && k + 3 < tokens.Length && tokens[k + 3].Kind == ShaderSyntaxKind.IdentifierToken
                                    && !(k + 4 < tokens.Length && tokens[k + 4].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken)
                        ? tokens[k + 3].Text
                        : null;
                    types[tokens[k + 1].Text] = types.ContainsKey(tokens[k + 1].Text) ? null : bound;
                    continue;
                }

                // R name( / R name<…>( (前が { ; ] } の、名前 2 つで始まる宣言)
                if (tokens[k].Kind == ShaderSyntaxKind.IdentifierToken && tokens[k + 1].Kind == ShaderSyntaxKind.IdentifierToken
                    && tokens[k + 2].Kind is ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.LessThanToken
                    && tokens[k - 1].Kind is ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBracketToken or ShaderSyntaxKind.CloseBraceToken)
                {
                    string name = tokens[k + 1].Text;
                    methods[name] = methods.ContainsKey(name) ? null : tokens[k].Text;
                }
            }
        }

        return (returns, associated);
    }

    /// <summary>
    /// どの型にも当たる extension (<c>extension&lt;T : I&gt; T { … }</c>) の本体のメソッドの名前と、それ以外の型・インターフェイス・extension の本体のメソッドの名前。
    /// </summary>
    private static (HashSet<string> Blanket, HashSet<string> Declared) CollectExtensionMethods(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        HashSet<string> blanket = new(StringComparer.Ordinal);
        HashSet<string> declared = new(StringComparer.Ordinal);

        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Text is not ("struct" or "class" or "interface" or "extension" or "__extension"))
            {
                continue;
            }

            // extension<T : I> T { (どの型にも当たる extension)
            bool everyType = false;
            int open = i + 1;

            if (tokens[i].Text is "extension" or "__extension" && tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken)
            {
                (List<string> parameters, _, int next) = ReadGenericParameters(tokens, i + 1);
                everyType = next < tokens.Length && parameters.Contains(tokens[next].Text);
                open = next;
            }

            while (open < tokens.Length && tokens[open].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
            {
                open++;
            }

            if (open >= tokens.Length || tokens[open].Kind != ShaderSyntaxKind.OpenBraceToken)
            {
                continue;
            }

            int depth = 0;

            for (int k = open; k + 2 < tokens.Length; k++)
            {
                if (tokens[k].Kind == ShaderSyntaxKind.OpenBraceToken)
                {
                    depth++;
                }
                else if (tokens[k].Kind == ShaderSyntaxKind.CloseBraceToken && --depth == 0)
                {
                    break;
                }

                if (depth == 1 && tokens[k].Kind == ShaderSyntaxKind.IdentifierToken && tokens[k + 1].Kind == ShaderSyntaxKind.IdentifierToken
                    && tokens[k + 2].Kind is ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.LessThanToken)
                {
                    (everyType ? blanket : declared).Add(tokens[k + 1].Text);
                }
            }
        }

        return (blanket, declared);
    }

    /// <summary>ジェネリックの関数とメソッドの、型の仮引数と、仮引数の型 (名前 1 つのもの。ほかは null) と、型の仮引数ごとの制約の有無。名前ごとの全部の宣言。</summary>
    private static Dictionary<string, List<(List<string> TypeParameters, List<string?> ParameterTypes, List<List<string>> Constraints)>> CollectGenericShapes(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        Dictionary<string, List<(List<string>, List<string?>, List<List<string>>)>> shapes = new(StringComparer.Ordinal);

        for (int i = 1; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i + 1].Kind != ShaderSyntaxKind.LessThanToken
                || tokens[i - 1].Kind != ShaderSyntaxKind.IdentifierToken
                || tokens[i - 1].Text is "return" or "case" or "else" or "throw" or "new" or "in" or "out" or "inout" or "typealias" or "typedef" or "struct" or "class" or "interface" or "extension"
                || tokens[i].Text is "struct" or "class" or "interface" or "extension")
            {
                continue;
            }

            (List<string> parameters, List<List<string>> constraints, int close) = ReadGenericParameters(tokens, i + 1);

            if (parameters.Count == 0 || close >= tokens.Length || tokens[close].Kind != ShaderSyntaxKind.OpenParenToken || Matching(tokens, close) is not (>= 0 and var end))
            {
                continue;
            }

            ReadWhere(tokens, end + 1, parameters, constraints);

            // 型引数のある制約 (U : IBase<T>) も、インターフェイスの名前として足す。
            for (int k = i + 2, index = -1, angle = 1; k < close && angle > 0; k++)
            {
                angle += tokens[k].Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, ShaderSyntaxKind.GreaterThanGreaterThanToken => -2, _ => 0 };

                if (angle == 1 && tokens[k].Kind == ShaderSyntaxKind.IdentifierToken && parameters.IndexOf(tokens[k].Text) is var found and >= 0
                    && tokens[k - 1].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.IdentifierToken)
                {
                    index = found;
                }
                else if (angle == 1 && index >= 0 && tokens[k].Kind == ShaderSyntaxKind.IdentifierToken && tokens[k - 1].Kind is ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.AmpersandToken
                         && !constraints[index].Contains(tokens[k].Text))
                {
                    constraints[index].Add(tokens[k].Text);
                }
            }

            List<string?> types = [];
            List<ShaderSyntaxToken> current = [];
            int depth = 0;

            for (int k = close + 1; k <= end; k++)
            {
                ShaderSyntaxKind kind = tokens[k].Kind;

                if ((kind == ShaderSyntaxKind.CommaToken && depth == 0) || k == end)
                {
                    if (current.Count > 0)
                    {
                        // 名前 2 つ (修飾子の後ろの型と仮引数の名前) だけのもの。
                        ShaderSyntaxToken[] names = [.. current.Where(t => t.Text is not ("in" or "out" or "inout" or "const" or "no_diff"))];
                        types.Add(names is [{ Kind: ShaderSyntaxKind.IdentifierToken } type, { Kind: ShaderSyntaxKind.IdentifierToken }] ? type.Text : null);
                    }

                    current.Clear();
                    continue;
                }

                depth += kind switch
                {
                    ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.OpenBracketToken => 1,
                    ShaderSyntaxKind.GreaterThanToken or ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.CloseBracketToken => -1,
                    _ => 0,
                };
                current.Add(tokens[k]);
            }

            if (!shapes.TryGetValue(tokens[i].Text, out var list))
            {
                shapes[tokens[i].Text] = list = [];
            }

            list.Add((parameters, types, constraints));
        }

        return shapes;
    }

    /// <summary>ファイル <paramref name="files"/> の extension の宣言を除いたトークン。</summary>
    private static ImmutableArray<ShaderSyntaxToken> WithoutExtensions(ImmutableArray<ShaderSyntaxToken> tokens, HashSet<string> files)
    {
        ImmutableArray<ShaderSyntaxToken>.Builder kept = ImmutableArray.CreateBuilder<ShaderSyntaxToken>(tokens.Length);

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is { Kind: ShaderSyntaxKind.IdentifierToken, Text: "extension" or "__extension" } && tokens[i].Source.FilePath is { } path
                && files.Contains(Path.GetFullPath(path)))
            {
                int depth = 0;

                for (; i < tokens.Length; i++)
                {
                    if (tokens[i].Kind == ShaderSyntaxKind.OpenBraceToken)
                    {
                        depth++;
                    }
                    else if ((tokens[i].Kind == ShaderSyntaxKind.CloseBraceToken && --depth == 0) || (depth == 0 && tokens[i].Kind == ShaderSyntaxKind.SemicolonToken))
                    {
                        break;
                    }
                }

                continue;
            }

            kept.Add(tokens[i]);
        }

        return kept.ToImmutable();
    }

    /// <summary>推論できなかった仮引数に、既定の型引数を当てる。</summary>
    private static List<string?> WithDefaults(Generic generic, List<string?> inferred)
    {
        List<string?> result = [.. inferred];

        for (int i = 0; i < result.Count && i < generic.Defaults.Length; i++)
        {
            result[i] ??= generic.Defaults[i]?.Text;
        }

        return result;
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
            else if (token.Kind == ShaderSyntaxKind.GreaterThanGreaterThanToken)
            {
                depth -= 2;
            }
            else if (token.Kind == ShaderSyntaxKind.CommaToken && depth == 0)
            {
                arguments.Add(Name(current));
                current.Clear();
                continue;
            }

            current.Add(token);
        }

        arguments.Add(Name(current));
        return arguments;

        // 名前 1 つ (S) か、適合を全部表に書いた標準ライブラリの型に型引数を書いたもの (RWBuffer<float>。適合は型引数に依らずに決まる分だけ見る)。
        static string? Name(List<ShaderSyntaxToken> type) => type switch
        {
            [{ Kind: ShaderSyntaxKind.IdentifierToken } single] => single.Text,
            // 呼び出しの型引数の最後の > は、>> で閉じると並びに入らない (f<Box<T>>(x))。
            [{ Kind: ShaderSyntaxKind.IdentifierToken } head, { Kind: ShaderSyntaxKind.LessThanToken }, ..]
                when SlangStandardConformance.Instance.Closed.ContainsKey(head.Text) => head.Text,
            _ => null,
        };
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
        HashSet<string> blanket,
        Dictionary<string, Generic?> genericInterfaces)
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
                List<ShaderSyntaxToken?> typeDefaults = [];
                (List<string> parameters, List<List<string>> constraints, j) = ReadGenericParameters(tokens, j, typeDefaults);

                // __generic<T : I = D> struct S (仮引数を型の名前の前に書いたもの)
                if (parameters.Count == 0 && token.Text is "struct" or "class" && i > 1 && tokens[i - 1].Kind == ShaderSyntaxKind.GreaterThanToken
                    && GenericBefore(tokens, i - 1) is { } genericOpen)
                {
                    (parameters, constraints, _) = ReadGenericParameters(tokens, genericOpen, typeDefaults);
                }

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

                        if (generic)
                        {
                            genericInterfaces[name] = genericInterfaces.ContainsKey(name) ? null : new Generic(name, [.. parameters], [.. constraints.Select(c => c.ToImmutableArray())]);
                        }

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
                            structs[name] = structs.ContainsKey(name)
                                ? null
                                : new Generic(name, [.. parameters], [.. constraints.Select(c => c.ToImmutableArray())]) { Defaults = [.. typeDefaults], Associated = ReadAssociated(tokens, j, parameters) };
                        }

                        break;
                }

                continue;
            }

            // typealias W<T : I> = Wrapper<T>; (型引数の制約のある別名)
            if (token.Text == "typealias" && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken && i + 2 < tokens.Length && tokens[i + 2].Kind == ShaderSyntaxKind.LessThanToken)
            {
                List<ShaderSyntaxToken?> aliasDefaults = [];
                (List<string> aliasParameters, List<List<string>> aliasConstraints, int _) = ReadGenericParameters(tokens, i + 2, aliasDefaults);

                if (aliasParameters.Count > 0)
                {
                    string alias = tokens[i + 1].Text;
                    structs[alias] = structs.ContainsKey(alias) ? null : new Generic(alias, [.. aliasParameters], [.. aliasConstraints.Select(c => c.ToImmutableArray())]) { Defaults = [.. aliasDefaults] };
                }

                continue;
            }

            // R f<T : I>(…) where T : J { / func f<T : I>(…) -> R / __generic<T : I> R f(…)
            if (tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken
                && i > 0 && tokens[i - 1].Kind == ShaderSyntaxKind.IdentifierToken
                && tokens[i - 1].Text is not ("return" or "case" or "else" or "throw" or "new" or "in" or "out" or "inout" or "typealias" or "typedef")
                && token.Text is not ("struct" or "class" or "interface" or "extension"))
            {
                List<ShaderSyntaxToken?> functionDefaults = [];
                (List<string> parameters, List<List<string>> constraints, int close) = ReadGenericParameters(tokens, i + 1, functionDefaults);

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

                // 仮引数の並び (型と配列の大きさ) に出てくる型の仮引数は、実引数から決まりうる。
                HashSet<string> mentioned = [.. tokens.Skip(close + 1).Take(end - close - 1).Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken).Select(t => t.Text)];

                // ほかの型の仮引数の制約の型引数 (TArray : IArray<T> の T) と where の制約に出てくる仮引数も、その型の仮引数から決まりうる。
                for (int k = i + 1, angle = 0; k < close; k++)
                {
                    angle += tokens[k].Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, ShaderSyntaxKind.GreaterThanGreaterThanToken => -2, _ => 0 };

                    if (angle >= 2 && tokens[k].Kind == ShaderSyntaxKind.IdentifierToken)
                    {
                        mentioned.Add(tokens[k].Text);
                    }
                }

                for (int k = end + 1; k < tokens.Length && tokens[k].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); k++)
                {
                    // where countof(TIndex) == N の N は、並びの数から決まらない (slangc は特殊化できない。E39999)。
                    bool countedValue = tokens[k - 1].Kind == ShaderSyntaxKind.EqualsEqualsToken && tokens[k - 2].Kind == ShaderSyntaxKind.CloseParenToken
                                        && Enumerable.Range(1, Math.Min(k - 2, 8)).Select(back => tokens[k - 2 - back]).TakeWhile(t => t.Kind != ShaderSyntaxKind.EqualsEqualsToken)
                                            .Any(t => t.Text == "countof");

                    if (tokens[k].Kind == ShaderSyntaxKind.IdentifierToken && tokens[k - 1].Text != "where" && !countedValue)
                    {
                        mentioned.Add(tokens[k].Text);
                    }
                }
                functions[token.Text] = functions.ContainsKey(token.Text)
                    ? null
                    : new Generic(token.Text, [.. parameters], [.. constraints.Select(c => c.ToImmutableArray())])
                    {
                        Defaults = [.. functionDefaults],
                        Mentioned = [.. parameters.Select(mentioned.Contains)],
                        Special = ReadSpecial(tokens, end + 1, parameters),
                    };
            }
        }
    }

    /// <summary>位置 <paramref name="close"/> の &gt; に対応する、前の __generic&lt; の &lt; の位置。無ければ <see langword="null"/>。</summary>
    private static int? GenericBefore(ImmutableArray<ShaderSyntaxToken> tokens, int close)
    {
        int depth = 0;

        for (int i = close; i > 0; i--)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.GreaterThanToken)
            {
                depth++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.LessThanToken && --depth == 0)
            {
                return tokens[i - 1].Text == "__generic" ? i : null;
            }
            else if (tokens[i].Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.CloseBraceToken)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>&lt;T : I, let N : int, U&gt;</c> を読む。型の仮引数の名前と、それぞれの制約 (名前だけのもの)。<c>let</c> の仮引数は値なので、制約を持たない名前として数える。
    /// </summary>
    private static (List<string> Parameters, List<List<string>> Constraints, int Next) ReadGenericParameters(ImmutableArray<ShaderSyntaxToken> tokens, int start)
        => ReadGenericParameters(tokens, start, []);

    /// <summary><c>&lt;T : I = D&gt;</c> を読む。<paramref name="defaults"/> に、仮引数ごとの既定の型引数 (名前 1 つのもの) を足す。</summary>
    private static (List<string> Parameters, List<List<string>> Constraints, int Next) ReadGenericParameters(ImmutableArray<ShaderSyntaxToken> tokens, int start, List<ShaderSyntaxToken?> defaults)
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

            // <U : IBase<T>> の >> (2 つの > が 1 つのトークンになったもの)
            if (token.Kind == ShaderSyntaxKind.GreaterThanGreaterThanToken)
            {
                depth -= 2;

                if (depth <= 0)
                {
                    i++;
                    break;
                }

                continue;
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

                // <int N> (型を前に書く値の仮引数)。名前は後ろの語。
                if (i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    value = true;
                    continue;
                }

                parameters.Add(token.Text);
                constraints.Add([]);
                defaults.Add(null);
                expectName = false;
                continue;
            }

            // = Default (名前 1 つで、続きが , か > のもの)
            if (token.Kind == ShaderSyntaxKind.EqualsToken && defaults.Count > 0 && i + 2 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken
                && tokens[i + 2].Kind is ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.GreaterThanToken)
            {
                defaults[^1] = tokens[i + 1];
                continue;
            }

            if (!value && token.Kind == ShaderSyntaxKind.ColonToken && i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken
                // ジェネリックのインターフェイス (IArray<T>) は名前だけを覚える。型引数に依らずに適合しないと言える型だけを報告する (利用者のジェネリックのインターフェイスは決めない)。
                && !(i + 2 < tokens.Length && tokens[i + 2].Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken)
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

    /// <summary><c>where T == X</c>・<c>where X(T)</c>・<c>where __hasDiffTypeInfo(T)</c> を読む。</summary>
    private static ImmutableArray<(int Index, string Kind, string Target)> ReadSpecial(ImmutableArray<ShaderSyntaxToken> tokens, int start, List<string> parameters)
    {
        ImmutableArray<(int, string, string)>.Builder special = ImmutableArray.CreateBuilder<(int, string, string)>();

        for (int i = start; i + 4 < tokens.Length && tokens[i].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); i++)
        {
            if (tokens[i].Text != "where" || tokens[i + 1].Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            // where Box<X> == Box<X>.Nested<X> (両辺が名前 1 つでない型)。両辺の最後の名前を覚える。
            if (TypesEquality(tokens, i + 1) is { } equality)
            {
                special.Add((-1, "types", equality));
                continue;
            }

            // where T == int (右が名前 1 つのもの)
            if (tokens[i + 2].Kind == ShaderSyntaxKind.EqualsEqualsToken && parameters.IndexOf(tokens[i + 1].Text) is var left and >= 0
                && tokens[i + 3].Kind == ShaderSyntaxKind.IdentifierToken
                && tokens[i + 4].Kind is not (ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken))
            {
                special.Add((left, "==", tokens[i + 3].Text));
            }

            // where int(T)・where __hasDiffTypeInfo(T)
            else if (tokens[i + 2].Kind == ShaderSyntaxKind.OpenParenToken && tokens[i + 3].Kind == ShaderSyntaxKind.IdentifierToken
                     && tokens[i + 4].Kind == ShaderSyntaxKind.CloseParenToken && parameters.IndexOf(tokens[i + 3].Text) is var inner and >= 0
                     && tokens[i + 1].Text != "nonempty")
            {
                special.Add((inner, tokens[i + 1].Text == "__hasDiffTypeInfo" ? "diff" : "()", tokens[i + 1].Text));
            }
        }

        return special.ToImmutable();
    }

    /// <summary><c>Box&lt;X&gt; == Box&lt;X&gt;.Nested&lt;X&gt;</c> の、両辺の最後の名前と両辺の文字列 (| で区切る)。どちらかの辺が名前 1 つなら <see langword="null"/>。</summary>
    private static string? TypesEquality(ImmutableArray<ShaderSyntaxToken> tokens, int start)
    {
        int depth = 0;
        int equals = -1;
        int end = start;

        for (; end < tokens.Length; end++)
        {
            ShaderSyntaxKind kind = tokens[end].Kind;
            depth += kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, _ => 0 };

            if (depth == 0 && (kind is ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CommaToken || tokens[end].Text == "where"))
            {
                break;
            }

            if (depth == 0 && kind == ShaderSyntaxKind.EqualsEqualsToken)
            {
                equals = end;
            }
            else if (kind is not (ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.GreaterThanToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.CommaToken))
            {
                return null;
            }
        }

        if (equals < 0)
        {
            return null;
        }

        static string? Last(IEnumerable<ShaderSyntaxToken> side)
        {
            int angle = 0;
            string? last = null;

            foreach (ShaderSyntaxToken token in side)
            {
                angle += token.Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, _ => 0 };

                if (angle == 0 && token.Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    last = token.Text;
                }
            }

            return last;
        }

        ShaderSyntaxToken[] left = [.. tokens.Skip(start).Take(equals - start)];
        ShaderSyntaxToken[] right = [.. tokens.Skip(equals + 1).Take(end - equals - 1)];

        // 両辺とも型引数か . のある型だけを見る (where T == int は別に読む)。
        if (left.Length < 2 || right.Length < 2 || Last(left) is not { } leftName || Last(right) is not { } rightName)
        {
            return null;
        }

        return $"{leftName}|{rightName}|{string.Concat(left.Select(t => t.Text))}|{string.Concat(right.Select(t => t.Text))}";
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
