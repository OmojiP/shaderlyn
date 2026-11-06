using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の配列を検査する。大きさ (GD0308)、要素の数と配列どうしの大きさ (GD0319)、要素の型 (CF0410)。
/// </summary>
/// <remarks>
/// <para>
/// Godot 4.7.2 の <c>_parse_array_size</c>・<c>_parse_array_constructor</c>・局所変数と const の宣言の検査を写し、Godot に読ませて確かめた。
/// <list type="bullet">
/// <item>大きさは正の整数の定数式 (<c>2</c>、<c>2u</c>、<c>N + 1</c>)。0・負の数・浮動小数点数・変数は書けない</item>
/// <item>大きさを省けるのは、<c>{ }</c> で初期化する局所変数と const だけ。仮引数・戻り値・uniform・varying・構造体のメンバーでは省けない</item>
/// <item>初期値の要素の数 (<c>{ … }</c> と <c>float[](…)</c>) は大きさと同じ。配列どうしの代入と仮引数への受け渡しも大きさが同じ</item>
/// <item><c>{ … }</c> の要素は配列の要素と同じ型 (整数のリテラルも変換しない)。<c>int[2](…)</c> を <c>float[2]</c> にも代入できない</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class GodotArrayAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [GodotRuleDescriptors.InvalidArraySize, GodotRuleDescriptors.ArraySizeMismatch, HlslRuleDescriptors.ValueNotConvertible];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGodot)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken? token, params object[] arguments)
        {
            if (token is not null
                && compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, arguments));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);
            Checker checker = new(program.Tree.Root, binder, Report);
            checker.Run();
        }
    }

    private sealed class Checker(CompilationUnitSyntax root, ExpressionTypeBinder binder, Action<DiagnosticDescriptor, ShaderSyntaxToken?, object[]> report)
    {
        /// <summary>整数の const の値 (関数の外と、今の関数の中)。</summary>
        private readonly Dictionary<string, long?> _constants = new(StringComparer.Ordinal);

        /// <summary>const でない変数の名前 (関数の外の uniform・varying と、今の関数の中の変数と仮引数)。</summary>
        private readonly HashSet<string> _variables = new(StringComparer.Ordinal);

        public void Run()
        {
            foreach (DeclarationSyntax declaration in root.Declarations)
            {
                switch (declaration)
                {
                    case VariableDeclarationSyntax variable:

                        CheckDeclaration(variable);
                        break;

                    case StructDeclarationSyntax structure:
                        foreach (VariableDeclarationSyntax member in structure.Members.OfType<VariableDeclarationSyntax>())
                        {
                            foreach (VariableDeclaratorSyntax declarator in member.Variables)
                            {
                                CheckSize(declarator.ArrayRankTokens, allowUnsized: false);
                            }

                            CheckSize(member.Type.ArrayRanks, allowUnsized: false);
                        }

                        break;

                    case FunctionDeclarationSyntax function:
                        CheckFunction(function);
                        break;
                }
            }
        }

        private void CheckFunction(FunctionDeclarationSyntax function)
        {
            Dictionary<string, long?> globalConstants = new(_constants, StringComparer.Ordinal);
            HashSet<string> globalVariables = new(_variables, StringComparer.Ordinal);

            CheckSize(function.ReturnType.ArrayRanks, allowUnsized: false);

            foreach (ParameterSyntax parameter in function.ParameterList)
            {
                CheckSize(parameter.ArrayRankTokens, allowUnsized: false);
                CheckSize(parameter.Type.ArrayRanks, allowUnsized: false);
                _constants.Remove(parameter.Name);
                _variables.Add(parameter.Name);
            }

            if (function.Body is { } body)
            {
                foreach (ShaderSyntaxNode node in body.DescendantNodesAndSelf().OfType<ShaderSyntaxNode>())
                {
                    switch (node)
                    {
                        case VariableDeclarationSyntax variable when variable.Parent is LocalDeclarationStatementSyntax or ForStatementSyntax:

                            CheckDeclaration(variable);
                            break;

                        case AssignmentExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsToken } assignment
                            when LengthOf(assignment.Left) is { } target && LengthOf(assignment.Right) is { } value && target != value:
                            report(GodotRuleDescriptors.ArraySizeMismatch, assignment.OperatorToken,
                                [$"大きさ {value} の配列を、大きさ {target} の配列に代入できません。"]);
                            break;

                        case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } invocation:
                            CheckArguments(invocation, callee.Name);
                            break;

                        // float[2](1.0, 2.0, 3.0) (初期値でない所の値の作成も、書いた大きさと要素の数が同じ)
                        case InvocationExpressionSyntax { Target: ArrayTypeExpressionSyntax constructor } invocation
                            when LengthOfRanks(constructor.ArrayRanks) is { } own && own != invocation.ArgumentExpressions.Count():
                            report(GodotRuleDescriptors.ArraySizeMismatch, constructor.ElementType.Identifier,
                                [$"配列の要素の数が合いません。{own} 個が要ります (今は {invocation.ArgumentExpressions.Count()} 個)。"]);
                            break;
                    }
                }
            }

            _constants.Clear();
            _variables.Clear();

            foreach ((string name, long? value) in globalConstants)
            {
                _constants[name] = value;
            }

            _variables.UnionWith(globalVariables);
        }

        /// <summary>変数の宣言の、大きさ・初期値の要素の数と型を調べる。</summary>
        private void CheckDeclaration(VariableDeclarationSyntax variable)
        {
            bool constant = variable.HasModifier("const");
            bool unsizedAllowed = !variable.HasModifier("uniform") && !variable.HasModifier("varying");

            CheckSize(variable.Type.ArrayRanks, unsizedAllowed);

            foreach (VariableDeclaratorSyntax declarator in variable.Variables)
            {
                CheckSize(declarator.ArrayRankTokens, unsizedAllowed);

                if (declarator.IsArray && declarator.Initializer is { } initializer)
                {
                    CheckInitializer(declarator, variable.Type, initializer);
                }

                if (constant && !declarator.IsArray && variable.Type.Name is "int" or "uint")
                {
                    _constants[declarator.Name] = declarator.Initializer is { } value ? Evaluate(value) : null;
                    _variables.Remove(declarator.Name);
                }
                else if (!constant)
                {
                    _constants.Remove(declarator.Name);
                    _variables.Add(declarator.Name);
                }
            }
        }

        /// <summary>配列の初期値の要素の数と型を調べる。</summary>
        private void CheckInitializer(VariableDeclaratorSyntax declarator, TypeSyntax type, ExpressionSyntax initializer)
        {
            long? length = LengthOfRanks(declarator.ArrayRankTokens) ?? LengthOfRanks(type.ArrayRanks);

            switch (initializer)
            {
                case InitializerListExpressionSyntax list:
                    ExpressionSyntax[] elements = [.. list.Elements.Select(e => e.Node).OfType<ExpressionSyntax>()];

                    if (length is { } expected && expected != elements.Length)
                    {
                        report(GodotRuleDescriptors.ArraySizeMismatch, list.OpenBrace,
                            [$"配列の要素の数が合いません。{expected} 個が要ります (今は {elements.Length} 個)。"]);
                        return;
                    }

                    foreach (ExpressionSyntax element in elements)
                    {
                        if (element is not InitializerListExpressionSyntax
                            && binder.GetTypesByConfiguration(element) is [{ Type: { } elementType }]
                            && elementType != type.Name
                            && IsKnownType(elementType) && IsKnownType(type.Name))
                        {
                            report(HlslRuleDescriptors.ValueNotConvertible, element.FirstToken, [type.Name, elementType, string.Empty]);
                            return;
                        }
                    }

                    break;

                case InvocationExpressionSyntax { Target: ArrayTypeExpressionSyntax constructor } invocation:
                    int count = invocation.ArgumentExpressions.Count();
                    long? constructed = LengthOfRanks(constructor.ArrayRanks);

                    if (constructor.ElementType.Name != type.Name && IsKnownType(constructor.ElementType.Name) && IsKnownType(type.Name))
                    {
                        string size = length?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                        report(HlslRuleDescriptors.ValueNotConvertible, constructor.ElementType.Identifier,
                            [$"{type.Name}[{size}]", $"{constructor.ElementType.Name}[{constructed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}]", string.Empty]);
                        return;
                    }

                    if (constructed is { } own && own != count)
                    {
                        report(GodotRuleDescriptors.ArraySizeMismatch, constructor.ElementType.Identifier,
                            [$"配列の要素の数が合いません。{own} 個が要ります (今は {count} 個)。"]);
                        return;
                    }

                    long found = constructed ?? count;

                    if (length is { } expectedLength && expectedLength != found)
                    {
                        report(GodotRuleDescriptors.ArraySizeMismatch, constructor.ElementType.Identifier,
                            [$"配列の要素の数が合いません。{expectedLength} 個が要ります (今は {found} 個)。"]);
                    }

                    break;
            }
        }

        /// <summary>配列の仮引数に渡した配列の大きさを調べる。</summary>
        private void CheckArguments(InvocationExpressionSyntax invocation, string name)
        {
            ExpressionSyntax[] arguments = [.. invocation.ArgumentExpressions];
            FunctionDeclarationSyntax[] functions = [.. root.Declarations.OfType<FunctionDeclarationSyntax>()
                .Where(f => f.Name == name && f.ParameterList.Count() == arguments.Length)];

            if (functions is not [var function])
            {
                return;
            }

            ParameterSyntax[] parameters = [.. function.ParameterList];

            for (int i = 0; i < arguments.Length; i++)
            {
                if ((LengthOfRanks(parameters[i].ArrayRankTokens) ?? LengthOfRanks(parameters[i].Type.ArrayRanks)) is { } expected
                    && LengthOf(arguments[i]) is { } actual
                    && expected != actual)
                {
                    report(GodotRuleDescriptors.ArraySizeMismatch, arguments[i].FirstToken,
                        [$"関数 '{name}' の {i + 1} 番目の仮引数は大きさ {expected} の配列です (渡したのは大きさ {actual})。"]);
                }
            }
        }

        /// <summary>大きさの書き方を調べる。</summary>
        private void CheckSize(ImmutableArray<ShaderSyntaxToken> ranks, bool allowUnsized)
        {
            if (ranks.IsEmpty || ranks[0].Kind != ShaderSyntaxKind.OpenBracketToken)
            {
                return;
            }

            int close = ranks.IndexOf(ranks.FirstOrDefault(t => t.Kind == ShaderSyntaxKind.CloseBracketToken)!);

            if (close < 0)
            {
                return;
            }

            ImmutableArray<ShaderSyntaxToken> content = ranks[1..close];

            if (content.IsEmpty)
            {
                if (!allowUnsized)
                {
                    report(GodotRuleDescriptors.InvalidArraySize, ranks[0], ["ここでは配列の大きさを省けません。"]);
                }

                return;
            }

            if (content.Any(t => t.Kind == ShaderSyntaxKind.NumericLiteralToken && !GodotLiterals.TryParseInteger(t.Text, out _))
                || content.Any(t => t.Kind == ShaderSyntaxKind.IdentifierToken && _variables.Contains(t.Text) && !_constants.ContainsKey(t.Text))
                || EvaluateTokens(content) is <= 0)
            {
                report(GodotRuleDescriptors.InvalidArraySize, content[0], ["配列の大きさは正の整数の定数でなければなりません。"]);
            }
        }

        /// <summary>配列の値の大きさ (名前と、大きさを書いた配列の値の作成)。分からなければ <see langword="null"/>。</summary>
        private long? LengthOf(ExpressionSyntax expression) => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => LengthOf(parenthesized.Expression),
            IdentifierExpressionSyntax identifier => binder.ResolveName(identifier) is { Kind: DeclaredNameKind.Local or DeclaredNameKind.Global, Candidates: [var single] }
                                                     && single.Type.IsArray
                                                     && single.Declarator is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } declarator
                ? LengthOfRanks(declarator.ArrayRankTokens) ?? LengthOfRanks(declaration.Type.ArrayRanks)
                : ParameterLength(identifier),
            _ => null,
        };

        /// <summary>配列の仮引数の大きさ。</summary>
        private long? ParameterLength(IdentifierExpressionSyntax identifier)
            => binder.ResolveName(identifier) is { Kind: DeclaredNameKind.Local, Candidates: [{ Declarator: ParameterSyntax parameter, Type.IsArray: true }] }
                ? LengthOfRanks(parameter.ArrayRankTokens) ?? LengthOfRanks(parameter.Type.ArrayRanks)
                : null;

        /// <summary>大きさの <c>[ … ]</c> を計算する。</summary>
        private long? LengthOfRanks(ImmutableArray<ShaderSyntaxToken> ranks)
        {
            if (ranks.Length < 3 || ranks[0].Kind != ShaderSyntaxKind.OpenBracketToken || ranks[^1].Kind != ShaderSyntaxKind.CloseBracketToken)
            {
                return null;
            }

            return EvaluateTokens(ranks[1..^1]);
        }

        /// <summary>整数の定数式を計算する (リテラル・整数の const・+ - * / %・括弧)。分からなければ <see langword="null"/>。</summary>
        private long? EvaluateTokens(ImmutableArray<ShaderSyntaxToken> tokens)
        {
            int position = 0;
            long? value = Sum();
            return position == tokens.Length ? value : null;

            long? Sum()
            {
                long? left = Product();

                while (position < tokens.Length && tokens[position].Kind is ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken)
                {
                    bool plus = tokens[position++].Kind == ShaderSyntaxKind.PlusToken;
                    long? right = Product();
                    left = left is { } a && right is { } b ? (plus ? a + b : a - b) : null;
                }

                return left;
            }

            long? Product()
            {
                long? left = Unary();

                while (position < tokens.Length && tokens[position].Kind is ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.SlashToken or ShaderSyntaxKind.PercentToken)
                {
                    ShaderSyntaxKind kind = tokens[position++].Kind;
                    long? right = Unary();
                    left = left is { } a && right is { } b && (kind == ShaderSyntaxKind.AsteriskToken || b != 0)
                        ? kind switch { ShaderSyntaxKind.AsteriskToken => a * b, ShaderSyntaxKind.SlashToken => a / b, _ => a % b }
                        : null;
                }

                return left;
            }

            long? Unary()
            {
                if (position >= tokens.Length)
                {
                    return null;
                }

                ShaderSyntaxToken token = tokens[position++];

                switch (token.Kind)
                {
                    case ShaderSyntaxKind.MinusToken:
                        return Unary() is { } negated ? -negated : null;
                    case ShaderSyntaxKind.NumericLiteralToken:
                        return GodotLiterals.TryParseInteger(token.Text, out long number) ? number : null;
                    case ShaderSyntaxKind.IdentifierToken:
                        return _constants.TryGetValue(token.Text, out long? constant) ? constant : null;
                    case ShaderSyntaxKind.OpenParenToken:
                        long? inner = Sum();
                        return position < tokens.Length && tokens[position++].Kind == ShaderSyntaxKind.CloseParenToken ? inner : null;
                    default:
                        return null;
                }
            }
        }

        /// <summary>整数の定数式 (式の木) を計算する。</summary>
        private long? Evaluate(ExpressionSyntax expression) => expression switch
        {
            LiteralExpressionSyntax literal => GodotLiterals.TryParseInteger(literal.Token.Text, out long value) ? value : null,
            IdentifierExpressionSyntax identifier => _constants.TryGetValue(identifier.Name, out long? constant) ? constant : null,
            ParenthesizedExpressionSyntax parenthesized => Evaluate(parenthesized.Expression),
            PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.MinusToken } minus => Evaluate(minus.Operand) is { } operand ? -operand : null,
            BinaryExpressionSyntax binary when Evaluate(binary.Left) is { } a && Evaluate(binary.Right) is { } b => binary.OperatorToken.Kind switch
            {
                ShaderSyntaxKind.PlusToken => a + b,
                ShaderSyntaxKind.MinusToken => a - b,
                ShaderSyntaxKind.AsteriskToken => a * b,
                ShaderSyntaxKind.SlashToken when b != 0 => a / b,
                ShaderSyntaxKind.PercentToken when b != 0 => a % b,
                _ => null,
            },
            _ => null,
        };

        /// <summary>型の名前が組み込みの型か、構造体か (型の分からない名前と比べない)。</summary>
        private bool IsKnownType(string type)
            => Shaderlyn.Godot.Parsing.GodotKeywords.BuiltInTypes.Contains(type) || root.Declarations.OfType<StructDeclarationSyntax>().Any(s => s.Name == type);
    }
}
