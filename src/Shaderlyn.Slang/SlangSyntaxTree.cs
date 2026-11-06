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
    /// <returns>解析結果。</returns>
    public static ShaderSyntaxTree Parse(SourceText text, PreprocessorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        PreprocessorOptions effective = options ?? new PreprocessorOptions();

        if (effective.Language is not SlangLanguageOptions)
        {
            effective = effective with { Language = SlangLanguageOptions.Default };
        }

        PreprocessResult preprocessed = new ShaderPreprocessor(effective).Preprocess(text);

        SlangParser parser = new(preprocessed.Tokens, text, options?.IsUserInclude);
        CompilationUnitSyntax root = parser.ParseCompilationUnit(out ImmutableArray<Diagnostic> parseDiagnostics);
        SyntaxNode.WireParents(root);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(preprocessed.Diagnostics);
        diagnostics.AddRange(parseDiagnostics);
        diagnostics.Sort(Diagnostic.DocumentOrderComparer);

        return new ShaderSyntaxTree(text, root, preprocessed, diagnostics.ToImmutable(), effective.Language!, parser.UnanalyzedIdentifiers);
    }
}
