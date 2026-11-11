using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Slang;

/// <summary>
/// <c>import</c> で取り込むモジュールを探して読み、その宣言をファイルから見えるようにする。
/// </summary>
/// <remarks>
/// <para>
/// Slang のモジュールは、<c>#include</c> と違ってプリプロセッサの状態 (マクロ) を共有しない。
/// そこで、モジュールのファイルをそれぞれ単独でプリプロセスし、そのトークンをファイルのトークンの前に置く。
/// 構文解析は、取り込んだヘッダと同じく、ほかのファイルのトークンとして読む (関数の本体は読まず、宣言だけを使う)。
/// </para>
/// <para>
/// 読むもの: <c>import a.b;</c> と <c>__exported import a.b;</c> (<c>a/b.slang</c>。名前の <c>_</c> を <c>-</c> にしたファイルも探す)、
/// <c>import "path.slang";</c>、<c>__include a;</c> と <c>implementing a;</c> (同じモジュールのほかの部分)。
/// 取り込んだモジュールの取り込みも、たどって読む (Slang はそれを公開しないこともあるが、名前が見えないことを根拠にする検査を誤らせないため、多めに見せる。見えない名前の使用は、SlangImportScopeAnalyzer が報告する)。
/// </para>
/// <para>
/// 見つからないモジュールは、解決できない取り込みとして伝える (<see cref="PreprocessResult.UnresolvedIncludes"/>)。
/// そのファイルでは、宣言が無いことを根拠にする検査が働かない。
/// </para>
/// </remarks>
internal static class SlangModules
{
    /// <summary>取り込みの文を始める語。</summary>
    private static readonly ImmutableHashSet<string> ImportKeywords = ["import", "__import", "__include", "implementing"];

    /// <summary>ファイルの取り込みを読み、モジュールのトークンを前に置いた結果を返す。</summary>
    /// <param name="text">ファイル。</param>
    /// <param name="preprocessed">ファイルのプリプロセスの結果。</param>
    /// <param name="options">プリプロセッサの設定。取り込みの解決器を使う。</param>
    /// <param name="modulePaths">読んだモジュールのファイルのパス (足していく)。</param>
    /// <param name="isStandardModule">slangc に入っている標準のモジュールか。そのモジュールは探さず、見つかったとする (名前は標準ライブラリの表から引く)。</param>
    /// <returns>モジュールのトークンを前に置いた結果。取り込みが無ければ元のまま。</returns>
    public static PreprocessResult Splice(SourceText text, PreprocessResult preprocessed, PreprocessorOptions options, HashSet<string> modulePaths, Func<string, bool>? isStandardModule = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(modulePaths);

        if (options.IncludeResolver is null || !HasImports(preprocessed.Tokens))
        {
            return preprocessed;
        }

        List<ShaderSyntaxToken> moduleTokens = [];
        List<string> resolved = [];
        List<string> unresolved = [];
        List<IncludeReference> references = [];
        List<Diagnostic> diagnostics = [];
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(text.FilePath ?? "<無名>") };

        Collect(text.FilePath ?? string.Empty, preprocessed.Tokens, options, visited, moduleTokens, resolved, unresolved, references, rootImport: null, isStandardModule);

        // 見つからないモジュールは、取り込みと同じく、その行で伝える (CF0021)。
        if (options.ReportUnresolvedIncludes)
        {
            diagnostics.AddRange(references
                .Where(r => !r.IsResolved)
                .Select(r => Diagnostic.Create(CFamily.ShaderDescriptors.UnresolvedInclude, r.Location, r.Path,
                    $"import のモジュールのファイル ({r.Path}.slang) が、このファイルのフォルダーと探索パスに見つかりません。")));
        }

        if (moduleTokens.Count == 0 && unresolved.Count == 0)
        {
            return preprocessed;
        }

        modulePaths.UnionWith(resolved);
        int shift = moduleTokens.Count;

