using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の private のメンバーと、別のモジュールの public でない宣言の使い方を検査する (SG0401)。
/// </summary>
/// <remarks>
/// <para>
/// 構造体の <c>private</c> のフィールドとメソッドは、その構造体の中 (中に定義した構造体を含む) でだけ使える
/// (slangc 2026.19 の「declaration not accessible」、E30600)。<c>s.member</c> の <c>s</c> の型がファイルの中の構造体のときだけ見る。
/// </para>
/// <para>
/// 別のモジュールの public でない宣言の使用も見る (<see cref="AnalyzeModules"/>)。
/// </para>
/// </remarks>
internal sealed class SlangVisibilityAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.NotAccessible];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            AnalyzeModules(context, compilation, program);
        }

        Dictionary<AnalyzedProgram, Dictionary<string, HashSet<string>>> privatesByProgram = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access)
            {
                continue;
            }

            if (!privatesByProgram.TryGetValue(program, out Dictionary<string, HashSet<string>>? privates))
            {
                privates = privatesByProgram[program] = CollectPrivates(program.Tree.Root);
            }

            if (privates.Count == 0
                || compilation.GetExpressionTypeBinder(program).GetEvaluatorFor(access.Target).Evaluate(access.Target) is not { } type
                || !privates.TryGetValue(type, out HashSet<string>? members)
                || !members.Contains(access.Name)
                || access.Ancestors().OfType<StructDeclarationSyntax>().Any(s => s.NameToken?.Text == type)
                || !compilation.IsReportable(access.NameToken)
                || access.NameToken.GetLocation() is not { } location)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.NotAccessible, location,
                $"'{access.Name}' は {type} の private のメンバーなので、{type} の外からは使えません。"));
        }
    }

    /// <summary>
    /// 別のモジュールの public でない宣言の使用を検査する (E30600)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 関数の外の宣言 (関数・構造体・列挙型・変数) は、書かなければ internal で、宣言したモジュールの中でだけ使える。
    /// public の構造体のメンバーも、そのモジュールの言語が 2025 (既定) なら internal が既定で、2026 (<c>#language slang 2026</c>) なら public が既定である。
    /// <c>module</c> の宣言も可視性の修飾子も無いファイルは、古い書き方のモジュールとして、すべてを public とみなす (slangc 2026.19 で確かめた)。
    /// </para>
    /// <para>
    /// 見るのは、解析するファイル (<c>implementing</c> と <c>__include</c> の無いもの) が使う、取り込んだモジュールのファイル (これらも <c>implementing</c> と <c>__include</c> の無いもの) の宣言だけである。
    /// 同じ名前の宣言がほかで public か、解析するファイルにあれば見ない。
    /// </para>
    /// </remarks>
    private static void AnalyzeModules(SyntaxTreeAnalysisContext context, ShaderCompilation compilation, AnalyzedProgram program)
    {
        string? root = compilation.Text.FilePath;
        ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;

        if (root is null)
        {
            return;
        }

        // ファイルごとの、宣言の語 (module・implementing・__include・可視性)。
        Dictionary<string, HashSet<string>> words = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<ShaderSyntaxToken, int> index = new(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];
            index[token] = i;

            // 可視性の語は、後ろに宣言が続くときだけ数える (Falcor の TinyUniformSampleGenerator internal = {}; のように、変数の名前にもなる)。
            if (token.Kind == ShaderSyntaxKind.IdentifierToken
                && (token.Text is "module" or "implementing" or "__include"
                    || (token.Text is "public" or "internal" or "private" && i + 1 < tokens.Length && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken))
                && token.Source.FilePath is { } path)
            {
                if (!words.TryGetValue(path, out HashSet<string>? set))
                {
                    words[path] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.Add(token.Text);
            }
        }

        bool IsSplit(string path) => words.TryGetValue(path, out HashSet<string>? set) && (set.Contains("implementing") || set.Contains("__include"));

        if (IsSplit(root))
        {
            return;
        }

        // 別のモジュールで、可視性を読めるファイル (古い書き方でなく、分けて書いていないもの)。
        bool IsForeign(string? path) => path is not null
                                        && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                                        && words.TryGetValue(path, out HashSet<string>? set)
                                        && !IsSplit(path)
                                        && (set.Contains("module") || set.Contains("public") || set.Contains("internal") || set.Contains("private"));

        bool IsPublic(ShaderSyntaxToken keyword, ImmutableArray<ShaderSyntaxToken> modifiers)
        {
            if (modifiers.Any(t => t.Text == "public"))
            {
                return true;
            }

            // 構造体と列挙型の宣言は修飾子を持たないので、キーワードの前の語を、前の宣言の終わり (; { }) まで見る
            // (属性 [ … ] と、修飾子の引数 ( … ) は越える。dyn・export などのほかの修飾子も越える)。
            if (!index.TryGetValue(keyword, out int at))
            {
                return true;
            }

            int depth = 0;

            for (int i = at - 1; i >= 0; i--)
            {
                ShaderSyntaxToken previous = tokens[i];

                if (previous.Kind is ShaderSyntaxKind.CloseBracketToken or ShaderSyntaxKind.CloseParenToken)
                {
                    depth++;
                }
                else if (previous.Kind is ShaderSyntaxKind.OpenBracketToken or ShaderSyntaxKind.OpenParenToken)
                {
                    depth--;
                }
                else if (depth == 0)
                {
                    if (previous.Text == "public")
                    {
                        return true;
                    }

                    if (previous.Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.CloseBraceToken
                        || !string.Equals(previous.Source.FilePath, keyword.Source.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            return false;
        }

        // 名前ごとの、使えない宣言と使える宣言。
        HashSet<string> hidden = new(StringComparer.Ordinal);
        HashSet<string> visible = new(StringComparer.Ordinal);
        Dictionary<string, string> hiddenFile = new(StringComparer.Ordinal);
        Dictionary<string, (string File, bool Since2026, HashSet<string> Hidden)> foreignStructs = new(StringComparer.Ordinal);

        foreach (DeclarationSyntax declaration in program.Tree.Root.Declarations)
        {
            (IEnumerable<string> names, ShaderSyntaxToken? keyword, ImmutableArray<ShaderSyntaxToken> modifiers) = declaration switch
            {
                FunctionDeclarationSyntax function => ([function.Name], function.NameToken, function.ModifierTokens),
                VariableDeclarationSyntax variable => (variable.Variables.Select(v => v.Name), variable.FirstToken, variable.ModifierTokens),
                StructDeclarationSyntax { NameToken: { } name } structure => ([name.Text], structure.Keyword, []),
                EnumDeclarationSyntax { NameToken: { } name } enumeration => ([name.Text], enumeration.Keyword, []),
                _ => ([], null, []),
            };

            if (keyword is null)
            {
                continue;
            }

            string? file = keyword.Source.FilePath;
            bool foreign = IsForeign(file);
            bool isPublic = !foreign || IsPublic(keyword, modifiers);

            foreach (string name in names)
            {
                if (isPublic)
                {
                    visible.Add(name);
                }
                else
                {
                    hidden.Add(name);
                    hiddenFile.TryAdd(name, Path.GetFileName(file!));
                }
            }

            // public の構造体の、使えないメンバー。
            // インターフェイスのメンバーは、インターフェイスと同じ可視性である。
            if (foreign && isPublic && declaration is StructDeclarationSyntax { NameToken: { } structName, Keyword.Text: not "interface" } publicStruct)
            {
                bool since2026 = System.Text.RegularExpressions.Regex.IsMatch(keyword.Source.ToString(), @"^\s*#\s*language\s+slang\s+(2026|latest)\b", System.Text.RegularExpressions.RegexOptions.Multiline);
                HashSet<string> hiddenMembers = new(StringComparer.Ordinal);
                HashSet<string> visibleMembers = new(StringComparer.Ordinal);

                foreach (DeclarationSyntax member in publicStruct.Members)
                {
                    (IEnumerable<string> memberNames, ImmutableArray<ShaderSyntaxToken> memberModifiers) = member switch
                    {
                        VariableDeclarationSyntax field => (field.Variables.Select(v => v.Name), field.ModifierTokens),
                        FunctionDeclarationSyntax method => ([method.Name], method.ModifierTokens),
                        _ => ([], []),
                    };

                    bool memberHidden = memberModifiers.Any(t => t.Text is "internal" or "private")
                                        || (!since2026 && !memberModifiers.Any(t => t.Text == "public"));
                    (memberHidden ? hiddenMembers : visibleMembers).UnionWith(memberNames);
                }

                hiddenMembers.ExceptWith(visibleMembers);

                if (hiddenMembers.Count > 0 && !foreignStructs.TryAdd(structName.Text, (Path.GetFileName(file!), since2026, hiddenMembers)))
                {
                    foreignStructs[structName.Text] = (string.Empty, since2026, []);
                }
            }
        }

        hidden.ExceptWith(visible);

        if (hidden.Count == 0 && foreignStructs.Count == 0)
        {
            return;
        }

        SlangOpenTypes open = SlangOpenTypes.Of(compilation);
        ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

        void Report(ShaderSyntaxToken token, string message)
        {
            if (compilation.IsReportable(token) && token.GetLocation() is { } location)
            {
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.NotAccessible, location, message));
            }
        }

        foreach (ShaderSyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case IdentifierExpressionSyntax identifier
                    when hidden.Contains(identifier.Name)
                         && identifier.Parent is not MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.ColonColonToken }
                         && !(identifier.Parent is MemberAccessExpressionSyntax parentAccess && ReferenceEquals(parentAccess.NameToken, identifier.FirstToken))
                         && binder.ResolveName(identifier).Kind is DeclaredNameKind.NotFound or DeclaredNameKind.Global:
                    Report(identifier.FirstToken!, $"'{identifier.Name}' は別のモジュール ({hiddenFile[identifier.Name]}) の public でない宣言なので、このモジュールからは使えません。");
                    break;

                case TypeSyntax { QualifierTokens.IsEmpty: true, NameToken: { } typeName } when hidden.Contains(typeName.Text):
                    Report(typeName, $"'{typeName.Text}' は別のモジュール ({hiddenFile[typeName.Text]}) の public でない宣言なので、このモジュールからは使えません。");
                    break;

                case MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access
                    when foreignStructs.Count > 0
                         && binder.GetEvaluatorFor(access.Target).Evaluate(access.Target) is { } type
                         && foreignStructs.TryGetValue(type, out (string File, bool Since2026, HashSet<string> Hidden) info)
                         && info.Hidden.Contains(access.Name)
                         && !open.Contains(type):
                    Report(access.NameToken, $"'{access.Name}' は別のモジュール ({info.File}) の {type} の public でないメンバーなので、このモジュールからは使えません。");
                    break;
            }
        }
    }

    /// <summary>構造体ごとの private のメンバーの名前。同じ名前の public のメンバー (多重定義) があれば外す。</summary>
    private static Dictionary<string, HashSet<string>> CollectPrivates(CompilationUnitSyntax root)
    {
        Dictionary<string, HashSet<string>> privates = new(StringComparer.Ordinal);

        foreach (StructDeclarationSyntax structure in root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
        {
            if (structure.NameToken is not { } name)
            {
                continue;
            }

            HashSet<string> hidden = new(StringComparer.Ordinal);
            HashSet<string> visible = new(StringComparer.Ordinal);

            foreach (DeclarationSyntax member in structure.Members)
            {
                (IEnumerable<string> names, ImmutableArray<ShaderSyntaxToken> modifiers) = member switch
                {
                    VariableDeclarationSyntax field => (field.Variables.Select(v => v.Name), field.ModifierTokens),
                    FunctionDeclarationSyntax method => ([method.Name], method.ModifierTokens),
                    _ => ([], []),
                };

                (modifiers.Any(t => t.Text == "private") ? hidden : visible).UnionWith(names);
            }

            hidden.ExceptWith(visible);

            if (hidden.Count > 0)
            {
                // 同じ名前の構造体が 2 つあれば (名前空間が違うなど)、どちらか決められない。
                if (!privates.TryAdd(name.Text, hidden))
                {
                    privates[name.Text] = [];
                }
            }
        }

        return privates;
    }
}
