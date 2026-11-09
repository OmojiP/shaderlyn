using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Wgsl.Parsing;

namespace Shaderlyn.Wgsl;

/// <summary>
/// WGSL のコードを解析して、構文木 (<see cref="ShaderSyntaxTree"/>) を作る入口。
/// </summary>
/// <remarks>
/// WGSL にプリプロセッサは無いが、字句はプリプロセッサを通して読む (取り込みもマクロも無いので、トークンはそのまま出る)。
/// 条件の範囲などの、木の外の情報をほかの言語と同じ形で持つためである。
/// </remarks>
internal static class WgslSyntaxTree
{
    /// <summary>WGSL のコードを解析して構文木を作る。</summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="options">プリプロセッサの設定。</param>
    /// <returns>解析結果。</returns>
    public static ShaderSyntaxTree Parse(SourceText text, PreprocessorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        PreprocessorOptions effective = (options ?? new PreprocessorOptions()) with { Language = WgslLanguageOptions.Default, NestedBlockComments = true };
        PreprocessResult preprocessed = new ShaderPreprocessor(effective).Preprocess(text);

        WgslParser parser = new(preprocessed.Tokens);
        CompilationUnitSyntax root = parser.ParseCompilationUnit(out ImmutableArray<Diagnostic> parseDiagnostics);
        SyntaxNode.WireParents(root);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(preprocessed.Diagnostics);
        diagnostics.AddRange(parseDiagnostics);
        diagnostics.Sort(Diagnostic.DocumentOrderComparer);

        return new ShaderSyntaxTree(text, root, preprocessed, diagnostics.ToImmutable(), effective.Language!, parser.UnanalyzedIdentifiers);
    }
}
