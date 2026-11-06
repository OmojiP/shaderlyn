using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Godot.Parsing;

namespace Shaderlyn.Godot;

/// <summary>
/// Godot シェーダー言語のコードを解析して、構文木 (<see cref="ShaderSyntaxTree"/>) を作る入口。
/// </summary>
internal static class GodotSyntaxTree
{
    /// <summary>Godot シェーダー言語のコードを解析して構文木を作る。</summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="options">プリプロセッサの設定。</param>
    /// <returns>解析結果。</returns>
    public static ShaderSyntaxTree Parse(SourceText text, PreprocessorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        PreprocessorOptions effective = options ?? new PreprocessorOptions();

        if (effective.Language is not GodotLanguageOptions)
        {
            effective = effective with { Language = GodotLanguageOptions.Default };
        }

        // Godot の字句解析は、閉じていない /* を誤りにせず、ファイルの終わりまでをコメントとして読む。
        // Godot のプリプロセッサが定義するマクロ (CURRENT_RENDERER など) は、設定で与えたマクロに負ける。
        effective = effective with
        {
            ReportsUnterminatedComment = false,
            PredefinedMacros = ((GodotLanguageOptions)effective.Language!).Renderer.CreateBuiltinMacros().SetItems(effective.PredefinedMacros),
        };

        PreprocessResult preprocessed = new ShaderPreprocessor(effective).Preprocess(text);

        GodotParser parser = new(preprocessed.Tokens, text, options?.IsUserInclude);
        CompilationUnitSyntax root = parser.ParseCompilationUnit(out ImmutableArray<Diagnostic> parseDiagnostics);
        SyntaxNode.WireParents(root);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        GodotDirectives.Check(text, preprocessed, diagnostics);
        // ファイルの終わりで閉じていないもの (} 抜けなど) は、Godot と同じくファイルの終わりの位置で報告する (最後のトークンの行ではない)。
        foreach (Diagnostic diagnostic in parseDiagnostics)
        {
            diagnostics.Add(
                diagnostic.Location.FilePath == text.FilePath && diagnostic.GetMessage().StartsWith("ファイルの末尾には", StringComparison.Ordinal)
                    ? diagnostic.WithLocation(Location.Create(text, new TextSpan(text.Length, 0)))
                    : diagnostic);
        }
        GodotGrammar.Check(root, preprocessed.Tokens, text, diagnostics);
        diagnostics.Sort(Diagnostic.DocumentOrderComparer);

        return new ShaderSyntaxTree(text, root, preprocessed, diagnostics.ToImmutable(), effective.Language!, parser.UnanalyzedIdentifiers);
    }
}
