using System.Collections.Immutable;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.LanguageServer;

/// <summary>補完するカーソルの位置が、何を書く場所か。</summary>
internal enum CompletionPlace
{
    /// <summary>何も出さない (コメント・文字列・ShaderLab の部分・新しく付ける名前など)。</summary>
    None,

    /// <summary>HLSL のコード。</summary>
    Code,

    /// <summary><c>#</c> に続く指令の名前。</summary>
    DirectiveName,

    /// <summary><c>#pragma</c> に続く名前。</summary>
    PragmaName,

    /// <summary><c>#pragma vertex</c> などに続く関数の名前。</summary>
    EntryPoint,

    /// <summary><c>#if</c> / <c>#elif</c> の条件。<c>defined</c> も書ける。</summary>
    Condition,

    /// <summary><c>#ifdef</c> / <c>#ifndef</c> の名前。</summary>
    DefinedName,

    /// <summary><c>#undef</c> の名前。</summary>
    MacroName,
}

/// <summary>
/// カーソルの位置が何を書く場所かを決める。
/// </summary>
/// <remarks>
/// <para>
/// <b>指令の行は構文木に残らないので、展開前のトークン列から決める。</b>
/// 木だけを見ていると、<c>#</c> の後ろでも HLSL のコードの候補 (組み込み関数や型) を出してしまう。
/// </para>
/// <para>
/// <b>分からない場所では何も出さない。</b>
/// 書けない名前を並べると、選んだ利用者がコンパイルの失敗で初めて気づくことになる。
/// </para>
/// </remarks>
internal static class CompletionPlaces
{
    /// <summary>関数の名前を続けて書く <c>#pragma</c>。</summary>
    private static readonly HashSet<string> EntryPointPragmas = new(StringComparer.Ordinal)
    {
        "vertex", "fragment", "geometry", "hull", "domain", "kernel", "surface",
    };

    /// <summary>カーソルの位置が何を書く場所かを決める。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>場所。</returns>
    public static CompletionPlace Find(ShaderCompilation compilation, int offset)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        if (!IsInHlsl(compilation, offset))
        {
            return CompletionPlace.None;
        }

        ImmutableArray<HlslSyntaxToken> tokens = compilation.CodeTokens;

        if (IsInCommentOrString(compilation.CodeText, tokens, offset))
        {
            return CompletionPlace.None;
        }

        // カーソルのある行で、カーソルより前から始まるトークン。
        SourceText text = compilation.CodeText;
        int lineStart = offset - text.GetLinePosition(Math.Min(offset, text.Length)).Character;
        List<HlslSyntaxToken> line = [];

        foreach (HlslSyntaxToken token in tokens)
        {
            if (token.Kind == HlslSyntaxKind.EndOfFileToken || token.Span.Start >= offset)
            {
                break;
            }

            if (token.Span.Start >= lineStart)
            {
                line.Add(token);
            }
        }

        if (line.Count == 0 || line[0].Kind != HlslSyntaxKind.HashToken)
        {
            return CompletionPlace.Code;
        }

