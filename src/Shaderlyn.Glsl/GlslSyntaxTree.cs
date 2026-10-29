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

        GlslLanguageOptions language = (GlslLanguageOptions)effective.Language!;
        GlslParser parser = new(preprocessed.Tokens, text, options?.IsUserInclude, language);
        CompilationUnitSyntax root = parser.ParseCompilationUnit(out ImmutableArray<Diagnostic> parseDiagnostics);
        SyntaxNode.WireParents(root);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(preprocessed.Diagnostics);
        diagnostics.AddRange(parseDiagnostics);
        AddReservedWords(diagnostics, preprocessed.Tokens, text, language);
        diagnostics.Sort(Diagnostic.DocumentOrderComparer);

        return new ShaderSyntaxTree(text, root, preprocessed, diagnostics.ToImmutable(), effective.Language!, parser.UnanalyzedIdentifiers);
    }

    /// <summary>
    /// そのバージョンと拡張で使えない予約語を、構文の誤り (CF0001) として報告する (glslang の「Reserved word.」)。
    /// </summary>
    /// <param name="diagnostics">報告を足す先。</param>
    /// <param name="tokens">展開したトークン。</param>
    /// <param name="text">解析しているファイル。このファイルに書いた語だけを報告する。</param>
    /// <param name="language">言語の設定。バージョンが分からなければ報告しない。</param>
    /// <remarks>
    /// <c>class</c> や <c>union</c> のようにどのバージョンでも予約語のものと、<c>double</c> を 1.40 で書くように、そのバージョンでは予約語のものがある。
    /// </remarks>
    private static void AddReservedWords(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<ShaderSyntaxToken> tokens,
        SourceText text,
        GlslLanguageOptions language)
    {
        if (language.Version is not { } version)
        {
            return;
        }

        GlslWordRules rules = new(version, language.Profile == GlslProfile.Es, language.Stage, language.Vulkan, language.Extensions.Contains);

        foreach (ShaderSyntaxToken token in tokens)
        {
            if (token.Kind == ShaderSyntaxKind.IdentifierToken
                && ReferenceEquals(token.Source, text)
                && rules.Classify(token.Text).IsReservedError
                && token.GetLocation() is { } location)
            {
                diagnostics.Add(Diagnostic.Create(
                    CFamily.ShaderDescriptors.SyntaxError, location, $"'{token.Text}' はこのバージョンの GLSL の予約語なので使えません。"));
            }
        }
    }
}

