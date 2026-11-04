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

        // glslang の Pp.cpp の規則で前処理する。#if の式は int (32 ビット) で計算し、評価できない式は通る分岐とする。
        effective = effective with { GlslRules = true };

        // 行の継続の無いバージョン (ES の 3.00 より前と、デスクトップの 4.20 より前) では、行末に \ を書いた // のコメントも行の終わりで終わる
        // (glslang の lineContinuationCheck。GL_ARB_shading_language_420pack を有効にすれば続く)。
        if (effective.Language is GlslLanguageOptions { Version: { } version } glsl
            && (glsl.Profile == GlslProfile.Es ? version < 300 : version < 420)
            && !text.ToString().Contains("GL_ARB_shading_language_420pack", StringComparison.Ordinal))
        {
            effective = effective with { CommentLineContinuation = false };
        }

        // ES では、#if の式に定義されていないマクロを書くと誤りである (GL0003)。その名前を記録させる。
        if (effective.Language is GlslLanguageOptions { Profile: GlslProfile.Es })
        {
            effective = effective with { RecordUndefinedConditionIdentifiers = true };
        }

        PreprocessResult preprocessed = new ShaderPreprocessor(effective).Preprocess(text);

        GlslLanguageOptions language = (GlslLanguageOptions)effective.Language!;
        GlslParser parser = new(preprocessed.Tokens, text, options?.IsUserInclude, language, preprocessed.RecordedDirectives);
        CompilationUnitSyntax root = parser.ParseCompilationUnit(out ImmutableArray<Diagnostic> parseDiagnostics);
        SyntaxNode.WireParents(root);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        // マクロを中身を変えて定義し直すこと (CF0004) は、glslang では誤り (「Macro redefined」) である。HLSL のコンパイラは警告に留める。
        // 重要度だけを差し替える。既定の重要度は警告のままなので、解析は止めない (ShaderCompilation.BreaksAnalysis)。設定で重要度を書けば、そちらが勝つ。
        diagnostics.AddRange(preprocessed.Diagnostics.Select(d => d.Descriptor == CFamily.ShaderDescriptors.MacroRedefinition ? d.WithSeverity(DiagnosticSeverity.Error) : d));
        diagnostics.AddRange(parseDiagnostics);
        AddReservedWords(diagnostics, preprocessed.Tokens, text, language, preprocessed.RecordedDirectives);
        AddParameterDefaults(diagnostics, root, text);
        diagnostics.Sort(Diagnostic.DocumentOrderComparer);

        return new ShaderSyntaxTree(text, root, preprocessed, diagnostics.ToImmutable(), effective.Language!, parser.UnanalyzedIdentifiers);
    }

    /// <summary>
    /// 仮引数の既定値 (<c>void f(int x = 2)</c>) を、構文の誤り (CF0001) として報告する。
    /// glslang の「default argument values are only available for builtins」。構文木は壊れていないので、解析は止めない。
    /// </summary>
    /// <param name="diagnostics">報告を足す先。</param>
    /// <param name="root">構文木の根。</param>
    /// <param name="text">解析しているファイル。このファイルに書いたものだけを報告する。</param>
    private static void AddParameterDefaults(ImmutableArray<Diagnostic>.Builder diagnostics, CompilationUnitSyntax root, SourceText text)
    {
        foreach (ParameterSyntax parameter in root.DescendantNodesAndSelf().OfType<ParameterSyntax>())
        {
            if (parameter.EqualsToken is { } equals
                && ReferenceEquals(equals.Source, text)
                && equals.GetLocation() is { } location)
            {
                diagnostics.Add(Diagnostic.Create(
                    CFamily.ShaderDescriptors.RecoverableSyntaxError, location, "GLSL では、仮引数に既定値を書けません。"));
            }
        }
    }

    /// <summary>
    /// そのバージョンと拡張で使えない予約語を、構文の誤り (CF0001) として報告する (glslang の「Reserved word.」)。
    /// </summary>
    /// <param name="diagnostics">報告を足す先。</param>
    /// <param name="tokens">展開したトークン。</param>
    /// <param name="text">解析しているファイル。このファイルに書いた語だけを報告する。</param>
    /// <param name="language">言語の設定。バージョンが分からなければ報告しない。</param>
    /// <param name="directives">展開の順番を覚えた指令。拡張は、書いた位置より後ろでだけ有効とする (構文解析と同じ)。</param>
    /// <remarks>
    /// <c>class</c> や <c>union</c> のようにどのバージョンでも予約語のものと、<c>double</c> を 1.40 で書くように、そのバージョンでは予約語のものがある。
    /// </remarks>
    private static void AddReservedWords(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<ShaderSyntaxToken> tokens,
        SourceText text,
        GlslLanguageOptions language,
        ImmutableArray<RecordedDirective> directives)
    {
        if (language.Version is not { } version)
        {
            return;
        }

        (int OutputIndex, string Name, bool On)[] extensions =
        [
            .. (directives.IsDefault ? [] : directives)
                .Where(d => d.Directive.Text == "extension" && d.Tokens is [_, { Kind: ShaderSyntaxKind.ColonToken }, _, ..])
                .Select(d => (d.OutputIndex, d.Tokens[0].Text, d.Tokens[2].Text != "disable")),
        ];
        int position = 0;

        bool IsOn(string name)
        {
            if (extensions.Length == 0)
            {
                return language.Extensions.Contains(name);
            }

            bool on = false;

            foreach ((int outputIndex, string extension, bool enabled) in extensions)
            {
                if (outputIndex > position)
                {
                    break;
                }

                if (extension == name || extension == "all")
                {
                    on = enabled;
                }
            }

            return on;
        }

        GlslWordRules rules = new(version, language.Profile == GlslProfile.Es, language.Stage, language.Vulkan, IsOn);

        for (position = 0; position < tokens.Length; position++)
        {
            ShaderSyntaxToken token = tokens[position];

            if (token.Kind == ShaderSyntaxKind.IdentifierToken
                && ReferenceEquals(token.Source, text)
                && rules.Classify(token.Text).IsReservedError
                && token.GetLocation() is { } location)
            {
                diagnostics.Add(Diagnostic.Create(
                    CFamily.ShaderDescriptors.ReservedWord, location, $"'{token.Text}' はこのバージョンの GLSL の予約語なので使えません。"));
            }
        }
    }
}

