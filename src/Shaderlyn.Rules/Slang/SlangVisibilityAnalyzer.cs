using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
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
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.NotAccessible, SlangRuleDescriptors.InvalidVisibility];

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
            AnalyzeDeclaredVisibility(context, compilation, program);
        }

        Dictionary<AnalyzedProgram, Dictionary<string, HashSet<string>>> privatesByProgram = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            // private の __subscript の添字 (t[0])。
            if (node is ElementAccessExpressionSyntax element)
            {
                if (!privatesByProgram.TryGetValue(program, out Dictionary<string, HashSet<string>>? subscriptPrivates))
                {
                    subscriptPrivates = privatesByProgram[program] = CollectPrivates(program.Tree.Root, program.Tree.PreprocessResult.Tokens);
                }

                if (subscriptPrivates.Count > 0
                    && compilation.GetExpressionTypeBinder(program).GetEvaluatorFor(element.Target).Evaluate(element.Target) is { } indexedType
                    && subscriptPrivates.TryGetValue(indexedType, out HashSet<string>? indexedMembers)
                    && indexedMembers.Contains("__subscript")
                    && !element.Ancestors().OfType<StructDeclarationSyntax>().Any(s => s.NameToken?.Text == indexedType)
                    && compilation.IsReportable(element.OpenBracket)
                    && element.OpenBracket.GetLocation() is { } subscriptLocation)
                {
                    context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.NotAccessible, subscriptLocation,
                        $"{indexedType} の __subscript は private なので、{indexedType} の外からは添字を使用できません。"));
                }

                continue;
            }

            if (node is not MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access)
            {
                continue;
            }

            if (!privatesByProgram.TryGetValue(program, out Dictionary<string, HashSet<string>>? privates))
            {
                privates = privatesByProgram[program] = CollectPrivates(program.Tree.Root, program.Tree.PreprocessResult.Tokens);
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
                $"'{access.Name}' は {type} の private のメンバーなので、{type} の外からは使用できません。"));
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
    /// 見るのは、解析するファイル (<c>implementing</c> と <c>__include</c> の無いもの) が使う、取り込んだモジュールのファイルの宣言である。
    /// 取り込んだモジュールを <c>__include</c> と <c>implementing</c> で分けて書いていれば、その各部分のファイルの宣言も見る。
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

        // 別のモジュールで、可視性を読めるファイル (古い書き方でないもの)。__include と implementing で分けて書いたモジュールの各部分も含む。
        bool IsForeign(string? path) => path is not null
                                        && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                                        && words.TryGetValue(path, out HashSet<string>? set)
                                        && (set.Contains("module") || set.Contains("implementing") || set.Contains("public") || set.Contains("internal") || set.Contains("private"));

        bool IsPublic(ShaderSyntaxToken keyword, ImmutableArray<ShaderSyntaxToken> modifiers)
            => modifiers.Any(t => t.Text == "public") || VisibilityBefore(keyword, tokens, index) is null or "public";

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

            // 別のモジュールの public の名前空間の、public でないメンバー (Namespace.internalVar)。
            if (declaration is NamespaceDeclarationSyntax { NameToken: { } namespaceName } space && IsForeign(namespaceName.Source.FilePath)
                && VisibilityBefore(space.Keyword, tokens, index) == "public")
            {
                HashSet<string> hiddenMembers = new(StringComparer.Ordinal);
                HashSet<string> visibleMembers = new(StringComparer.Ordinal);

                foreach (DeclarationSyntax member in space.Members)
                {
                    (IEnumerable<string> memberNames, bool? memberPublic) = member switch
                    {
                        VariableDeclarationSyntax field => (field.Variables.Select(v => v.Name), (bool?)field.ModifierTokens.Any(t => t.Text == "public")),
                        FunctionDeclarationSyntax method => ([method.Name], method.ModifierTokens.Any(t => t.Text == "public")),
                        StructDeclarationSyntax { NameToken: { } n } nested => ([n.Text], VisibilityBefore(nested.Keyword, tokens, index) is { } v ? v == "public" : null),
                        EnumDeclarationSyntax { NameToken: { } n } nested => ([n.Text], VisibilityBefore(nested.Keyword, tokens, index) is { } v ? v == "public" : null),
                        _ => ([], null),
                    };

                    if (memberPublic is { } known)
                    {
                        (known ? visibleMembers : hiddenMembers).UnionWith(memberNames);
                    }
                }

                hiddenMembers.ExceptWith(visibleMembers);

                if (hiddenMembers.Count > 0 && !foreignStructs.TryAdd(namespaceName.Text, (Path.GetFileName(namespaceName.Source.FilePath!), false, hiddenMembers)))
                {
                    foreignStructs[namespaceName.Text] = (string.Empty, false, []);
                }

                continue;
            }

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

                    // 中の構造体と列挙型 (PublicOuter.Hidden)。修飾子は構文木に残らないので、キーワードの前の語を見る。
                    (string? nestedName, ShaderSyntaxToken? nestedKeyword) = member switch
                    {
                        StructDeclarationSyntax { NameToken: { } n } nested => (n.Text, nested.Keyword),
                        EnumDeclarationSyntax { NameToken: { } n } nested => (n.Text, nested.Keyword),
                        _ => (null, null),
                    };

                    if (nestedName is not null && VisibilityBefore(nestedKeyword!, tokens, index) is { } nestedVisibility)
                    {
                        bool nestedHidden = nestedVisibility is "internal" or "private" || (nestedVisibility.Length == 0 && !since2026);
                        (nestedHidden ? hiddenMembers : visibleMembers).Add(nestedName);
                    }
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
                    Report(identifier.FirstToken!, $"'{identifier.Name}' は別のモジュール ({hiddenFile[identifier.Name]}) の public でない宣言なので、このモジュールからは使用できません。");
                    break;

                case TypeSyntax { QualifierTokens.IsEmpty: true, NameToken: { } typeName } when hidden.Contains(typeName.Text):
                    Report(typeName, $"'{typeName.Text}' は別のモジュール ({hiddenFile[typeName.Text]}) の public でない宣言なので、このモジュールからは使用できません。");
                    break;

                // B(0.0) (別のモジュールの構造体の、public でない __init の呼び出し)。
                case InvocationExpressionSyntax { TargetName: { } constructed } construction
                    when foreignStructs.TryGetValue(constructed, out (string File, bool Since2026, HashSet<string> Hidden) constructedInfo)
                         && constructedInfo.Hidden.Contains("__init")
                         && construction.Target.FirstToken is { } constructedToken:
                    Report(constructedToken, $"{constructed} の __init は別のモジュール ({constructedInfo.File}) の public でない宣言なので、このモジュールからは呼べません。");
                    break;

                // PublicOuter.Hidden.A・Namespace.internalVar (型と名前空間の名前からの、public でないメンバー)。
                case MemberAccessExpressionSyntax { Target: IdentifierExpressionSyntax owner } ownerAccess
                    when foreignStructs.TryGetValue(owner.Name, out (string File, bool Since2026, HashSet<string> Hidden) ownerInfo)
                         && binder.ResolveName(owner).Kind == DeclaredNameKind.NotFound:
                    if (ownerInfo.Hidden.Contains(ownerAccess.Name))
                    {
                        Report(ownerAccess.NameToken, $"'{ownerAccess.Name}' は別のモジュール ({ownerInfo.File}) の {owner.Name} の public でないメンバーなので、このモジュールからは使用できません。");
                    }

                    break;

                // PublicOuter.InternalInner x (型の名前で修飾した、public でない中の型)。
                case TypeSyntax { QualifierTokens: [.., { Kind: ShaderSyntaxKind.IdentifierToken } qualifier, _], NameToken: { } nestedType }
                    when foreignStructs.TryGetValue(qualifier.Text, out (string File, bool Since2026, HashSet<string> Hidden) qualifierInfo)
                         && qualifierInfo.Hidden.Contains(nestedType.Text):
                    Report(nestedType, $"'{nestedType.Text}' は別のモジュール ({qualifierInfo.File}) の {qualifier.Text} の public でない宣言なので、このモジュールからは使用できません。");
                    break;

                case MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access
                    when foreignStructs.Count > 0
                         && binder.GetEvaluatorFor(access.Target).Evaluate(access.Target) is { } type
                         && foreignStructs.TryGetValue(type, out (string File, bool Since2026, HashSet<string> Hidden) info)
                         && info.Hidden.Contains(access.Name)
                         && !open.Contains(type):
                    Report(access.NameToken, $"'{access.Name}' は別のモジュール ({info.File}) の {type} の public でないメンバーなので、このモジュールからは使用できません。");
                    break;
            }
        }
    }

    /// <summary>
    /// 解析するファイルの宣言に書いた可視性を検査する (SG0402)。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>関数の外の宣言の <c>private</c> (E30603。private は型のメンバーにだけ書ける)</item>
    /// <item>public でない構造体の、public のメンバー (E30601。メンバーは親より見えるようにできない)</item>
    /// <item>public の宣言 (関数・変数・typealias・public の構造体の public のメンバー) が、このファイルの public でない型を使うもの (E30604)。
    /// public の構造体の public のフィールドなら、構造体の名前でも報告する (slangc と同じ。メソッドのときは報告しない)</item>
    /// </list>
    /// 可視性を何も書かないファイルは、どれも起こらない。
    /// </remarks>
    private static void AnalyzeDeclaredVisibility(SyntaxTreeAnalysisContext context, ShaderCompilation compilation, AnalyzedProgram program)
    {
        string? root = compilation.Text.FilePath;
        ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;

        if (root is null || !tokens.Any(t => t.Text is "public" or "private" && string.Equals(t.Source.FilePath, root, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Dictionary<ShaderSyntaxToken, int> index = new(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < tokens.Length; i++)
        {
            index[tokens[i]] = i;
        }

        bool InRoot(ShaderSyntaxToken token) => string.Equals(token.Source.FilePath, root, StringComparison.OrdinalIgnoreCase);

        string Visibility(ShaderSyntaxToken keyword, ImmutableArray<ShaderSyntaxToken> modifiers)
            => modifiers.FirstOrDefault(t => t.Text is "public" or "internal" or "private")?.Text ?? VisibilityBefore(keyword, tokens, index) ?? "public";

        void Report(ShaderSyntaxToken? token, string message)
        {
            if (token is { IsMissing: false } && compilation.IsReportable(token) && token.GetLocation() is { } location)
            {
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.InvalidVisibility, location, message));
            }
        }

        // このファイルの、public でない型 (構造体・列挙型・インターフェイス・typealias)。同じ名前の public の宣言があれば外す。
        HashSet<string> lessVisible = new(StringComparer.Ordinal);
        HashSet<string> publicTypes = new(StringComparer.Ordinal);

        foreach (ShaderSyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
        {
            (ShaderSyntaxToken? keyword, string? name) = node switch
            {
                StructDeclarationSyntax { NameToken: { } structName } structure => (structure.Keyword, structName.Text),
                EnumDeclarationSyntax { NameToken: { } enumName } enumeration => (enumeration.Keyword, enumName.Text),
                IncompleteDeclarationSyntax { Tokens: [{ Text: "typealias" } alias, { Kind: ShaderSyntaxKind.IdentifierToken } aliasName, ..] } => (alias, aliasName.Text),
                _ => (null, null),
            };

            if (keyword is null || name is null)
            {
                continue;
            }

            (InRoot(keyword) && Visibility(keyword, []) != "public" ? lessVisible : publicTypes).Add(name);
        }

        lessVisible.ExceptWith(publicTypes);

        string? LessVisible(TypeSyntax? type)
            => type is { QualifierTokens.IsEmpty: true, NameToken: { } name } && lessVisible.Contains(name.Text) ? name.Text : null;

        void CheckSignature(FunctionDeclarationSyntax function)
        {
            if (LessVisible(function.ReturnType) is { } returned)
            {
                Report(function.ReturnType.NameToken, $"public の '{function.Name}' の戻り値の型 {returned} は public ではありません。");
            }

            foreach (ParameterSyntax parameter in function.ParameterList)
            {
                if (LessVisible(parameter.Type) is { } parameterType)
                {
                    Report(parameter.Type.NameToken, $"public の '{function.Name}' の仮引数の型 {parameterType} は public ではありません。");
                }
            }
        }

        foreach (ShaderSyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
        {
            if (node is not DeclarationSyntax declaration || declaration.FirstToken is not { } first || !InRoot(first))
            {
                continue;
            }

            bool topLevel = declaration.Parent is CompilationUnitSyntax or Hlsl.Syntax.NamespaceDeclarationSyntax;

            switch (declaration)
            {
                case FunctionDeclarationSyntax function when topLevel:
                {
                    string visibility = Visibility(function.NameToken, function.ModifierTokens);

                    if (visibility == "private")
                    {
                        Report(function.ModifierTokens.First(t => t.Text == "private"), $"'{function.Name}' は型のメンバーではないので、private にできません。");
                    }
                    else if (visibility == "public")
                    {
                        CheckSignature(function);
                    }

                    break;
                }

                case VariableDeclarationSyntax variable when topLevel:
                {
                    if (variable.ModifierTokens.FirstOrDefault(t => t.Text == "private") is { } privateToken)
                    {
                        Report(privateToken, "関数の外の変数は型のメンバーではないので、private にできません。");
                    }
                    else if (variable.ModifierTokens.Any(t => t.Text == "public") && LessVisible(variable.Type) is { } variableType)
                    {
                        Report(variable.Type.NameToken, $"public の変数の型 {variableType} は public ではありません。");
                    }

                    break;
                }

                case IncompleteDeclarationSyntax { Tokens: [{ Text: "typealias" } alias, ..] incomplete } when topLevel && Visibility(alias, []) == "public":
                    foreach (ShaderSyntaxToken target in incomplete.SkipWhile(t => t.Kind != ShaderSyntaxKind.EqualsToken).Where(t => lessVisible.Contains(t.Text)))
                    {
                        Report(target, $"public の typealias が指す型 {target.Text} は public ではありません。");
                    }

                    break;

                case StructDeclarationSyntax { NameToken: { } structName, Keyword.Text: "struct" or "class" } structure:
                {
                    string visibility = Visibility(structure.Keyword, []);

                    if (topLevel && visibility == "private")
                    {
                        Report(structure.Keyword, $"'{structName.Text}' は型のメンバーではないので、private にできません。");
                        break;
                    }

                    bool exposesLessVisible = false;

                    foreach (DeclarationSyntax member in structure.Members)
                    {
                        // public property IS ii2 { get { … } } (E30604)。property は構文木では読み飛ばした宣言になる。
                        if (visibility == "public"
                            && member is IncompleteDeclarationSyntax { Tokens: var propertyTokens }
                            && propertyTokens.IndexOf(propertyTokens.FirstOrDefault(t => t.Text == "property")!) is var keywordAt and >= 0
                            && keywordAt + 2 < propertyTokens.Length
                            && (propertyTokens.Take(keywordAt).Any(t => t.Text == "public") || VisibilityBefore(propertyTokens[keywordAt], tokens, index) == "public")
                            && propertyTokens[keywordAt + 1] is { Kind: ShaderSyntaxKind.IdentifierToken } propertyType
                            && lessVisible.Contains(propertyType.Text)
                            && propertyTokens[keywordAt + 2] is { Kind: ShaderSyntaxKind.IdentifierToken } propertyName)
                        {
                            Report(propertyName, $"public の property '{propertyName.Text}' の型 {propertyType.Text} は public ではありません。");
                            continue;
                        }

                        (ShaderSyntaxToken? memberName, ImmutableArray<ShaderSyntaxToken> modifiers) = member switch
                        {
                            VariableDeclarationSyntax field => (field.Variables.FirstOrDefault()?.NameToken, field.ModifierTokens),
                            FunctionDeclarationSyntax method => (method.NameToken, method.ModifierTokens),
                            _ => (null, []),
                        };

                        if (memberName is null || !modifiers.Any(t => t.Text == "public"))
                        {
                            continue;
                        }

                        if (visibility != "public")
                        {
                            Report(memberName, $"'{memberName.Text}' を public にしていますが、親の {structName.Text} が public ではありません。メンバーの公開範囲は親より広くできません。");
                            continue;
                        }

                        if (member is VariableDeclarationSyntax publicField && LessVisible(publicField.Type) is { } fieldType)
                        {
                            Report(publicField.Type.NameToken, $"public のフィールド '{memberName.Text}' の型 {fieldType} は public ではありません。");
                            exposesLessVisible = true;
                        }
                        else if (member is FunctionDeclarationSyntax publicMethod)
                        {
                            CheckSignature(publicMethod);
                        }
                    }

                    if (exposesLessVisible)
                    {
                        Report(structName, $"public の {structName.Text} の public のメンバーが、public でない型を使用しています。");
                    }

                    break;
                }
            }
        }
    }

    /// <summary>
    /// 宣言のキーワードの前に書いた可視性 (public・internal・private)。書いていなければ空文字列、前の語を引けなければ <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 構造体と列挙型の宣言は修飾子を持たないので、キーワードの前の語を、前の宣言の終わり (; { }) まで見る
    /// (属性 [ … ] と、修飾子の引数 ( … ) は越える。dyn・export などのほかの修飾子も越える)。
    /// </remarks>
    private static string? VisibilityBefore(ShaderSyntaxToken keyword, ImmutableArray<ShaderSyntaxToken> tokens, Dictionary<ShaderSyntaxToken, int> index)
    {
        if (!index.TryGetValue(keyword, out int at))
        {
            return null;
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
                if (previous.Text is "public" or "internal" or "private")
                {
                    return previous.Text;
                }

                if (previous.Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.CloseBraceToken
                    || !string.Equals(previous.Source.FilePath, keyword.Source.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 型の extension (読み飛ばした宣言) の中の、private のメソッドの名前 (<c>extension S { private int f() { … } }</c> の f)。
    /// </summary>
    private static IEnumerable<string> ExtensionPrivates(CompilationUnitSyntax root, string type)
    {
        foreach (IncompleteDeclarationSyntax extension in root.DescendantNodesAndSelf().OfType<IncompleteDeclarationSyntax>())
        {
            ImmutableArray<ShaderSyntaxToken> tokens = extension.Tokens;

            if (tokens is not [{ Text: "extension" }, { Kind: ShaderSyntaxKind.IdentifierToken } extended, ..] || extended.Text != type)
            {
                continue;
            }

            int depth = 0;

            for (int i = 0; i + 3 < tokens.Length; i++)
            {
                depth += tokens[i].Kind switch { ShaderSyntaxKind.OpenBraceToken => 1, ShaderSyntaxKind.CloseBraceToken => -1, _ => 0 };

                if (depth == 1 && tokens[i].Text == "private" && tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken
                    && tokens[i + 2].Kind == ShaderSyntaxKind.IdentifierToken && tokens[i + 3].Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    yield return tokens[i + 2].Text;
                }
            }
        }
    }

    /// <summary>構造体ごとの private のメンバーの名前。同じ名前の public のメンバー (多重定義) があれば外す。extension の private のメソッドと、private の __subscript も含む。</summary>
    private static Dictionary<string, HashSet<string>> CollectPrivates(CompilationUnitSyntax root, ImmutableArray<ShaderSyntaxToken> preprocessed)
    {
        Dictionary<string, HashSet<string>> privates = new(StringComparer.Ordinal);
        Dictionary<ShaderSyntaxToken, ShaderSyntaxToken> previousTokens = new(ReferenceEqualityComparer.Instance);
        ShaderSyntaxToken? last = null;

        foreach (ShaderSyntaxToken token in preprocessed)
        {
            if (last is not null)
            {
                previousTokens[token] = last;
            }

            last = token;
        }

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

                // 読み飛ばした __subscript (private __subscript(int i) -> int { … })。添字の使用を見る。
                // 修飾子は構文木に残らないので、宣言の前のトークンを見る。
                if (member is IncompleteDeclarationSyntax { Tokens: [{ Text: "__subscript" } subscript, ..] }
                    && previousTokens.TryGetValue(subscript, out ShaderSyntaxToken? before) && before.Text == "private")
                {
                    hidden.Add("__subscript");
                }
            }

            hidden.ExceptWith(visible);
            hidden.UnionWith(ExtensionPrivates(root, name.Text));

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
