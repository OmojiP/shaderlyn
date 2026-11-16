using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の型の宣言と、その使い方を検査する。class の作り方 (SG0346)、継承と override (SG0347)、property と __subscript (SG0348)。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>SG0346: <c>new</c> を書かない class の作成 (<c>MyClass()</c>・<c>(MyClass)4</c>。E30066)</item>
/// <item>SG0347: 2 つ以上の構造体からの継承と、最初でない構造体の基底 (E30820)。インターフェイスの既定の実装と同じ名前のメソッドの <c>override</c> の書き忘れ (E30853) と、
/// 基底に同じ名前と仮引数の数のメソッドが無い <c>override</c> (E30854)。構造体と class の間の継承と <c>#lang 2026</c> の構造体の継承 (E30811・E30814)、
/// インターフェイスの要件の <c>override</c> (E30312)、<c>[COM]</c> のインターフェイスの継承 (E31124・E31126)</item>
/// <item>SG0101: インターフェイスの <c>__init</c> と property の要件の本体 (E30317)、<c>[COM]</c> のインターフェイスの associatedtype (E31122)</item>
/// <item>SG0301: <c>as</c> の右のインターフェイス (E30302) と、制約の無いジェネリックの型の値の <c>is</c> (E30301)</item>
/// <item>SG0348: <c>-&gt;</c> で戻り値の型を書かない <c>__subscript</c> (E30901)。property の <c>get</c> などの仮引数 (E31112)、<c>set</c> の 2 つ以上の仮引数 (E31108)、
/// 型を書かない仮引数 (E30621)、property の型と違う <c>set</c> の仮引数の型 (E31109)、インターフェイスの accessor の片方だけの既定の実装 (E30318)</item>
/// </list>
/// 構文解析は <c>new</c>・<c>property</c>・<c>__subscript</c> を構文木に残さないので、トークンの並びを読む。
/// </remarks>
internal sealed class SlangTypeDeclarationAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [SlangRuleDescriptors.ClassWithoutNew, SlangRuleDescriptors.InheritanceAndOverride, SlangRuleDescriptors.AccessorForm, SlangRuleDescriptors.WrongOperandTypes, SlangRuleDescriptors.InterfaceRequirement, SlangRuleDescriptors.DeclarationNotAllowed,
         SlangRuleDescriptors.InvalidDeclarationType, SlangRuleDescriptors.NotWritable];

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
            ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;

            void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken at, string message)
            {
                if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
                }
            }

            StructDeclarationSyntax[] types = [.. root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>().Where(s => s.NameToken is not null)];
            Dictionary<string, int> kinds = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax type in types)
            {
                // 同じ名前の型が 2 つあれば、どちらか決めない (-1)。
                int kind = type.Keyword.Text switch { "class" => 1, "struct" => 2, "interface" => 3, _ => 0 };
                kinds[type.NameToken!.Text] = kinds.TryGetValue(type.NameToken.Text, out int known) && known != kind ? -1 : kind;
            }

            // 関数ごとの、変数の名前から宣言した型の名前 (その関数の中で 1 度だけ宣言した名前)。is / as の左の値の型を引く。
            List<(FunctionDeclarationSyntax Function, Dictionary<string, string?> Names)> scopes = [];

            foreach (FunctionDeclarationSyntax function in root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>())
            {
                Dictionary<string, string?> variables = new(StringComparer.Ordinal);
                scopes.Add((function, variables));

                foreach (ShaderSyntaxNode node in function.DescendantNodesAndSelf())
                {
                    (string? name, TypeSyntax? type) = node switch
                    {
                        VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } variable => (variable.IsArray ? null : variable.Name, declaration.Type),
                        ParameterSyntax parameter => (parameter.IsArray ? null : parameter.Name, parameter.Type),
                        _ => (null, null),
                    };

                    if (name is not null && type is not null)
                    {
                        variables[name] = variables.ContainsKey(name) || type.OpenAngle is not null || !type.NestedTypeTokens.IsDefaultOrEmpty ? null : type.NameToken.Text;
                    }
                }
            }

            Dictionary<string, string?>? VariablesAt(ShaderSyntaxToken token)
                => FunctionAt(token).Names;

            (FunctionDeclarationSyntax Function, Dictionary<string, string?> Names) FunctionAt(ShaderSyntaxToken token)
                => scopes.FirstOrDefault(s => s.Function.Body is { } body && ReferenceEquals(body.OpenBrace.Source, token.Source)
                                              && body.OpenBrace.Span.Start < token.Span.Start && token.Span.Start < body.CloseBrace.Span.Start);

            // 関数の、制約の無いジェネリックの型の仮引数 (<T> の T。where で制約を足したものは除く)。
            HashSet<string> UnconstrainedAt(ShaderSyntaxToken token)
            {
                HashSet<string> names = new(StringComparer.Ordinal);

                if (FunctionAt(token).Function is not { GenericParameterTokens: { Length: > 2 } generic } function)
                {
                    return names;
                }

                List<ShaderSyntaxToken> part = [];

                foreach (ShaderSyntaxToken t in generic.Skip(1))
                {
                    if (t.Kind is ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.GreaterThanToken)
                    {
                        if (part is [{ Kind: ShaderSyntaxKind.IdentifierToken } only])
                        {
                            names.Add(only.Text);
                        }

                        part.Clear();
                        continue;
                    }

                    part.Add(t);
                }

                // ) と { の間の where T : I の T
                int close = tokens.IndexOf(function.CloseParen);

                for (int k = close + 1; close >= 0 && k + 1 < tokens.Length && tokens[k].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); k++)
                {
                    if (tokens[k].Text == "where")
                    {
                        names.Remove(tokens[k + 1].Text == "optional" && k + 2 < tokens.Length ? tokens[k + 2].Text : tokens[k + 1].Text);
                    }
                }

                return names;
            }

            // 構造体の継承の関係 (基底への as は通る)。
            Dictionary<string, string> bases = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax type in types)
            {
                if (type.BaseTypeName is { } baseName)
                {
                    bases.TryAdd(type.NameToken!.Text, baseName);
                }
            }

            bool Derives(string derived, string ancestor)
            {
                for (int depth = 0; depth < 16 && bases.TryGetValue(derived, out string? next); depth++)
                {
                    if (next == ancestor)
                    {
                        return true;
                    }

                    derived = next;
                }

                return false;
            }

            CheckTokens(tokens, kinds, VariablesAt, UnconstrainedAt, (a, b) => Derives(a, b) || Derives(b, a), DeclarationSyntaxAnalyzer.IsSlang2026(root), Report);
            // extension で足されうる型と、既定の実装を足されうるインターフェイスの名前 (extension NAME)。
            HashSet<string> extended = new(StringComparer.Ordinal);

            for (int i = 0; i + 1 < tokens.Length; i++)
            {
                if (tokens[i].Text is "extension" or "__extension")
                {
                    // extension<T> T : IFoo のような、どの型にも当たる extension があれば、要件の検査をしない。
                    extended.Add(tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken ? tokens[i + 1].Text : "*");
                }
            }

            CheckOverrides(types, kinds, extended, SlangConformanceModel.Build(tokens, compilation.TypeSystem.ForTree(program.Tree)), Report);
            CheckCom(tokens, Report);
            CheckInterfaceMembers(tokens, Report);
            CheckCycles(types, Report);
            CheckReadOnlyProperties(tokens, types, Report);

            // a + b (制約の無いジェネリックの型の値どうしの組み込みの演算子。E39999)
            for (int i = 1; i + 2 < tokens.Length; i++)
            {
                if (tokens[i].Kind is not (ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.SlashToken or ShaderSyntaxKind.PercentToken)
                    || tokens[i - 1].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i + 1].Kind != ShaderSyntaxKind.IdentifierToken
                    || (i > 1 && tokens[i - 2].Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken)
                    || tokens[i + 2].Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.OpenBracketToken or ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.ColonColonToken
                    || UnconstrainedAt(tokens[i]) is not { Count: > 0 } unconstrained || VariablesAt(tokens[i]) is not { } variables)
                {
                    continue;
                }

                string? left = variables.GetValueOrDefault(tokens[i - 1].Text);
                string? right = variables.GetValueOrDefault(tokens[i + 1].Text);

                if (left is not null && right is not null && unconstrained.Contains(left) && left == right)
                {
                    Report(SlangRuleDescriptors.WrongOperandTypes, tokens[i],
                        $"制約の無いジェネリックの型 {left} の値に、{tokens[i].Text} は使えません。{left} に IArithmetic などの制約を付けてください。");
                }
            }
        }
    }

    /// <summary>
    /// 自分を (フィールドを通して) 含む構造体 (E41001)。ポインターと、資源などの型引数の中のものは含まない。
    /// </summary>
    private static void CheckCycles(StructDeclarationSyntax[] types, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        Dictionary<string, StructDeclarationSyntax?> byName = new(StringComparer.Ordinal);

        foreach (StructDeclarationSyntax type in types.Where(t => t.Keyword.Text is "struct" or "class"))
        {
            byName[type.Name] = byName.ContainsKey(type.Name) ? null : type;
        }

        // 構造体から、値として持つフィールドの構造体へ。class は参照なので見ない。
        Dictionary<string, HashSet<string>> edges = new(StringComparer.Ordinal);

        foreach ((string name, StructDeclarationSyntax? type) in byName)
        {
            if (type is null || type.Keyword.Text != "struct")
            {
                continue;
            }

            edges[name] = [.. type.Fields
                .Where(f => !f.ModifierTokens.Any(t => t.Text is "static" or "const") && !f.Type.IsPointer && f.Type.QualifierTokens.IsDefaultOrEmpty && f.Type.NestedTypeTokens.IsDefaultOrEmpty)
                .Select(f => f.Type.NameToken.Text)
                .Where(n => byName.TryGetValue(n, out StructDeclarationSyntax? target) && target is { Keyword.Text: "struct" })];
        }

        foreach ((string start, StructDeclarationSyntax? type) in byName)
        {
            if (type is null || !edges.ContainsKey(start))
            {
                continue;
            }

            // start から start へ戻る道があるか。
            HashSet<string> seen = new(StringComparer.Ordinal);
            Stack<string> pending = new(edges[start]);
            bool cyclic = false;

            while (!cyclic && pending.TryPop(out string? next))
            {
                cyclic = next == start;

                if (!cyclic && seen.Add(next) && edges.TryGetValue(next, out HashSet<string>? more))
                {
                    foreach (string m in more)
                    {
                        pending.Push(m);
                    }
                }
            }

            if (cyclic && type.NameToken is { } at)
            {
                report(SlangRuleDescriptors.InvalidDeclarationType, at, $"構造体 {start} は、フィールドを通して自分を値として含みます。どこかをポインターにするか、フィールドを外してください。");
            }
        }
    }

    /// <summary>get だけの property への書き込み (t.prop.x += 1。E30047)。</summary>
    private static void CheckReadOnlyProperties(ImmutableArray<ShaderSyntaxToken> tokens, StructDeclarationSyntax[] types, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        // property NAME … { get … } (set・ref・modify の無いもの) の名前。同じ名前のフィールドと、書ける property があれば外す。
        HashSet<string> readOnly = new(StringComparer.Ordinal);
        HashSet<string> writable = new(StringComparer.Ordinal);

        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Text != "property")
            {
                continue;
            }

            int open = i + 1;

            while (open < tokens.Length && tokens[open].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
            {
                open++;
            }

            // 名前は { か : の前の最後の名前 (property int p { … }・property p : int { … })。
            int nameAt = open - 1;

            for (int k = i + 1; k < open; k++)
            {
                if (tokens[k].Kind == ShaderSyntaxKind.ColonToken)
                {
                    nameAt = k - 1;
                    break;
                }
            }

            if (open >= tokens.Length || tokens[open].Kind != ShaderSyntaxKind.OpenBraceToken || nameAt <= i || tokens[nameAt].Kind != ShaderSyntaxKind.IdentifierToken
                || MatchingBrace(tokens, open) is not { } close)
            {
                continue;
            }

            bool settable = false;
            int depth = 0;

            for (int k = open; k <= close; k++)
            {
                depth += tokens[k].Kind switch { ShaderSyntaxKind.OpenBraceToken => 1, ShaderSyntaxKind.CloseBraceToken => -1, _ => 0 };
                settable |= depth == 1 && tokens[k].Text is "set" or "ref" or "modify";
            }

            (settable ? writable : readOnly).Add(tokens[nameAt].Text);
        }

        readOnly.ExceptWith(writable);
        readOnly.ExceptWith(types.SelectMany(t => t.Fields).SelectMany(f => f.Variables).Select(v => v.Name));

        if (readOnly.Count == 0)
        {
            return;
        }

        for (int i = 1; i + 1 < tokens.Length; i++)
        {
            if (tokens[i - 1].Kind != ShaderSyntaxKind.DotToken || tokens[i].Kind != ShaderSyntaxKind.IdentifierToken || !readOnly.Contains(tokens[i].Text))
            {
                continue;
            }

            // .prop と、続く .x のような成分。
            int j = i + 1;

            while (j + 1 < tokens.Length && tokens[j].Kind == ShaderSyntaxKind.DotToken && tokens[j + 1].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                j += 2;
            }

            if (j < tokens.Length && tokens[j].Kind is ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken
                    or ShaderSyntaxKind.AsteriskEqualsToken or ShaderSyntaxKind.SlashEqualsToken or ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken)
            {
                report(SlangRuleDescriptors.NotWritable, tokens[i], $"property '{tokens[i].Text}' には get しか無いので、書き込めません。set を書いてください。");
            }
        }
    }

    private static void CheckTokens(ImmutableArray<ShaderSyntaxToken> tokens, Dictionary<string, int> kinds, Func<ShaderSyntaxToken, Dictionary<string, string?>?> variablesAt, Func<ShaderSyntaxToken, HashSet<string>> unconstrainedAt, Func<string, string, bool> related, bool since2026, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            ShaderSyntaxToken? previous = i > 0 ? tokens[i - 1] : null;
            ShaderSyntaxToken? next = i + 1 < tokens.Length ? tokens[i + 1] : null;

            // MyClass(…) と (MyClass)x。new MyClass(…) と、型の名前の位置 (MyClass f(…)・MyClass x) は見ない。
            if (kinds.TryGetValue(token.Text, out int kind) && kind == 1 && previous is not null && previous.Text != "new")
            {
                // 同じ class の値からの作成 (Counter(other)・(Counter)other) は、値をそのまま使うので通る。実引数が無いか、数のリテラルだけのものを見る。
                bool call = next?.Kind == ShaderSyntaxKind.OpenParenToken
                            && (previous.Kind is ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.QuestionToken
                                || previous.Text == "return")
                            && Matching(tokens, i + 1) is { } end
                            && tokens.Skip(i + 2).Take(end - i - 2).All(t => t.Kind is ShaderSyntaxKind.NumericLiteralToken or ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.MinusToken);
                bool cast = previous.Kind == ShaderSyntaxKind.OpenParenToken && next?.Kind == ShaderSyntaxKind.CloseParenToken
                            && i + 2 < tokens.Length && tokens[i + 2].Kind == ShaderSyntaxKind.NumericLiteralToken
                            && i > 1 && tokens[i - 2].Kind is ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.CommaToken;

                if (call || cast)
                {
                    report(SlangRuleDescriptors.ClassWithoutNew, token, $"class の {token.Text} の値は new で作ってください (new {token.Text}(…))。");
                }
            }

            // struct Derived : Base1, Base2 (2 つの構造体の基底と、最初でない構造体の基底)。
            if (token.Text is "struct" or "class" && next?.Kind == ShaderSyntaxKind.IdentifierToken)
            {
                CheckBases(tokens, i, kinds, since2026, report);
            }

            // __subscript(int x) { … } (-> で戻り値の型を書いていない)。
            if (token.Text == "__subscript" && next?.Kind == ShaderSyntaxKind.OpenParenToken
                && Matching(tokens, i + 1) is { } close && close + 1 < tokens.Length
                && tokens[close + 1].Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                report(SlangRuleDescriptors.AccessorForm, token, "__subscript には、-> で戻り値の型を書いてください (__subscript(int i) -> float)。");
            }

            if (token.Text == "property")
            {
                CheckProperty(tokens, i, report);
            }

            // x as IFoo (as の右のインターフェイス。E30302) と、制約の無いジェネリックの型の T is IFoo・t is IFoo (E30301)。
            if (token.Text is "is" or "as" && previous is { Kind: ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.CloseParenToken } before
                && next is { Kind: ShaderSyntaxKind.IdentifierToken } target
                && !(i + 2 < tokens.Length && tokens[i + 2].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken)
                && kinds.TryGetValue(target.Text, out int targetKind) && targetKind == 3)
            {
                if (token.Text == "as")
                {
                    report(SlangRuleDescriptors.WrongOperandTypes, token, $"as の右に、インターフェイス ({target.Text}) は書けません。値をインターフェイスの型の変数に入れてください。");
                }
                else if (before.Kind == ShaderSyntaxKind.IdentifierToken && !(i > 1 && tokens[i - 2].Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken)
                         && unconstrainedAt(token) is { Count: > 0 } unconstrained
                         && (unconstrained.Contains(before.Text) || (variablesAt(token) is { } known && known.TryGetValue(before.Text, out string? declaredType) && declaredType is not null && unconstrained.Contains(declaredType))))
                {
                    report(SlangRuleDescriptors.WrongOperandTypes, token, $"制約の無いジェネリックの型の値に、is でインターフェイス ({target.Text}) を調べられません。");
                }
            }

            // d is Cat・d as Cat (d が別の具体的な構造体の値)。左が型の名前 (AnotherType is ConcreteImpl) も同じ。
            if (token.Text is "is" or "as" && previous is { Kind: ShaderSyntaxKind.IdentifierToken } left
                && next is { Kind: ShaderSyntaxKind.IdentifierToken } right
                && !(i + 2 < tokens.Length && tokens[i + 2].Kind is ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken)
                && !(i > 1 && tokens[i - 2].Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken or ShaderSyntaxKind.GreaterThanToken)
                && kinds.TryGetValue(right.Text, out int rightKind) && rightKind is 1 or 2
                && (kinds.TryGetValue(left.Text, out int leftKind) && leftKind is 1 or 2 ? left.Text
                    : variablesAt(token) is { } variables && variables.TryGetValue(left.Text, out string? declared) ? declared : null) is { } leftType
                && kinds.TryGetValue(leftType, out int declaredKind) && declaredKind is 1 or 2
                && leftType != right.Text
                && !related(leftType, right.Text))
            {
                report(SlangRuleDescriptors.WrongOperandTypes, token, $"{token.Text} は、関わりの無い具体的な型どうし ({leftType} と {right.Text}) には使えません (どちらかがインターフェイスの型でなければなりません)。");
            }
        }
    }

    /// <summary>
    /// [COM] のインターフェイスの使い方を見る。associatedtype の要件 (E31122)、[COM] でないインターフェイスの継承 (E31126)、構造体の適合 (E31124)。
    /// </summary>
    private static void CheckCom(ImmutableArray<ShaderSyntaxToken> tokens, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        HashSet<string> com = new(StringComparer.Ordinal);
        List<(int Open, int Close)> bodies = [];

        for (int i = 1; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Text != "COM" || tokens[i - 1].Kind != ShaderSyntaxKind.OpenBracketToken)
            {
                continue;
            }

            // [COM("…")] の後ろの、次の interface NAME。
            for (int j = i + 1; j + 1 < tokens.Length && tokens[j].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); j++)
            {
                if (tokens[j].Text == "interface" && tokens[j + 1].Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    com.Add(tokens[j + 1].Text);
                    int open = j + 2;

                    while (open < tokens.Length && tokens[open].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
                    {
                        open++;
                    }

                    if (open < tokens.Length && tokens[open].Kind == ShaderSyntaxKind.OpenBraceToken && MatchingBrace(tokens, open) is { } close)
                    {
                        bodies.Add((open, close));
                    }

                    break;
                }
            }
        }

        if (com.Count == 0)
        {
            return;
        }

        foreach ((int open, int close) in bodies)
        {
            for (int j = open + 1; j < close; j++)
            {
                if (tokens[j].Text == "associatedtype")
                {
                    report(SlangRuleDescriptors.DeclarationNotAllowed, tokens[j], "[COM] のインターフェイスに、associatedtype の要件は書けません。");
                }
            }
        }

        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i].Text is not ("struct" or "interface") || tokens[i + 1].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i + 2].Kind != ShaderSyntaxKind.ColonToken
                || (tokens[i].Text == "interface" && com.Contains(tokens[i + 1].Text)))
            {
                continue;
            }

            for (int j = i + 3; j < tokens.Length && tokens[j].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.LessThanToken) && tokens[j].Text != "where"; j++)
            {
                if (tokens[j].Kind == ShaderSyntaxKind.IdentifierToken && com.Contains(tokens[j].Text))
                {
                    report(SlangRuleDescriptors.InheritanceAndOverride, tokens[j], tokens[i].Text == "struct"
                        ? $"構造体は、[COM] のインターフェイス {tokens[j].Text} に適合できません。class にしてください。"
                        : $"[COM] のインターフェイス {tokens[j].Text} を継承するインターフェイス {tokens[i + 1].Text} にも、[COM] を付けてください。");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// インターフェイスの中の書き方を見る。本体のある __init と、すべてに本体のある property の accessor (E30317。__subscript は通る)、override (E30312)、property と __subscript の get・set の片方だけの既定の実装 (E30318)。
    /// </summary>
    private static void CheckInterfaceMembers(ImmutableArray<ShaderSyntaxToken> tokens, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i].Text != "interface" || tokens[i + 1].Kind != ShaderSyntaxKind.IdentifierToken)
            {
                continue;
            }

            int open = i + 2;

            while (open < tokens.Length && tokens[open].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
            {
                open++;
            }

            if (open >= tokens.Length || tokens[open].Kind != ShaderSyntaxKind.OpenBraceToken || MatchingBrace(tokens, open) is not { } close)
            {
                continue;
            }

            // インターフェイスの直下 (深さ 1) のトークンだけを見る。
            int depth = 0;

            for (int j = open; j < close; j++)
            {
                ShaderSyntaxToken token = tokens[j];

                if (token.Kind == ShaderSyntaxKind.OpenBraceToken)
                {
                    depth++;
                    continue;
                }

                if (token.Kind == ShaderSyntaxKind.CloseBraceToken)
                {
                    depth--;
                    continue;
                }

                if (depth != 1)
                {
                    continue;
                }

                if (token.Text == "override" && tokens[j - 1].Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.CloseBracketToken)
                {
                    report(SlangRuleDescriptors.InheritanceAndOverride, token, "インターフェイスの要件に override は書けません。");
                }

                if (token.Text == "__init" && tokens[j + 1].Kind == ShaderSyntaxKind.OpenParenToken && Matching(tokens, j + 1) is { } parenClose
                    && parenClose + 1 < close && tokens[parenClose + 1].Kind == ShaderSyntaxKind.OpenBraceToken)
                {
                    report(SlangRuleDescriptors.DeclarationNotAllowed, token, "インターフェイスの __init の要件に、本体は書けません。");
                }

                // property … { get; set { } } と __subscript(…) -> T { get { … } set; }
                if (token.Text is "property" or "__subscript")
                {
                    int body = j + 1;

                    while (body < close && tokens[body].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
                    {
                        body++;
                    }

                    if (body >= close || tokens[body].Kind != ShaderSyntaxKind.OpenBraceToken || MatchingBrace(tokens, body) is not { } bodyClose)
                    {
                        continue;
                    }

                    List<(ShaderSyntaxToken Accessor, bool HasBody)> accessors = [];

                    for (int k = body + 1; k + 1 < bodyClose; k++)
                    {
                        if (tokens[k].Text is "get" or "set" or "ref" && tokens[k - 1].Kind is ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.CloseBracketToken)
                        {
                            int after = k + 1;

                            // set(v) の仮引数を飛ばす。
                            if (tokens[after].Kind == ShaderSyntaxKind.OpenParenToken && Matching(tokens, after) is { } p)
                            {
                                after = p + 1;
                            }

                            if (after < bodyClose && tokens[after].Kind is ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken)
                            {
                                accessors.Add((tokens[k], tokens[after].Kind == ShaderSyntaxKind.OpenBraceToken));

                                if (tokens[after].Kind == ShaderSyntaxKind.OpenBraceToken && MatchingBrace(tokens, after) is { } skip)
                                {
                                    k = skip;
                                }
                            }
                        }
                    }

                    if (accessors.Any(a => a.HasBody) && accessors.FirstOrDefault(a => !a.HasBody) is { Accessor: { } missing })
                    {
                        report(SlangRuleDescriptors.AccessorForm, accessors.First(a => a.HasBody).Accessor,
                            $"インターフェイスの {token.Text} の accessor に既定の実装を書くなら、すべての accessor に書いてください ({missing.Text} に本体がありません)。");
                    }
                    else if (token.Text == "property" && accessors.Count > 0 && accessors.All(a => a.HasBody))
                    {
                        foreach ((ShaderSyntaxToken accessor, bool _) in accessors)
                        {
                            report(SlangRuleDescriptors.DeclarationNotAllowed, accessor, $"インターフェイスの {token.Text} の要件 ({accessor.Text}) に、本体は書けません。");
                        }
                    }

                    j = bodyClose;
                }
            }
        }
    }

    /// <summary>インターフェイスと、その継承したインターフェイスのすべて。利用者の (1 度だけ宣言した) インターフェイスでないものを継承していれば <see langword="null"/>。</summary>
    private static List<StructDeclarationSyntax>? Ancestors(List<StructDeclarationSyntax> bases, Dictionary<string, StructDeclarationSyntax> interfaces)
    {
        List<StructDeclarationSyntax> all = [];
        Stack<StructDeclarationSyntax> pending = new(bases);

        while (pending.TryPop(out StructDeclarationSyntax? next))
        {
            if (all.Contains(next))
            {
                continue;
            }

            all.Add(next);

            if (next.ColonToken is null)
            {
                continue;
            }

            int angle = 0;

            foreach (ShaderSyntaxToken token in next.DescendantTokens().SkipWhile(t => t.Kind != ShaderSyntaxKind.ColonToken).Skip(1).TakeWhile(t => t.Kind != ShaderSyntaxKind.OpenBraceToken))
            {
                angle += token.Kind switch { ShaderSyntaxKind.LessThanToken => 1, ShaderSyntaxKind.GreaterThanToken => -1, _ => 0 };

                if (token.Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken || token.Text == "where")
                {
                    return null;
                }

                if (angle == 0 && token.Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    if (!interfaces.TryGetValue(token.Text, out StructDeclarationSyntax? inherited))
                    {
                        return null;
                    }

                    pending.Push(inherited);
                }
            }
        }

        return all;
    }

    /// <summary>{ に対応する } の位置。</summary>
    private static int? MatchingBrace(ImmutableArray<ShaderSyntaxToken> tokens, int open)
    {
        int depth = 0;

        for (int i = open; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                depth++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.CloseBraceToken && --depth == 0)
            {
                return i;
            }
        }

        return null;
    }

    private static void CheckBases(ImmutableArray<ShaderSyntaxToken> tokens, int keyword, Dictionary<string, int> kinds, bool since2026, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        int i = keyword + 2;

        // ジェネリックの仮引数 <T> を飛ばす。
        if (i < tokens.Length && tokens[i].Kind == ShaderSyntaxKind.LessThanToken)
        {
            int depth = 0;

            for (; i < tokens.Length; i++)
            {
                if (tokens[i].Kind == ShaderSyntaxKind.LessThanToken)
                {
                    depth++;
                }
                else if (tokens[i].Kind == ShaderSyntaxKind.GreaterThanToken && --depth == 0)
                {
                    i++;
                    break;
                }
            }
        }

        if (i >= tokens.Length || tokens[i].Kind != ShaderSyntaxKind.ColonToken)
        {
            return;
        }

        List<ShaderSyntaxToken> bases = [];
        int angle = 0;

        for (i++; i < tokens.Length && tokens[i].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken) && tokens[i].Text != "where"; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.LessThanToken)
            {
                angle++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.GreaterThanToken)
            {
                angle--;
            }
            else if (angle == 0 && tokens[i].Kind == ShaderSyntaxKind.IdentifierToken
                     && tokens[i - 1].Kind is ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.CommaToken)
            {
                bases.Add(tokens[i]);
            }
        }

        int structs = 0;

        for (int b = 0; b < bases.Count; b++)
        {
            if (!kinds.TryGetValue(bases[b].Text, out int kind) || kind is not (1 or 2))
            {
                continue;
            }

            structs++;

            // struct S : C (class の継承)・class D : S (構造体の継承)・#lang 2026 の struct D : B (E30811・E30814)。
            bool isClass = tokens[keyword].Text == "class";

            if (kind == 1 && !isClass || kind == 2 && (isClass || since2026))
            {
                report(SlangRuleDescriptors.InheritanceAndOverride, bases[b],
                    isClass
                        ? $"class が継承できるのは class かインターフェイスです ({bases[b].Text} は構造体です)。"
                        : kind == 1
                            ? $"構造体は class ({bases[b].Text}) を継承できません。"
                            : $"#lang 2026 の構造体は、インターフェイスでない型 ({bases[b].Text}) を継承できません。");
                return;
            }

            if (structs > 1 || b > 0)
            {
                report(SlangRuleDescriptors.InheritanceAndOverride, bases[b],
                    structs > 1
                        ? $"構造体が継承できる構造体は 1 つだけです ({bases[b].Text} は 2 つ目の構造体です)。"
                        : $"基底の構造体 {bases[b].Text} は、継承の並びの最初に書いてください。");
                return;
            }
        }
    }

    /// <summary>property の本体の accessor (get・set・ref) の仮引数を見る。</summary>
    private static void CheckProperty(ImmutableArray<ShaderSyntaxToken> tokens, int keyword, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        // property p : int { … } と property int p { … }。
        int open = keyword + 1;

        while (open < tokens.Length && tokens[open].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
        {
            open++;
        }

        if (open >= tokens.Length || tokens[open].Kind != ShaderSyntaxKind.OpenBraceToken)
        {
            return;
        }

        ShaderSyntaxToken[] head = [.. tokens.Skip(keyword + 1).Take(open - keyword - 1)];
        string? type = head switch
        {
            [{ Kind: ShaderSyntaxKind.IdentifierToken }, { Kind: ShaderSyntaxKind.ColonToken }, { Kind: ShaderSyntaxKind.IdentifierToken } named] => named.Text,
            [{ Kind: ShaderSyntaxKind.IdentifierToken } leading, { Kind: ShaderSyntaxKind.IdentifierToken }] => leading.Text,
            _ => null,
        };

        int depth = 0;

        for (int i = open; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                depth++;
            }
            else if (tokens[i].Kind == ShaderSyntaxKind.CloseBraceToken && --depth == 0)
            {
                return;
            }

            if (depth != 1 || tokens[i].Text is not ("get" or "set" or "ref") || i + 1 >= tokens.Length || tokens[i + 1].Kind != ShaderSyntaxKind.OpenParenToken
                || Matching(tokens, i + 1) is not { } close)
            {
                continue;
            }

            List<ShaderSyntaxToken[]> parameters = Split(tokens, i + 2, close);

            if (tokens[i].Text != "set" && parameters.Count > 0)
            {
                report(SlangRuleDescriptors.AccessorForm, tokens[i], $"{tokens[i].Text} には仮引数を書けません (仮引数を書けるのは set だけです)。");
            }
            else if (tokens[i].Text == "set" && parameters.Count > 1)
            {
                report(SlangRuleDescriptors.AccessorForm, tokens[i], "set の仮引数は 1 つだけです。");
            }

            foreach (ShaderSyntaxToken[] parameter in parameters)
            {
                // set(v) の 1 つの仮引数は、property の型になる。
                if (parameter is [{ Kind: ShaderSyntaxKind.IdentifierToken } bare] && !(tokens[i].Text == "set" && parameters.Count == 1))
                {
                    report(SlangRuleDescriptors.AccessorForm, bare, $"仮引数 '{bare.Text}' には型を書いてください (set(float v) か set(v : float))。");
                }
                else if (tokens[i].Text == "set" && parameters.Count == 1 && type is not null
                         && (parameter is [_, { Kind: ShaderSyntaxKind.ColonToken }, { Kind: ShaderSyntaxKind.IdentifierToken } given] ? given
                             : parameter is [{ Kind: ShaderSyntaxKind.IdentifierToken } leading, { Kind: ShaderSyntaxKind.IdentifierToken }] ? leading
                             : null) is { } written
                         && written.Text != type)
                {
                    report(SlangRuleDescriptors.AccessorForm, tokens[i], $"set の仮引数の型 ({written.Text}) が、property の型 ({type}) と違います。");
                }
            }

            i = close;
        }
    }

    /// <summary>インターフェイスの既定の実装と override の対応。</summary>
    private static void CheckOverrides(StructDeclarationSyntax[] types, Dictionary<string, int> kinds, HashSet<string> extended, SlangConformanceModel model, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        Dictionary<string, StructDeclarationSyntax> interfaces = types.Where(t => t.Keyword.Text == "interface" && kinds[t.NameToken!.Text] == 3)
            .GroupBy(t => t.NameToken!.Text, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (StructDeclarationSyntax type in types)
        {
            if (type.Keyword.Text is not ("struct" or "class") || type.ColonToken is null)
            {
                continue;
            }

            // 基底の名前 (BaseType と、続く , の後ろのもの)。
            List<StructDeclarationSyntax> bases = [];

            foreach (ShaderSyntaxToken token in type.DescendantTokens().TakeWhile(t => t.Kind != ShaderSyntaxKind.OpenBraceToken))
            {
                if (interfaces.TryGetValue(token.Text, out StructDeclarationSyntax? found))
                {
                    bases.Add(found);
                }
            }

            // 基底の構造体があれば、そのメソッドを override しうるので見ない。
            if (type.BaseTypeName is { } baseName && kinds.TryGetValue(baseName, out int baseKind) && baseKind != 3)
            {
                continue;
            }

            if (bases.Count == 0)
            {
                continue;
            }

            // インターフェイスが継承したインターフェイスの要件も足す。継承の並びに利用者のインターフェイス (1 度だけ宣言したもの) でないものがあれば、要件は決めない。
            if (Ancestors(bases, interfaces) is not { } all)
            {
                continue;
            }

            FunctionDeclarationSyntax[] required = [.. all.SelectMany(b => b.Members.OfType<FunctionDeclarationSyntax>())];

            // 継承の並びのどこかで本体を書いた (既定の実装のある) 要件は、満たしている。
            HashSet<string> implemented = [.. required.Where(r => r.Body is not null).Select(r => r.Name)];
            required = [.. required.Where(r => r.Body is not null || !implemented.Contains(r.Name))];
            bases = all;

            // インターフェイスの要件 (本体の無いメソッド) を、型が持っているか (E38100)。持っていれば、[mutating] と static が合うか (E38105)。
            // extension で足されうる型とインターフェイス、ジェネリックのインターフェイスは見ない。
            // どの型にも当たる extension (extension<T : IBar> T { … }) は、その制約に適合しうる型にだけ足しうる。
            if (!(extended.Contains("*") && model.MethodExtensionsMayApply(type.NameToken!.Text)) && !extended.Contains(type.NameToken!.Text) && type.Keyword.Text is "struct" or "class"
                && bases.All(b => !extended.Contains(b.NameToken!.Text) && b.GenericParameterTokens.IsDefaultOrEmpty))
            {
                FunctionDeclarationSyntax[] own = [.. type.Members.OfType<FunctionDeclarationSyntax>()];

                foreach (FunctionDeclarationSyntax requirement in required.Where(r => r.Body is null && r.Name != "__init"))
                {
                    FunctionDeclarationSyntax[] candidates = [.. own.Where(m => m.Name == requirement.Name)];

                    if (candidates.Length == 0)
                    {
                        report(SlangRuleDescriptors.InterfaceRequirement, type.NameToken, $"{type.Name} に、インターフェイスの要件 '{requirement.Name}' がありません。");
                        continue;
                    }

                    if (candidates.FirstOrDefault(c => c.ParameterList.Count() == requirement.ParameterList.Count()) is not { } implementation || candidates.Length > 1)
                    {
                        continue;
                    }

                    bool Mutating(FunctionDeclarationSyntax f) => f.Attributes.Any(a => a.Name == "mutating") || f.ModifierTokens.Any(t => t.Text == "mutating");
                    bool Static(FunctionDeclarationSyntax f) => f.ModifierTokens.Any(t => t.Text == "static");

                    if (Mutating(implementation) && !Mutating(requirement))
                    {
                        report(SlangRuleDescriptors.InterfaceRequirement, implementation.NameToken, $"インターフェイスの要件 '{requirement.Name}' は [mutating] でないので、[mutating] のメソッドで満たせません。");
                    }
                    else if (Static(implementation) != Static(requirement))
                    {
                        report(SlangRuleDescriptors.InterfaceRequirement, implementation.NameToken,
                            Static(requirement) ? $"インターフェイスの要件 '{requirement.Name}' は static なので、static でないメソッドで満たせません。" : $"インターフェイスの要件 '{requirement.Name}' は static でないので、static のメソッドで満たせません。");
                    }
                }
            }

            foreach (FunctionDeclarationSyntax method in type.Members.OfType<FunctionDeclarationSyntax>())
            {
                bool marked = method.ModifierTokens.Any(t => t.Text == "override");
                int count = method.ParameterList.Count();

                if (marked && !required.Any(r => r.Name == method.Name && r.ParameterList.Count() == count))
                {
                    report(SlangRuleDescriptors.InheritanceAndOverride, method.NameToken, $"override の '{method.Name}' に当たるメソッド (同じ名前と仮引数の数) が、基底にありません。");
                }
                else if (!marked && required.Any(r => r.Name == method.Name && r.Body is not null && r.ParameterList.Count() == count))
                {
                    report(SlangRuleDescriptors.InheritanceAndOverride, method.NameToken, $"'{method.Name}' はインターフェイスの既定の実装を置き換えるので、override を書いてください。");
                }
            }
        }
    }

    /// <summary>index の ( に対応する ) の位置。</summary>
    private static int? Matching(ImmutableArray<ShaderSyntaxToken> tokens, int index)
    {
        int depth = 0;

        for (int i = index; i < tokens.Length; i++)
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

        return null;
    }

    /// <summary>start から end の前までを、深さ 0 の , で分ける。空なら 0 個。</summary>
    private static List<ShaderSyntaxToken[]> Split(ImmutableArray<ShaderSyntaxToken> tokens, int start, int end)
    {
        List<ShaderSyntaxToken[]> parts = [];
        List<ShaderSyntaxToken> current = [];
        int depth = 0;

        for (int i = start; i < end; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind is ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.LessThanToken)
            {
                depth++;
            }
            else if (token.Kind is ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.GreaterThanToken)
            {
                depth--;
            }
            else if (token.Kind == ShaderSyntaxKind.CommaToken && depth == 0)
            {
                parts.Add([.. current]);
                current.Clear();
                continue;
            }

            current.Add(token);
        }

        if (current.Count > 0)
        {
            parts.Add([.. current]);
        }

        return parts;
    }
}
