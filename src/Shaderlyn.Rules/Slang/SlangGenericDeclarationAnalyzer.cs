using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang のジェネリックの仮引数の書き方を検査する (SG0358)。インターフェイスの要件の初期値 (SG0101) と、型引数を書かないジェネリックの型 (SG0358) も見る。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>型を書かない値の仮引数 (<c>let N</c>。E30625) と、整数・列挙型・bool でない型の値の仮引数 (<c>let F : float</c>。E30624)</item>
/// <item>制約に書けない型 (<c>&lt;D : int&gt;</c>・<c>where D : int</c>・構造体の名前。E30401)。値の仮引数なら let と書く</item>
/// <item>制約の型引数が、後ろの仮引数を指すもの (<c>&lt;Foo : IFoo&lt;T&gt;, T : IFloat&gt;</c>。E30117)</item>
/// <item>extension の型の仮引数のうち、拡張する型に出てこないもの (<c>extension&lt;T&gt; int</c>。E30855)</item>
/// <item>インターフェイスの static const の要件の初期値 (E30623。SG0101)</item>
/// <item>型引数を書かない Buffer・Tuple と、既定の型引数の無い利用者のジェネリックの型 (E30408)</item>
/// </list>
/// 構文解析はジェネリックの仮引数と where を構文木に残さないので、トークンの並びを読む。
/// </remarks>
internal sealed class SlangGenericDeclarationAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>ジェネリックの仮引数 1 つ。</summary>
    private sealed record Parameter(ShaderSyntaxToken Name, bool Value, bool Pack, List<ShaderSyntaxToken> Constraint, bool HasDefault);

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.GenericParameterForm, SlangRuleDescriptors.DeclarationNotAllowed];

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
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken at, string message)
            {
                if (!at.IsMissing && compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
                }
            }

            Dictionary<string, string> kinds = new(StringComparer.Ordinal);

            foreach (StructDeclarationSyntax type in root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
            {
                if (type.NameToken is { } name)
                {
                    kinds[name.Text] = kinds.ContainsKey(name.Text) ? "?" : type.Keyword.Text;
                }
            }

            // 既定の型引数の無いジェネリックの型 (利用者が宣言したもの)。
            Dictionary<string, bool> requiresArguments = new(StringComparer.Ordinal);

            for (int i = 0; i + 2 < tokens.Length; i++)
            {
                ShaderSyntaxToken token = tokens[i];

                if (token.Kind != ShaderSyntaxKind.IdentifierToken)
                {
                    continue;
                }

                bool typeDeclaration = token.Text is "struct" or "class" or "interface" && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken
                                       && tokens[i + 2].Kind == ShaderSyntaxKind.LessThanToken;
                bool extension = token.Text is "extension" && tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken;
                bool function = tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken && i > 0 && tokens[i - 1].Kind == ShaderSyntaxKind.IdentifierToken
                                && tokens[i - 1].Text is not ("return" or "new" or "struct" or "class" or "interface" or "extension" or "typealias" or "case" or "else");
                int open = typeDeclaration ? i + 2 : i + 1;

                if (!typeDeclaration && !extension && !function)
                {
                    continue;
                }

                if (ReadParameters(tokens, open) is not { } read)
                {
                    continue;
                }

                (List<Parameter> parameters, int close) = read;

                // 関数は、仮引数の並びの ( が続くものだけ。
                if (function && (close >= tokens.Length || tokens[close].Kind != ShaderSyntaxKind.OpenParenToken))
                {
                    continue;
                }

                if (typeDeclaration)
                {
                    requiresArguments[tokens[i + 1].Text] = !requiresArguments.ContainsKey(tokens[i + 1].Text) && parameters.Any(p => !p.HasDefault && !p.Pack);
                }

                CheckParameters(parameters, kinds, binder, Report);

                // where D : int (宣言の後ろの where)
                int end = close;

                if (function && Matching(tokens, close) is { } parenClose)
                {
                    end = parenClose + 1;
                }

                for (int j = end; j + 3 < tokens.Length && tokens[j].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken); j++)
                {
                    if (tokens[j].Text == "where" && tokens[j + 1].Kind == ShaderSyntaxKind.IdentifierToken && tokens[j + 2].Kind == ShaderSyntaxKind.ColonToken
                        && parameters.FirstOrDefault(p => p.Name.Text == tokens[j + 1].Text && !p.Value) is { } constrained
                        && tokens[j + 3].Kind == ShaderSyntaxKind.IdentifierToken
                        && DescribeNonInterface(tokens[j + 3].Text, kinds, binder) is { } what)
                    {
                        Report(SlangRuleDescriptors.GenericParameterForm, tokens[j + 3], $"{tokens[j + 3].Text} は{what}なので、制約に書けません。{(what == "組み込みの型" ? $"値の仮引数なら let {constrained.Name.Text} : {tokens[j + 3].Text} と書いてください。" : "インターフェイスを書いてください。")}");
                    }
                }

                // extension<T> int (拡張する型に出てこない型の仮引数)
                if (extension)
                {
                    List<string> target = [];

                    for (int j = close; j < tokens.Length && tokens[j].Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.SemicolonToken) && tokens[j].Text != "where"; j++)
                    {
                        target.Add(tokens[j].Text);
                    }

                    // 拡張する型に出てくる仮引数の制約の中に出てくる仮引数 (extension<T, U : IBuf<T>> U の T) も、決まるので通る。
                    HashSet<string> referenced = [.. parameters.Select(p => p.Name.Text).Where(target.Contains)];
                    bool grew = true;

                    while (grew)
                    {
                        grew = false;

                        foreach (Parameter known in parameters.Where(p => referenced.Contains(p.Name.Text)).ToArray())
                        {
                            foreach (ShaderSyntaxToken used in known.Constraint)
                            {
                                grew |= parameters.Any(p => p.Name.Text == used.Text) && referenced.Add(used.Text);
                            }
                        }
                    }

                    foreach (Parameter parameter in parameters.Where(p => !referenced.Contains(p.Name.Text)))
                    {
                        Report(SlangRuleDescriptors.GenericParameterForm, parameter.Name,
                            $"extension の型の仮引数 {parameter.Name.Text} が、拡張する型 ({string.Join(" ", target)}) に出てきません。");
                    }
                }
            }

            foreach (ShaderSyntaxNode node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    // interface I { static const int x = 0; }
                    case VariableDeclarationSyntax { Parent: StructDeclarationSyntax { Keyword.Text: "interface" } } requirement:
                        foreach (VariableDeclaratorSyntax variable in requirement.Variables.Where(v => v.Initializer is not null))
                        {
                            Report(SlangRuleDescriptors.DeclarationNotAllowed, variable.NameToken, $"インターフェイスの要件 '{variable.Name}' に初期値は書けません。値は、適合する型の側で決めてください。");
                        }

                        break;

                    // Buffer b0;・Tuple t0;・IProcessor obj (型引数を書かないジェネリックの型)
                    case TypeSyntax { OpenAngle: null, IsPointer: false } bare
                        when bare.Parent is VariableDeclarationSyntax or ParameterSyntax
                             && bare.NestedTypeTokens.IsDefaultOrEmpty && bare.QualifierTokens.IsDefaultOrEmpty
                             && (bare.NameToken.Text is "Tuple" or "Buffer" or "RWBuffer"
                                 || (requiresArguments.TryGetValue(bare.NameToken.Text, out bool required) && required))
                             && !program.Tree.Root.TypeAliases.ContainsKey(bare.NameToken.Text):
                        Report(SlangRuleDescriptors.GenericParameterForm, bare.NameToken, $"ジェネリックの型 {bare.NameToken.Text} には型引数を書いてください ({bare.NameToken.Text}<…>)。");
                        break;
                }
            }
        }
    }

    private static void CheckParameters(List<Parameter> parameters, Dictionary<string, string> kinds, ExpressionTypeBinder binder, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            Parameter parameter = parameters[i];

            if (parameter.Value)
            {
                if (parameter.Constraint.Count == 0)
                {
                    report(SlangRuleDescriptors.GenericParameterForm, parameter.Name, $"値の仮引数 {parameter.Name.Text} には型を書いてください (let {parameter.Name.Text} : int)。");
                }
                else if (parameter.Constraint is [{ Text: "float" or "double" or "half" } floating])
                {
                    report(SlangRuleDescriptors.GenericParameterForm, floating, $"値の仮引数の型に {floating.Text} は使えません。整数か列挙型か bool にしてください。");
                }

                continue;
            }

            if (parameter.Constraint is [{ Kind: ShaderSyntaxKind.IdentifierToken } single] && DescribeNonInterface(single.Text, kinds, binder) is { } what)
            {
                report(SlangRuleDescriptors.GenericParameterForm, single, what == "組み込みの型"
                    ? $"{single.Text} は制約に書けません。値の仮引数なら let {(parameter.Pack ? "each " : string.Empty)}{parameter.Name.Text} : {single.Text} と書いてください。"
                    : $"{single.Text} は{what}なので、制約に書けません。インターフェイスを書いてください。");
                continue;
            }

            // 後ろの仮引数を指す制約 (IFoo<T> の T が後ろの仮引数)
            HashSet<string> later = [.. parameters.Skip(i + 1).Select(p => p.Name.Text)];

            if (parameter.Constraint.FirstOrDefault(t => t.Kind == ShaderSyntaxKind.IdentifierToken && later.Contains(t.Text)) is { } forward)
            {
                report(SlangRuleDescriptors.GenericParameterForm, forward, $"制約の中の {forward.Text} は、後ろで宣言する仮引数です。{forward.Text} を {parameter.Name.Text} より前に書いてください。");
            }
        }
    }

    /// <summary>インターフェイスでない型の説明 (組み込みの型・構造体)。インターフェイスか分からなければ <see langword="null"/>。</summary>
    private static string? DescribeNonInterface(string name, Dictionary<string, string> kinds, ExpressionTypeBinder binder)
    {
        if (kinds.TryGetValue(name, out string? kind))
        {
            return kind is "struct" or "class" ? "構造体" : null;
        }

        return binder.TypeSystem.TryDescribeNumeric(name, out _) || name == "bool" ? "組み込みの型" : null;
    }

    /// <summary><c>&lt;…&gt;</c> の仮引数を読む。仮引数の並びでなければ <see langword="null"/>。</summary>
    private static (List<Parameter> Parameters, int Close)? ReadParameters(ImmutableArray<ShaderSyntaxToken> tokens, int open)
    {
        if (open >= tokens.Length || tokens[open].Kind != ShaderSyntaxKind.LessThanToken)
        {
            return null;
        }

        List<List<ShaderSyntaxToken>> parts = [[]];
        int depth = 0;
        int i = open;

        for (; i < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind == ShaderSyntaxKind.LessThanToken && depth++ == 0)
            {
                continue;
            }

            if (token.Kind == ShaderSyntaxKind.GreaterThanToken && --depth == 0)
            {
                break;
            }

            // IFoo<T>> の >> (2 つの閉じ)
            if (token.Kind == ShaderSyntaxKind.GreaterThanGreaterThanToken)
            {
                depth -= 2;

                if (depth <= 0)
                {
                    break;
                }
            }

            if (token.Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.CloseParenToken)
            {
                return null;
            }

            if (token.Kind == ShaderSyntaxKind.CommaToken && depth == 1)
            {
                parts.Add([]);
                continue;
            }

            parts[^1].Add(token);
        }

        List<Parameter> parameters = [];

        foreach (List<ShaderSyntaxToken> part in parts)
        {
            int k = 0;
            bool value = false;
            bool pack = false;

            while (k < part.Count && part[k].Text is "let" or "each")
            {
                value |= part[k].Text == "let";
                pack |= part[k].Text == "each";
                k++;
            }

            if (k >= part.Count || part[k].Kind != ShaderSyntaxKind.IdentifierToken)
            {
                return null;
            }

            ShaderSyntaxToken name = part[k++];
            List<ShaderSyntaxToken> constraint = [];
            bool hasDefault = false;

            if (k < part.Count && part[k].Kind == ShaderSyntaxKind.ColonToken)
            {
                for (k++; k < part.Count && part[k].Kind != ShaderSyntaxKind.EqualsToken; k++)
                {
                    constraint.Add(part[k]);
                }
            }

            if (k < part.Count && part[k].Kind == ShaderSyntaxKind.EqualsToken)
            {
                hasDefault = true;
            }
            else if (k < part.Count)
            {
                return null;
            }

            parameters.Add(new Parameter(name, value, pack, constraint, hasDefault));
        }

        return parameters.Count == 0 ? null : (parameters, i + 1);
    }

    private static int? Matching(ImmutableArray<ShaderSyntaxToken> tokens, int open)
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

        return null;
    }
}