        return FindInDirective(line, offset);
    }

    /// <summary>指令の行の中で、何を書く場所かを決める。</summary>
    /// <param name="line"><c>#</c> から、カーソルより前から始まるトークンまで。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>場所。</returns>
    private static CompletionPlace FindInDirective(List<HlslSyntaxToken> line, int offset)
    {
        // 書きかけの語はカーソルの直前で終わっている。空白を挟めば、次の語を書き始める位置である。
        bool typing = line[^1].Span.End == offset && IsWord(line[^1]);
        int written = line.Count - 1 - (typing ? 1 : 0);

        // # の直後か、指令の名前を書きかけている。
        if (written == 0)
        {
            return CompletionPlace.DirectiveName;
        }

        // 指令の名前と、カーソルまでに書き終えた引数。
        string directive = line[1].Text;
        int arguments = written - 1;

        return directive switch
        {
            "pragma" when arguments == 0 => CompletionPlace.PragmaName,
            "pragma" when arguments == 1 && EntryPointPragmas.Contains(line[2].Text) => CompletionPlace.EntryPoint,
            "if" or "elif" => CompletionPlace.Condition,
            "ifdef" or "ifndef" when arguments == 0 => CompletionPlace.DefinedName,
            "undef" when arguments == 0 => CompletionPlace.MacroName,

            // 本体は HLSL のコードである。名前と仮引数は新しく付けるものなので出さない。
            "define" when arguments > 0 && !IsInParameters(line) => CompletionPlace.Code,
            _ => CompletionPlace.None,
        };
    }

    /// <summary>関数形式マクロの仮引数を書いているかを判定する。</summary>
    /// <param name="line"><c>#</c> から、カーソルより前から始まるトークンまで。</param>
    /// <returns>名前の直後の <c>(</c> がまだ閉じていなければ <see langword="true"/>。</returns>
    private static bool IsInParameters(List<HlslSyntaxToken> line)
    {
        // #define NAME( は、名前と ( の間に空白が無いときだけ仮引数になる。
        if (line.Count < 4
            || line[3].Kind != HlslSyntaxKind.OpenParenToken
            || line[3].Span.Start != line[2].Span.End)
        {
            return false;
        }

        return !line.Skip(4).Any(t => t.Kind == HlslSyntaxKind.CloseParenToken);
    }

    /// <summary>名前として書きかけうるトークンかを判定する。</summary>
    /// <param name="token">対象のトークン。</param>
    /// <returns>識別子かキーワードなら <see langword="true"/>。</returns>
    /// <remarks><c>#if</c> の <c>if</c> は、識別子ではなくキーワードとして字句解析される。</remarks>
    private static bool IsWord(HlslSyntaxToken token)
        => token.Text.Length > 0 && (char.IsLetter(token.Text[0]) || token.Text[0] == '_');

    /// <summary>カーソルが HLSL のコードの中にあるかを判定する。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>HLSL 単体のファイルか、<c>.shader</c> の HLSL のブロックの中なら <see langword="true"/>。</returns>
    /// <remarks>
    /// <c>.shader</c> の ShaderLab の部分 (<c>Properties</c> や <c>Tags</c>) で HLSL の候補を出しても、書ける場所ではない。
    /// </remarks>
    private static bool IsInHlsl(ShaderCompilation compilation, int offset)
    {
        if (compilation.IsStandaloneHlsl)
        {
            return true;
        }

        foreach (SyntaxNode node in compilation.ShaderLabTree.Root.DescendantNodesAndSelf())
        {
            if (node is ProgramBlockSyntax { Language: ProgramBlockLanguage.Hlsl } block
                && block.BodySpan.Start <= offset
                && offset <= block.BodySpan.End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>カーソルがコメントか文字列の中にあるかを判定する。</summary>
    /// <param name="text">トークンを切り出したテキスト。</param>
    /// <param name="tokens">展開前のトークン。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>中にあれば <see langword="true"/>。</returns>
    private static bool IsInCommentOrString(SourceText text, ImmutableArray<HlslSyntaxToken> tokens, int offset)
    {
        foreach (HlslSyntaxToken token in tokens)
        {
            if (token.FullSpan.Start > offset)
            {
                break;
            }

            if (token.FullSpan.End < offset)
            {
                continue;
            }

            if (token.Kind == HlslSyntaxKind.StringLiteralToken
                && token.Span.Start < offset
                && (offset < token.Span.End || !IsClosedString(token)))
            {
                return true;
            }

            foreach (HlslSyntaxTrivia trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
            {
                // 行コメントは行の終わりまで続く。閉じたブロックコメントの直後は外である。
                bool inside = trivia.Kind switch
                {
                    HlslSyntaxKind.SingleLineCommentTrivia => trivia.Span.Start < offset && offset <= trivia.Span.End,
                    HlslSyntaxKind.MultiLineCommentTrivia => trivia.Span.Start < offset
                        && (offset < trivia.Span.End || !text.ToString(trivia.Span).EndsWith("*/", StringComparison.Ordinal)),
                    _ => false,
                };

                if (inside)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>文字列のトークンが閉じているかを判定する。</summary>
    /// <param name="token">文字列のトークン。</param>
    /// <returns>閉じていれば <see langword="true"/>。</returns>
    private static bool IsClosedString(HlslSyntaxToken token)
        => token.Text.Length >= 2 && token.Text[^1] == '"';
}
