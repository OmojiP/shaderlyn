using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Slang.Parsing;

namespace Shaderlyn.Slang;

/// <summary>
/// Slang のコードを解析して、構文木 (<see cref="ShaderSyntaxTree"/>) を作る入口。
/// </summary>
internal static class SlangSyntaxTree
{
    /// <summary>Slang のコードを解析して構文木を作る。</summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="options">プリプロセッサの設定。</param>
    /// <param name="isStandardModule">slangc に入っている標準のモジュールか (名前の区切りは <c>/</c>)。そのモジュールのファイルは探さない。</param>
    /// <returns>解析結果。</returns>
    public static ShaderSyntaxTree Parse(SourceText text, PreprocessorOptions? options = null, Func<string, bool>? isStandardModule = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        PreprocessorOptions effective = options ?? new PreprocessorOptions();

        if (effective.Language is not SlangLanguageOptions)
        {
            effective = effective with { Language = SlangLanguageOptions.Default };
        }

        PreprocessResult preprocessed = new ShaderPreprocessor(effective).Preprocess(text);

        // import で取り込むモジュールの宣言を見えるようにする (SlangModules)。モジュールの関数の本体は、取り込んだ利用者のヘッダとしては読まない
        // (モジュールはそれぞれ単独で解析する)。
        HashSet<string> modules = new(StringComparer.OrdinalIgnoreCase);
        preprocessed = SlangModules.Splice(text, preprocessed, effective, modules, isStandardModule);
        Func<string, bool>? isUserInclude = options?.IsUserInclude is { } user && modules.Count > 0
            ? path => !modules.Contains(SafeFullPath(path)) && user(path)
            : options?.IsUserInclude;

        SlangParser parser = new(preprocessed.Tokens, text, isUserInclude);
        CompilationUnitSyntax root = parser.ParseCompilationUnit(out ImmutableArray<Diagnostic> parseDiagnostics);
        SyntaxNode.WireParents(root);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(preprocessed.Diagnostics);
        diagnostics.AddRange(parseDiagnostics);
        diagnostics.Sort(Diagnostic.DocumentOrderComparer);

        return new ShaderSyntaxTree(text, root, preprocessed, diagnostics.ToImmutable(), effective.Language!, parser.UnanalyzedIdentifiers);
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