        return preprocessed with
        {
            Tokens = [.. moduleTokens, .. preprocessed.Tokens],
            ResolvedIncludes = [.. preprocessed.ResolvedIncludes, .. resolved.Except(preprocessed.ResolvedIncludes, StringComparer.OrdinalIgnoreCase)],
            UnresolvedIncludes = [.. preprocessed.UnresolvedIncludes, .. unresolved.Distinct(StringComparer.Ordinal)],
            Diagnostics = [.. preprocessed.Diagnostics, .. diagnostics],
            Includes = [.. preprocessed.Includes, .. references],
            ConditionalRegions = [.. preprocessed.ConditionalRegions.Select(r => r with { Start = r.Start + shift })],
        };
    }

    /// <summary>取り込みの文があるか。</summary>
    private static bool HasImports(ImmutableArray<ShaderSyntaxToken> tokens)
        => tokens.Any(t => t.Kind == ShaderSyntaxKind.IdentifierToken && ImportKeywords.Contains(t.Text));

    /// <summary>トークンの中の取り込みをたどり、モジュールのトークンを集める。</summary>
    private static void Collect(
        string filePath,
        ImmutableArray<ShaderSyntaxToken> tokens,
        PreprocessorOptions options,
        HashSet<string> visited,
        List<ShaderSyntaxToken> moduleTokens,
        List<string> resolved,
        List<string> unresolved,
        List<IncludeReference> references,
        ShaderSyntaxToken? rootImport,
        Func<string, bool>? isStandardModule)
    {
        bool isRoot = rootImport is null;

        foreach ((string name, ShaderSyntaxToken at, bool isString) in FindImports(tokens))
        {
            // 標準のモジュールは、名前で書いても文字列で書いても (import "glsl";) 探さない。
            if (isStandardModule?.Invoke(isString ? Path.ChangeExtension(name, null).Replace('\\', '/') : name) == true)
            {
                continue;
            }

            if (TryResolve(name, isString, filePath, options.IncludeResolver!, out SourceText? module) is not { } modulePath)
            {
                // Markdown の中にコードを書いたモジュール (a.slang.md) は、見つかるが宣言を読まない。
                // 見つからないとは伝えず、宣言が無いことを根拠にする検査だけを止める (解決できない取り込みとして数える)。
                if (IsLiterateModule(name, isString, filePath, options.IncludeResolver!))
                {
                    unresolved.Add(name);
                    continue;
                }

                // 根のファイルの取り込みだけを伝える。モジュールの中の見つからない取り込みは、そのモジュールを解析するときに伝える。
                if (isRoot)
                {
                    unresolved.Add(name);

                    if (at.GetLocation() is { } location)
                    {
                        references.Add(new IncludeReference(name, location, null) { Directive = "import" });
                    }
                }

                continue;
            }

            if (!visited.Add(modulePath))
            {
                continue;
            }

            // 読んだモジュールは、たどって読んだものも、根のファイルの取り込みの位置で覚える (モジュールのファイルには指摘を出さないため)。
            if ((rootImport ?? at).GetLocation() is { } found)
            {
                references.Add(new IncludeReference(name, found, modulePath) { Directive = "import" });
            }

            resolved.Add(modulePath);
            PreprocessResult result = new ShaderPreprocessor(options with { ReportUnresolvedIncludes = false }).Preprocess(module!);
            ImmutableArray<ShaderSyntaxToken> body = [.. result.Tokens.Where(t => t.Kind != ShaderSyntaxKind.EndOfFileToken)];

            // 先にモジュールの取り込みを置く (宣言の順を保つ)。
            Collect(modulePath, body, options, visited, moduleTokens, resolved, unresolved, references, rootImport ?? at, isStandardModule);
            moduleTokens.AddRange(body);
        }
    }

    /// <summary>取り込みの文 (<c>import a.b;</c> など) を探す。</summary>
    private static IEnumerable<(string Name, ShaderSyntaxToken At, bool IsString)> FindImports(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Kind != ShaderSyntaxKind.IdentifierToken || !ImportKeywords.Contains(tokens[i].Text))
            {
                continue;
            }

            // 文の先頭 (前が ; ・ } ・ __exported か、ファイルの先頭) でだけ読む。
            if (i > 0 && tokens[i - 1].Kind is not (ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken)
                && tokens[i - 1].Text is not ("__exported" or "export"))
            {
                continue;
            }

            int j = i + 1;

            if (j < tokens.Length && tokens[j].Kind == ShaderSyntaxKind.StringLiteralToken)
            {
                yield return (tokens[j].Text.Trim('"'), tokens[i], true);
                continue;
            }

            List<string> parts = [];

            while (j < tokens.Length && tokens[j].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                parts.Add(tokens[j].Text);

                if (j + 1 < tokens.Length && tokens[j + 1].Kind == ShaderSyntaxKind.DotToken)
                {
                    j += 2;
                    continue;
                }

                j++;
                break;
            }

            if (parts.Count > 0 && j < tokens.Length && tokens[j].Kind == ShaderSyntaxKind.SemicolonToken)
            {
                yield return (string.Join("/", parts), tokens[i], false);
            }
        }
    }

    /// <summary>Markdown の中にコードを書いたモジュール (<c>a.slang.md</c>、名前の _ を - にしたものも) があるか。</summary>
    private static bool IsLiterateModule(string name, bool isString, string includingFilePath, IIncludeResolver resolver)
    {
        IEnumerable<string> candidates = isString
            ? [name + ".md", name + ".slang.md"]
            : [name + ".slang.md", name.Replace('_', '-') + ".slang.md"];

        return candidates.Distinct(StringComparer.Ordinal).Any(candidate => resolver.TryResolve(candidate, includingFilePath, out _));
    }

    /// <summary>モジュールの名前からファイルを探す。</summary>
    /// <returns>見つけたファイルの絶対パス。見つからなければ <see langword="null"/>。</returns>
    private static string? TryResolve(string name, bool isString, string includingFilePath, IIncludeResolver resolver, out SourceText? module)
    {
        IEnumerable<string> candidates = isString
            ? [name, name + ".slang"]
            : [name + ".slang", name.Replace('_', '-') + ".slang"];

        foreach (string candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (resolver.TryResolve(candidate, includingFilePath, out SourceText? found))
            {
                module = found;
                return Path.GetFullPath(found.FilePath ?? candidate);
            }
        }

        module = null;
        return null;
    }
}
