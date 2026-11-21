using System.Collections.Immutable;
using Shaderlyn.CFamily;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Godot;

/// <summary>
/// Godot のプリプロセッサ (<c>servers/rendering/shader_preprocessor.cpp</c>) の規則で、C の規則と違うものを当てる。
/// </summary>
/// <remarks>
/// <para>
/// Godot は、C のプリプロセッサが警告に留めるもの (マクロの中身を変えた定義し直し、<c>#ifdef A B</c> の余分な語) も誤りにする。
/// 共通のプリプロセッサの報告の重要度をエラーに差し替える。設定で重要度を書けば、そちらが勝つ。
/// </para>
/// <para>
/// 共通のプリプロセッサが何も言わないもの (<c>#define F(a b)</c>、マクロの本体の先頭と末尾の <c>##</c>、条件の無い <c>#if</c>、
/// Godot が知らない <c>#pragma</c>、到達する <c>#error</c>) は、ファイルの指令の行を読んで報告する。
/// </para>
/// </remarks>
internal static class GodotDirectives
{
    /// <summary>Godot が知っている <c>#pragma</c>。</summary>
    private const string KnownPragma = "disable_preprocessor";

    /// <summary>共通のプリプロセッサの報告を Godot の規則に合わせ、Godot にだけある誤りを足す。</summary>
    /// <param name="text">解析しているファイル。</param>
    /// <param name="preprocessed">展開の結果。</param>
    /// <param name="diagnostics">報告を足す先。</param>
    public static void Check(SourceText text, PreprocessResult preprocessed, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (Diagnostic diagnostic in preprocessed.Diagnostics)
        {
            diagnostics.Add(diagnostic.Descriptor == ShaderDescriptors.MacroRedefinition || diagnostic.Descriptor == ShaderDescriptors.ExtraTokensAfterConditionName
                ? diagnostic.WithSeverity(DiagnosticSeverity.Error)
                : diagnostic);
        }

        // どの構成でも到達する #error は、Godot ではコンパイルの誤りである。
        foreach (Location location in preprocessed.ReachedErrorDirectives.Where(l => string.Equals(l.FilePath, text.FilePath, StringComparison.Ordinal)))
        {
            diagnostics.Add(Diagnostic.Create(ShaderDescriptors.PreprocessorError, location, "#error に到達しました。"));
        }

        foreach (ImmutableArray<ShaderSyntaxToken> line in DirectiveLines(text))
        {
            CheckLine(line, diagnostics);
        }
    }

    /// <summary>指令 1 行を調べる。<paramref name="line"/> は <c>#</c> から行の終わりまで。</summary>
    private static void CheckLine(ImmutableArray<ShaderSyntaxToken> line, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (line.Length < 2)
        {
            return;
        }

        void Report(ShaderSyntaxToken token, string message)
        {
            if (token.GetLocation() is { } location)
            {
                diagnostics.Add(Diagnostic.Create(ShaderDescriptors.PreprocessorError, location, message));
            }
        }

        ShaderSyntaxToken directive = line[1];
        ImmutableArray<ShaderSyntaxToken> rest = line[2..];

        switch (directive.Text)
        {
            case "define" when rest.Length >= 1:
                CheckDefine(rest, Report);
                break;

            case "if" or "elif" when rest.IsEmpty:
                Report(directive, $"#{directive.Text} に条件がありません。");
                break;

            case "pragma" when rest.IsEmpty:
                Report(directive, "#pragma の後ろに指定がありません。");
                break;

            case "pragma" when rest[0].Text != KnownPragma || rest.Length > 1:
                Report(rest[0], $"Godot は #pragma {rest[0].Text} を知りません (使用できるのは #pragma {KnownPragma} だけです)。");
                break;
        }
    }

    /// <summary><c>#define</c> の仮引数の並びと、本体の <c>##</c> の位置を調べる。</summary>
    private static void CheckDefine(ImmutableArray<ShaderSyntaxToken> rest, Action<ShaderSyntaxToken, string> report)
    {
        ShaderSyntaxToken name = rest[0];
        int bodyStart = 1;

        // 関数形式のマクロは、名前と ( の間に空白が無い。
        if (rest.Length > 1 && rest[1].Kind == ShaderSyntaxKind.OpenParenToken && rest[1].Span.Start == name.Span.End)
        {
            int i = 2;

            for (; i < rest.Length && rest[i].Kind != ShaderSyntaxKind.CloseParenToken; i++)
            {
                // 名前が 2 つ続く (#define F(a b))。名前でも , でもないものは、共通のプリプロセッサが報告する。
                if (rest[i].Kind == ShaderSyntaxKind.IdentifierToken && rest[i - 1].Kind == ShaderSyntaxKind.IdentifierToken)
                {
                    report(rest[i], "マクロの仮引数の間にはカンマが必要です。");
                    return;
                }
            }

            bodyStart = i + 1;
        }

        if (bodyStart < rest.Length)
        {
            if (rest[bodyStart].Kind == ShaderSyntaxKind.HashHashToken)
            {
                report(rest[bodyStart], "マクロの本体の先頭に ## は書けません。");
            }
            else if (rest[^1].Kind == ShaderSyntaxKind.HashHashToken)
            {
                report(rest[^1], "マクロの本体の末尾に ## は書けません。");
            }
        }
    }

    /// <summary>ファイルを字句に分け、行の頭の <c>#</c> から始まる指令の行 (行の継続を含む) を返す。</summary>
    private static IEnumerable<ImmutableArray<ShaderSyntaxToken>> DirectiveLines(SourceText text)
    {
        ImmutableArray<ShaderSyntaxToken> tokens = new ShaderLexer(text) { ReportsUnterminatedComment = false }.Lex(out _);

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Kind != ShaderSyntaxKind.HashToken || !tokens[i].IsAtLineStart)
            {
                continue;
            }

            int end = i + 1;

            while (end < tokens.Length && !tokens[end].IsAtLineStart && tokens[end].Kind != ShaderSyntaxKind.EndOfFileToken)
            {
                end++;
            }

            yield return tokens[i..end];
            i = end - 1;
        }
    }
}
