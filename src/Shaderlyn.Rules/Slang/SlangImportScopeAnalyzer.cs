using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、取り込んだモジュールから見えない名前の使用を検査する (CF0310)。
/// </summary>
/// <remarks>
/// <para>
/// 解析では、取り込んだモジュールの取り込みもたどって読み、名前空間の中の宣言も名前空間の外から引けるものとする (名前が見えないことを根拠にする検査を誤らせないため)。
/// slangc はそうしない (2026.19 で確かめた)。ここで、その差を報告する (E30015「undefined identifier」)。
/// </para>
/// <list type="bullet">
/// <item><c>import</c> はたどらない。取り込んだモジュール (<c>tmid</c>) が取り込んだモジュール (<c>tleaf</c>) の宣言は、<c>__exported import</c> で取り込んだものでなければ見えない</item>
/// <item>名前空間の中の宣言は、名前空間で修飾するか、このモジュールで <c>using namespace</c> を書かなければ見えない。取り込んだモジュールの <c>using</c> は効かない</item>
/// </list>
/// 見るのは、関数の外の関数と変数を修飾せずに指す名前だけである。モジュールの取り込みの関係を読めなければ (見つからない取り込みがあるなど)、見ない。
/// </remarks>
internal sealed class SlangImportScopeAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>取り込みの文 1 つ。</summary>
    private sealed record Import(string Name, bool IsString, string Kind, bool Exported);

    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.UndeclaredIdentifier];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang || compilation.Text.FilePath is not { } rootPath)
        {
            return;
        }

        string root = Path.GetFullPath(rootPath);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (!program.Tree.PreprocessResult.UnresolvedIncludes.IsEmpty)
            {
                continue;
            }

            ImmutableArray<ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;

            // ファイルごとのトークン。
            Dictionary<string, List<ShaderSyntaxToken>> files = new(StringComparer.OrdinalIgnoreCase);

            foreach (ShaderSyntaxToken token in tokens)
            {
                if (token.Source.FilePath is { } path)
                {
                    string full = Path.GetFullPath(path);

                    if (!files.TryGetValue(full, out List<ShaderSyntaxToken>? list))
                    {
                        files[full] = list = [];
                    }

                    list.Add(token);
                }
            }

            if (!files.ContainsKey(root))
            {
                continue;
            }

            if (Visible(root, files) is not { } visible)
            {
                continue;
            }

            HashSet<string> rootModule = visible.RootModule;
            Report(context, compilation, program, files, visible.Files, rootModule);
        }
    }

    /// <summary>根のファイルのモジュールから見えるファイルと、根のファイルのモジュールのファイル。</summary>
    private static (HashSet<string> Files, HashSet<string> RootModule)? Visible(string root, Dictionary<string, List<ShaderSyntaxToken>> files)
    {
        Dictionary<string, List<(string Target, Import Import)>> edges = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string file, List<ShaderSyntaxToken> fileTokens) in files)
        {
            List<(string, Import)> list = [];

            foreach (Import import in FindImports(fileTokens))
            {
                if (Resolve(import, file, files.Keys) is { } target)
                {
                    list.Add((target, import));
                }
            }

            edges[file] = list;
        }

        // __include と implementing でつながったファイルは、同じモジュールである (向きによらない)。
        Dictionary<string, HashSet<string>> parts = new(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files.Keys)
        {
            parts[file] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file };
        }

        foreach ((string file, List<(string Target, Import Import)> list) in edges)
        {
            foreach ((string target, Import import) in list.Where(e => e.Import.Kind is "__include" or "implementing"))
            {
                parts[file].Add(target);
                parts[target].Add(file);
            }
        }

        HashSet<string> ModuleOf(string file)
        {
            HashSet<string> module = new(StringComparer.OrdinalIgnoreCase);
            Stack<string> pending = new([file]);

            while (pending.TryPop(out string? next))
            {
                if (module.Add(next))
                {
                    foreach (string part in parts[next])
                    {
                        pending.Push(part);
                    }
                }
            }

            return module;
        }

        // 取り込みの関係でたどりつけるファイル。
        HashSet<string> reachable = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> walk = new([root]);

        while (walk.TryPop(out string? next))
        {
            if (reachable.Add(next))
            {
                foreach ((string target, Import _) in edges[next])
                {
                    walk.Push(target);
                }
            }
        }

        HashSet<string> rootModule = ModuleOf(root);

        // 取り込みでたどりつけないファイル (#include で取り込んだファイルなど) は、どこから見えるか決めないので、見えるものとする。
        HashSet<string> visible = new(rootModule, StringComparer.OrdinalIgnoreCase);
        visible.UnionWith(files.Keys.Where(f => !reachable.Contains(f)));
        Stack<string> imported = new();

        foreach (string file in rootModule)
        {
            foreach ((string target, Import import) in edges[file])
            {
                if (import.Kind is "import" or "__import")
                {
                    imported.Push(target);
                }
            }
        }

        // 取り込んだモジュールと、そのモジュールが __exported import で取り込んだモジュール。
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        while (imported.TryPop(out string? next))
        {
            if (!seen.Add(next))
            {
                continue;
            }

            foreach (string file in ModuleOf(next))
            {
                visible.Add(file);

                foreach ((string target, Import import) in edges[file])
                {
                    if (import.Kind is "import" or "__import" && import.Exported)
                    {
                        imported.Push(target);
                    }
                }
            }
        }

        return (visible, rootModule);
    }

    private static void Report(SyntaxTreeAnalysisContext context, ShaderCompilation compilation, AnalyzedProgram program,
        Dictionary<string, List<ShaderSyntaxToken>> files, HashSet<string> visible, HashSet<string> rootModule)
    {
        CompilationUnitSyntax unit = program.Tree.Root;

        // 関数の外の関数の宣言 (名前ごと)。
        Dictionary<string, List<ShaderSyntaxNode>> functions = new(StringComparer.Ordinal);

        foreach (FunctionDeclarationSyntax function in unit.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>())
        {
            if (function.Parent is CompilationUnitSyntax or NamespaceDeclarationSyntax && function.QualifierTokens.IsEmpty)
            {
                if (!functions.TryGetValue(function.Name, out List<ShaderSyntaxNode>? list))
                {
                    functions[function.Name] = list = [];
                }

                list.Add(function);
            }
        }

        // このモジュールで using した名前空間の名前 (using namespace Foo;・using Foo;)。
        HashSet<string> used = new(StringComparer.Ordinal);

        foreach (string file in rootModule)
        {
            List<ShaderSyntaxToken> fileTokens = files[file];

            for (int i = 0; i + 1 < fileTokens.Count; i++)
            {
                if (fileTokens[i].Text == "using")
                {
                    int j = fileTokens[i + 1].Text == "namespace" ? i + 2 : i + 1;

                    for (; j < fileTokens.Count && fileTokens[j].Kind is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken; j++)
                    {
                        if (fileTokens[j].Kind == ShaderSyntaxKind.IdentifierToken)
                        {
                            used.Add(fileTokens[j].Text);
                        }
                    }
                }
            }
        }

        // 名前空間の中の using (namespace ns { using ns2; })。ns を using すれば、ns2 の宣言も見える。
        Dictionary<string, HashSet<string>> forwarded = new(StringComparer.Ordinal);

        foreach (NamespaceDeclarationSyntax space in unit.DescendantNodesAndSelf().OfType<NamespaceDeclarationSyntax>())
        {
            if (space.NameToken is null || space.OpenBrace.Source.FilePath is not { } spacePath || !files.TryGetValue(Path.GetFullPath(spacePath), out List<ShaderSyntaxToken>? spaceTokens))
            {
                continue;
            }

            List<ShaderSyntaxToken> body = [.. spaceTokens.Where(t => ReferenceEquals(t.Source, space.OpenBrace.Source)
                                                                     && t.Span.Start > space.OpenBrace.Span.Start && t.Span.Start < space.CloseBrace.Span.Start)];

            for (int i = 0; i + 1 < body.Count; i++)
            {
                if (body[i].Text == "using")
                {
                    int j = body[i + 1].Text == "namespace" ? i + 2 : i + 1;

                    if (j < body.Count && body[j].Kind == ShaderSyntaxKind.IdentifierToken)
                    {
                        if (!forwarded.TryGetValue(space.Name, out HashSet<string>? set))
                        {
                            forwarded[space.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                        }

                        set.Add(body[j].Text);
                    }
                }
            }
        }

        Stack<string> expand = new(used);

        while (expand.TryPop(out string? next))
        {
            foreach (string inner in forwarded.GetValueOrDefault(next) ?? [])
            {
                if (used.Add(inner))
                {
                    expand.Push(inner);
                }
            }
        }

        ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

        string? FileOf(ShaderSyntaxNode node) => node.FirstToken?.Source.FilePath is { } p ? Path.GetFullPath(p) : null;

        // 名前空間ごとの、中の宣言の名前 (同じ名前の名前空間をまとめる)。名前空間の中の読み飛ばした宣言があれば、その名前空間は決めない。
        Dictionary<string, HashSet<string>> namespaceMembers = new(StringComparer.Ordinal);
        HashSet<string> undecided = new(StringComparer.Ordinal);

        foreach (NamespaceDeclarationSyntax space in unit.DescendantNodesAndSelf().OfType<NamespaceDeclarationSyntax>())
        {
            if (!namespaceMembers.TryGetValue(space.Name, out HashSet<string>? names))
            {
                namespaceMembers[space.Name] = names = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (DeclarationSyntax member in space.Members)
            {
                switch (member)
                {
                    case FunctionDeclarationSyntax function: names.Add(function.Name); break;
                    case VariableDeclarationSyntax variable: names.UnionWith(variable.Variables.Select(v => v.Name)); break;
                    case StructDeclarationSyntax { NameToken: { } n }: names.Add(n.Text); break;
                    case EnumDeclarationSyntax { NameToken: { } n }: names.Add(n.Text); break;
                    case NamespaceDeclarationSyntax inner: names.Add(inner.Name); break;
                    default: undecided.Add(space.Name); break;
                }
            }
        }

        foreach (string name in undecided)
        {
            namespaceMembers.Remove(name);
        }

        HashSet<string> typeNames = [.. unit.DescendantNodesAndSelf().Select(n => n switch
        {
            StructDeclarationSyntax { NameToken: { } s } => s.Text,
            EnumDeclarationSyntax { NameToken: { } e } => e.Text,
            _ => null,
        }).OfType<string>(), .. unit.TypeAliases.Keys];

        // ns.f_a (名前空間で修飾した、見えないモジュールの宣言)。
        foreach (MemberAccessExpressionSyntax access in unit.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Target is not IdentifierExpressionSyntax space || access.NameToken.Source.FilePath is not { } accessPath || !rootModule.Contains(Path.GetFullPath(accessPath))
                || binder.ResolveName(space).Kind != DeclaredNameKind.NotFound)
            {
                continue;
            }

            List<ShaderSyntaxNode> members = [.. (functions.GetValueOrDefault(access.Name) ?? [])
                .Where(f => f.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault()?.Name == space.Name)];

            // ns.f_a の f_a が、どの namespace ns にも無い (E30027)。ns が型の名前でもあれば見ない。
            if (members.Count == 0 && namespaceMembers.TryGetValue(space.Name, out HashSet<string>? declared) && !typeNames.Contains(space.Name)
                && !declared.Contains(access.Name))
            {
                Diagnose(context, compilation, access.NameToken, access.Name, $"名前空間 {space.Name} の中に");
                continue;
            }

            if (members.Count > 0 && members.All(m => FileOf(m) is { } file && files.ContainsKey(file) && !visible.Contains(file)))
            {
                Diagnose(context, compilation, access.NameToken, $"{space.Name}.{access.Name}",
                    $"、このモジュールが直接 import していないモジュール ({Path.GetFileName(FileOf(members[0]))}) にしか");
            }
        }

        foreach (IdentifierExpressionSyntax identifier in unit.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>())
        {
            if (identifier.FirstToken is not { } at || at.Source.FilePath is not { } usePath || !rootModule.Contains(Path.GetFullPath(usePath))
                || identifier.Parent is MemberAccessExpressionSyntax { NameToken: var nameToken } && ReferenceEquals(nameToken, at)
                || identifier.Ancestors().Any(a => a is NamespaceDeclarationSyntax))
            {
                continue;
            }

            NameResolution resolution = binder.ResolveName(identifier);
            List<ShaderSyntaxNode> candidates = resolution.Kind switch
            {
                DeclaredNameKind.Global => [.. resolution.Candidates.Select(c => c.Declarator)],
                DeclaredNameKind.NotFound when identifier.Parent is InvocationExpressionSyntax { Target: var target } && ReferenceEquals(target, identifier)
                    => functions.TryGetValue(identifier.Name, out List<ShaderSyntaxNode>? list) ? list : [],
                _ => [],
            };

            if (candidates.Count == 0)
            {
                continue;
            }


            // 取り込んだモジュールの取り込みの宣言 (どの宣言も、見えないファイルにある)。
            if (candidates.All(c => FileOf(c) is { } file && files.ContainsKey(file) && !visible.Contains(file)))
            {
                Diagnose(context, compilation, at, identifier.Name,
                    $"、このモジュールが直接 import していないモジュール ({Path.GetFileName(FileOf(candidates[0]))}) にしか");
                continue;
            }

            // 名前空間の中の宣言 (どの宣言も、using していない名前空間の中にある)。
            if (candidates.All(c => c.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault() is { NameToken: { } space } && !used.Contains(space.Text)))
            {
                string space = candidates[0].Ancestors().OfType<NamespaceDeclarationSyntax>().First().Name;
                Diagnose(context, compilation, at, identifier.Name, $"名前空間 {space} の中にしか");
            }
        }
    }

    private static void Diagnose(SyntaxTreeAnalysisContext context, ShaderCompilation compilation, ShaderSyntaxToken at, string name, string where)
    {
        if (compilation.IsReportable(at) && at.GetLocation() is { } location)
        {
            context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.UndeclaredIdentifier, location, name, where));
        }
    }

    /// <summary>ファイルの取り込みの文 (<c>import a.b;</c>・<c>__exported import a;</c>・<c>import "x.slang";</c>・<c>__include a;</c>・<c>implementing a;</c>)。</summary>
    private static IEnumerable<Import> FindImports(List<ShaderSyntaxToken> tokens)
    {
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != ShaderSyntaxKind.IdentifierToken || tokens[i].Text is not ("import" or "__import" or "__include" or "implementing"))
            {
                continue;
            }

            bool exported = i > 0 && tokens[i - 1].Text is "__exported" or "export";

            if (i > 0 && !exported && tokens[i - 1].Kind is not (ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken))
            {
                continue;
            }

            if (tokens[i + 1].Kind == ShaderSyntaxKind.StringLiteralToken)
            {
                yield return new Import(tokens[i + 1].Text.Trim('"'), true, tokens[i].Text, exported);
                continue;
            }

            List<string> parts = [];

            for (int j = i + 1; j < tokens.Count && tokens[j].Kind == ShaderSyntaxKind.IdentifierToken; j += 2)
            {
                parts.Add(tokens[j].Text);

                if (j + 1 >= tokens.Count || tokens[j + 1].Kind != ShaderSyntaxKind.DotToken)
                {
                    break;
                }
            }

            if (parts.Count > 0)
            {
                yield return new Import(string.Join('/', parts), false, tokens[i].Text, exported);
            }
        }
    }

    /// <summary>取り込みの名前に当たる、読んだファイル。無いか決められなければ <see langword="null"/>。</summary>
    private static string? Resolve(Import import, string from, IEnumerable<string> loaded)
    {
        string relative = import.IsString ? import.Name : import.Name + ".slang";
        string[] names = import.IsString ? [relative] : [relative, relative.Replace('_', '-')];
        string directory = Path.GetDirectoryName(from) ?? string.Empty;

        foreach (string name in names)
        {
            string near = Path.GetFullPath(Path.Combine(directory, name));

            if (loaded.Contains(near, StringComparer.OrdinalIgnoreCase))
            {
                return near;
            }
        }

        // 探索パスで見つけたもの。パスの終わりが合うファイルが 1 つだけなら、それとする。
        string[] matches = [.. loaded.Where(f => names.Any(n => f.Replace('\\', '/').EndsWith("/" + n.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))];
        return matches.Length == 1 ? matches[0] : null;
    }
}
