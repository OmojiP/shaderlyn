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
/// 基底に同じ名前と仮引数の数のメソッドが無い <c>override</c> (E30854)</item>
/// <item>SG0348: <c>-&gt;</c> で戻り値の型を書かない <c>__subscript</c> (E30901)。property の <c>get</c> などの仮引数 (E31112)、<c>set</c> の 2 つ以上の仮引数 (E31108)、
/// 型を書かない仮引数 (E30621)、property の型と違う <c>set</c> の仮引数の型 (E31109)</item>
/// </list>
/// 構文解析は <c>new</c>・<c>property</c>・<c>__subscript</c> を構文木に残さないので、トークンの並びを読む。
/// </remarks>
internal sealed class SlangTypeDeclarationAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [SlangRuleDescriptors.ClassWithoutNew, SlangRuleDescriptors.InheritanceAndOverride, SlangRuleDescriptors.AccessorForm];

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

            CheckTokens(tokens, kinds, Report);
            CheckOverrides(types, kinds, Report);
        }
    }

    private static void CheckTokens(ImmutableArray<ShaderSyntaxToken> tokens, Dictionary<string, int> kinds, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
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
                CheckBases(tokens, i, kinds, report);
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
        }
    }

    private static void CheckBases(ImmutableArray<ShaderSyntaxToken> tokens, int keyword, Dictionary<string, int> kinds, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
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
    private static void CheckOverrides(StructDeclarationSyntax[] types, Dictionary<string, int> kinds, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
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

            // インターフェイスがさらにインターフェイスを継承していれば、その要件は決めない。
            if (bases.Any(b => b.ColonToken is not null))
            {
                continue;
            }

            FunctionDeclarationSyntax[] required = [.. bases.SelectMany(b => b.Members.OfType<FunctionDeclarationSyntax>())];

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
