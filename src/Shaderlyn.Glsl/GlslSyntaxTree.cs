using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Glsl.Parsing;

namespace Shaderlyn.Glsl;

/// <summary>
/// GLSL のコードを解析して、構文木 (<see cref="ShaderSyntaxTree"/>) を作る入口。
/// </summary>
/// <remarks>
/// 単位 P2 の試作。共通の構文木と共通の構文解析器で GLSL を読めるかを確かめるためにある。
/// </remarks>
internal static class GlslSyntaxTree
{
    /// <summary>GLSL コードを解析して構文木を作る。</summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="options">プリプロセッサの設定。</param>
    /// <returns>解析結果。</returns>
    public static ShaderSyntaxTree Parse(SourceText text, PreprocessorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        // 環境が言語の設定 (#version で決まるバージョンなど) を与えていれば、それを木に残す。
        PreprocessorOptions effective = options ?? new PreprocessorOptions();

        if (effective.Language is not GlslLanguageOptions)
        {
            effective = effective with { Language = GlslLanguageOptions.Default };
        }

        PreprocessResult preprocessed = new ShaderPreprocessor(effective).Preprocess(text);

        GlslParser parser = new(preprocessed.Tokens, text, options?.IsUserInclude);
        CompilationUnitSyntax root = parser.ParseCompilationUnit(out ImmutableArray<Diagnostic> parseDiagnostics);
        SyntaxNode.WireParents(root);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(preprocessed.Diagnostics);
        diagnostics.AddRange(parseDiagnostics);
        diagnostics.Sort(Diagnostic.DocumentOrderComparer);

        return new ShaderSyntaxTree(text, root, preprocessed, diagnostics.ToImmutable(), effective.Language!, parser.UnanalyzedIdentifiers);
    }
}

