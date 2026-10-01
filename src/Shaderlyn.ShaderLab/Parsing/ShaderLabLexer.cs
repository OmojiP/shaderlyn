using System.Collections.Immutable;
using Shaderlyn.Core.Text;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.ShaderLab.Parsing;

/// <summary>
/// ShaderLab のソーステキストをトークン列へ分解する。
/// </summary>
/// <remarks>
/// <para>
/// <b>字句解析は決して例外を投げない。</b>
/// 未知の文字は <see cref="SyntaxKind.BadToken"/> として 1 文字ずつ返し、
/// 閉じられていない文字列やコメントはファイル末尾までを 1 つのトークンとして返す。
/// 壊れた入力に対しても必ずトークン列が最後まで生成されることを保証しており、
/// これがパーサの「例外を投げない」という性質の土台になっている。
/// </para>
/// <para>
/// 生成されるトークンの <see cref="SyntaxToken.FullSpan"/> は隙間なく連結し、
/// 全体でファイル全体を覆う。この性質はラウンドトリップテストで検証している。
/// </para>
/// </remarks>
internal sealed class ShaderLabLexer
{
    private readonly SourceText _text;
    private readonly List<LexerDiagnostic> _diagnostics = [];
    private int _position;

    /// <summary>
    /// 字句解析ツールを生成する。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    public ShaderLabLexer(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
    }

    /// <summary>
    /// 字句解析の過程で検出した問題。
    /// </summary>
    /// <param name="Span">問題のある範囲。</param>
    /// <param name="Message">問題の内容。</param>
    public readonly record struct LexerDiagnostic(TextSpan Span, string Message);

    /// <summary>
    /// ソーステキスト全体をトークン列へ分解する。
    /// </summary>
    /// <param name="diagnostics">字句解析の過程で検出した問題。</param>
    /// <returns>
    /// トークンの列。末尾には必ず <see cref="SyntaxKind.EndOfFileToken"/> が 1 つ含まれる。
    /// </returns>
    public ImmutableArray<SyntaxToken> Lex(out ImmutableArray<LexerDiagnostic> diagnostics)
    {
        ImmutableArray<SyntaxToken>.Builder tokens = ImmutableArray.CreateBuilder<SyntaxToken>();

        while (true)
        {
            SyntaxToken token = LexToken();
            tokens.Add(token);

            if (token.Kind == SyntaxKind.EndOfFileToken)
            {
                break;
            }
        }

        diagnostics = [.. _diagnostics];
        return tokens.ToImmutable();
    }

    /// <summary>
    /// 先行 trivia、トークン本体、後続 trivia をこの順に読んで 1 つのトークンを組み立てる。
    /// </summary>
    /// <returns>読み取ったトークン。</returns>
    private SyntaxToken LexToken()
    {
        ImmutableArray<SyntaxTrivia> leading = LexTrivia(isTrailing: false);

        int start = _position;
        (SyntaxKind kind, string? valueText) = LexTokenCore();
        TextSpan span = TextSpan.FromBounds(start, _position);
        string text = _text.ToString(span);

        ImmutableArray<SyntaxTrivia> trailing = kind == SyntaxKind.EndOfFileToken
            ? []
            : LexTrivia(isTrailing: true);

        return new SyntaxToken(kind, span, text, leading, trailing, valueText);
    }

    /// <summary>
    /// 空白・改行・コメントを読み進める。
    /// </summary>
    /// <param name="isTrailing">
    /// 後続 trivia として読んでいるかどうか。後続の場合は改行を 1 つ読んだ時点で打ち切る。
    /// </param>
    /// <returns>読み取った trivia の列。</returns>
    /// <remarks>
    /// 後続 trivia を改行までで打ち切るのは Roslyn と同じ帰属規則である。
    /// これにより行末コメントは直前のトークンに付き、
    /// 次の行の先頭にあるコメントは次のトークンに付く。
    /// 抑制コメントを「どの行に対する指示か」で解釈するために必要な区別である。
    /// </remarks>
    private ImmutableArray<SyntaxTrivia> LexTrivia(bool isTrailing)
    {
        ImmutableArray<SyntaxTrivia>.Builder trivia = ImmutableArray.CreateBuilder<SyntaxTrivia>();

        while (_position < _text.Length)
        {
            char current = _text[_position];

            if (current is '\r' or '\n')
            {
                int start = _position;
                if (current == '\r' && Peek(1) == '\n')
                {
                    _position++;
                }

                _position++;
                trivia.Add(new SyntaxTrivia(SyntaxKind.EndOfLineTrivia, TextSpan.FromBounds(start, _position)));

                if (isTrailing)
                {
                    break;
                }
            }
            else if (char.IsWhiteSpace(current))
            {
                int start = _position;
                while (_position < _text.Length && char.IsWhiteSpace(_text[_position]) && _text[_position] is not ('\r' or '\n'))
                {
                    _position++;
                }

                trivia.Add(new SyntaxTrivia(SyntaxKind.WhitespaceTrivia, TextSpan.FromBounds(start, _position)));
            }
            else if (current == '/' && Peek(1) == '/')
            {
                int start = _position;
                while (_position < _text.Length && _text[_position] is not ('\r' or '\n'))
                {
                    _position++;
                }

                trivia.Add(new SyntaxTrivia(SyntaxKind.SingleLineCommentTrivia, TextSpan.FromBounds(start, _position)));
            }
            else if (current == '/' && Peek(1) == '*')
            {
                trivia.Add(LexMultiLineComment());
            }
            else
            {
                break;
            }
        }

        return trivia.ToImmutable();
    }

    /// <summary>
    /// 複数行コメントを読み進める。
    /// </summary>
    /// <returns>読み取った trivia。</returns>
    /// <remarks>
    /// 閉じられていない場合はファイル末尾までをコメントとみなし、診断を 1 件出す。
    /// 例外にせず読み進めるのは、閉じ忘れの 1 か所で解析が止まると
    /// ファイル内の他の問題が一切報告されなくなるためである。
    /// </remarks>
    private SyntaxTrivia LexMultiLineComment()
    {
        int start = _position;
        _position += 2;

        while (_position < _text.Length)
        {
            if (_text[_position] == '*' && Peek(1) == '/')
            {
                _position += 2;
                return new SyntaxTrivia(SyntaxKind.MultiLineCommentTrivia, TextSpan.FromBounds(start, _position));
            }

            _position++;
        }

        _diagnostics.Add(new LexerDiagnostic(
            TextSpan.FromBounds(start, Math.Min(start + 2, _text.Length)),
            "複数行コメントが '*/' で閉じられていません。"));

        return new SyntaxTrivia(SyntaxKind.MultiLineCommentTrivia, TextSpan.FromBounds(start, _position));
    }

    /// <summary>
    /// トークン本体を 1 つ読み進める。
    /// </summary>
    /// <returns>読み取ったトークンの種別と、文字列リテラルの場合はその値。</returns>
    private (SyntaxKind Kind, string? ValueText) LexTokenCore()
    {
        if (_position >= _text.Length)
        {
            return (SyntaxKind.EndOfFileToken, null);
        }

        char current = _text[_position];

        switch (current)
        {
            case '{': _position++; return (SyntaxKind.OpenBraceToken, null);
            case '}': _position++; return (SyntaxKind.CloseBraceToken, null);
            case '(': _position++; return (SyntaxKind.OpenParenToken, null);
            case ')': _position++; return (SyntaxKind.CloseParenToken, null);
            case '[': _position++; return (SyntaxKind.OpenBracketToken, null);
            case ']': _position++; return (SyntaxKind.CloseBracketToken, null);
            case ',': _position++; return (SyntaxKind.CommaToken, null);
            case '=': _position++; return (SyntaxKind.EqualsToken, null);
            case '"': return LexStringLiteral();
        }

        if (char.IsDigit(current) || (current is '-' or '+' or '.' && char.IsDigit(Peek(1))))
        {
            return LexNumericLiteral();
        }

        if (IsIdentifierStart(current))
        {
            return LexIdentifierOrProgramBlock();
        }

        // ドットは属性の引数に現れる修飾名で使う (例: [Enum(UnityEngine.Rendering.CullMode)])。
        // 数値の判定より後に置くこと。先に判定すると .5 のような小数が
        // ドットと数値に分割されてしまう。
        if (current == '.')
        {
            _position++;
            return (SyntaxKind.DotToken, null);
        }

        // どの規則にも当てはまらない文字は 1 文字ずつ不正トークンとして返す。
        // まとめて読み飛ばさないのは、後続に正しい構文が続いている場合に
        // そこから解析を再開できるようにするためである。
        _position++;
        return (SyntaxKind.BadToken, null);
    }

    /// <summary>
    /// 二重引用符で囲まれた文字列を読み進める。
    /// </summary>
    /// <returns>トークンの種別と、引用符を外した値。</returns>
    /// <remarks>
    /// ShaderLab の文字列にエスケープシーケンスは無い。
    /// シェーダー名・表示名・タグの値のいずれも、引用符で囲んだ生の文字列である。
    /// 改行をまたぐこともできないため、改行に達した時点で閉じ忘れとみなす。
    /// </remarks>
    private (SyntaxKind Kind, string? ValueText) LexStringLiteral()
    {
        int start = _position;
        _position++;

        while (_position < _text.Length && _text[_position] != '"' && _text[_position] is not ('\r' or '\n'))
        {
            _position++;
        }

        if (_position >= _text.Length || _text[_position] is '\r' or '\n')
        {
            _diagnostics.Add(new LexerDiagnostic(
                TextSpan.FromBounds(start, _position),
                "文字列が '\"' で閉じられていません。"));

            return (SyntaxKind.StringLiteralToken, _text.ToString(TextSpan.FromBounds(start + 1, _position)));
        }

        _position++;
        return (SyntaxKind.StringLiteralToken, _text.ToString(TextSpan.FromBounds(start + 1, _position - 1)));
    }

    /// <summary>
    /// 数値、または数字で始まる識別子を読み進める。
    /// </summary>
    /// <returns>トークンの種別。</returns>
    /// <remarks>
    /// <para>
    /// 符号・小数点・指数表記を受け付ける。値の妥当性 (範囲や桁数) は検査しない。
    /// 数値として解釈できるかどうかは、その値が何を表すかを知っている構文解析側で判断する。
    /// </para>
    /// <para>
    /// <b>ShaderLab には数字で始まる識別子がある。</b>
    /// プロパティの型名 <c>2D</c> / <c>3D</c> / <c>2DArray</c> がそれで、
    /// これらを数値と識別子に分割してしまうと、
    /// テクスチャプロパティを持つほぼすべてのシェーダーが構文エラーになる。
    /// そのため数字の並びの直後に英字が続く場合は、全体を識別子として読み直す。
    /// </para>
    /// <para>
    /// 指数表記との区別は「<c>e</c> の後ろに (符号付きの) 数字が続くか」で行う。
    /// <c>1e-5</c> は数値、<c>2D</c> は識別子になる。
    /// 符号が付いている場合 (<c>-1</c> など) は識別子になりえないため、この読み直しは行わない。
    /// </para>
    /// </remarks>
    private (SyntaxKind Kind, string? ValueText) LexNumericLiteral()
    {
        bool hasSign = _text[_position] is '-' or '+';
        if (hasSign)
        {
            _position++;
        }

        while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.'))
        {
            _position++;
        }

        // 指数表記 (1e-5 など)。Range の既定値などで現れうる。
        if (_position < _text.Length && (_text[_position] is 'e' or 'E'))
        {
            int savedPosition = _position;
            _position++;

            if (_position < _text.Length && _text[_position] is '-' or '+')
            {
                _position++;
            }

            if (_position < _text.Length && char.IsDigit(_text[_position]))
            {
                while (_position < _text.Length && char.IsDigit(_text[_position]))
                {
                    _position++;
                }

                return (SyntaxKind.NumericLiteralToken, null);
            }

            // 'e' の後ろが数字でなければ指数表記ではない。位置を戻して識別子として読み直す。
            _position = savedPosition;
        }

        if (!hasSign && _position < _text.Length && IsIdentifierStart(_text[_position]))
        {
            while (_position < _text.Length && IsIdentifierPart(_text[_position]))
            {
                _position++;
            }

            return (SyntaxKind.IdentifierToken, null);
        }

        return (SyntaxKind.NumericLiteralToken, null);
    }

    /// <summary>
    /// 識別子を読み進める。埋め込みコードブロックの開始キーワードだった場合はブロック全体を読む。
    /// </summary>
    /// <returns>トークンの種別。</returns>
    private (SyntaxKind Kind, string? ValueText) LexIdentifierOrProgramBlock()
    {
        int start = _position;

        while (_position < _text.Length && IsIdentifierPart(_text[_position]))
        {
            _position++;
        }

        ReadOnlySpan<char> identifier = _text.AsSpan(TextSpan.FromBounds(start, _position));

        if (ProgramBlockDelimiters.TryGetByStartKeyword(identifier, out ProgramBlockDelimiter delimiter))
        {
            ConsumeProgramBlockBody(start, delimiter.StartKeyword, delimiter.EndKeyword);
            return (SyntaxKind.ProgramBlockToken, null);
        }

        return (SyntaxKind.IdentifierToken, null);
    }

    /// <summary>
    /// 埋め込みコードブロックの中身を、対応する終了キーワードまで読み飛ばす。
    /// </summary>
    /// <param name="blockStart">ブロック開始位置 (開始キーワードの先頭)。</param>
    /// <param name="startKeyword">開始キーワード。診断メッセージに使う。</param>
    /// <param name="endKeyword">対応する終了キーワード。</param>
    /// <remarks>
    /// <para>
    /// 中身は HLSL / Cg / GLSL であり ShaderLab の字句規則が通用しないため、
    /// 終了キーワードの探索だけを行う。
    /// </para>
    /// <para>
    /// 終了キーワードは<b>識別子として独立している場合のみ</b>一致とみなす。
    /// 単純な部分文字列検索にすると <c>MY_ENDCG</c> のような識別子の一部に反応して
    /// ブロックが途中で切れてしまう。
    /// </para>
    /// <para>
    /// <b>コメントと文字列の中は探索対象から外す。</b>
    /// <c>// ENDCG</c> と書かれたコメントで終端したと誤判定すると、
    /// そこから先の HLSL コードが ShaderLab として解釈され、
    /// 実際には正しいファイルに対して大量の誤ったエラーが出る。
    /// リンタとして、正しいコードを誤りと報告することは最も避けるべき失敗である。
    /// </para>
    /// </remarks>
    private void ConsumeProgramBlockBody(int blockStart, string startKeyword, string endKeyword)
    {
        while (_position < _text.Length)
        {
            char current = _text[_position];

            if (current == '/' && Peek(1) == '/')
            {
                while (_position < _text.Length && _text[_position] is not ('\r' or '\n'))
                {
                    _position++;
                }

                continue;
            }

            if (current == '/' && Peek(1) == '*')
            {
                _position += 2;
                while (_position < _text.Length && !(_text[_position] == '*' && Peek(1) == '/'))
                {
                    _position++;
                }

                _position = Math.Min(_position + 2, _text.Length);
                continue;
            }

            if (current is '"' or '\'')
            {
                SkipProgramBlockStringLiteral(current);
                continue;
            }

            if (!IsIdentifierStart(current))
            {
                _position++;
                continue;
            }

            int wordStart = _position;
            while (_position < _text.Length && IsIdentifierPart(_text[_position]))
            {
                _position++;
            }

            if (_text.AsSpan(TextSpan.FromBounds(wordStart, _position)).Equals(endKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        _diagnostics.Add(new LexerDiagnostic(
            TextSpan.FromBounds(blockStart, Math.Min(blockStart + startKeyword.Length, _text.Length)),
            $"'{startKeyword}' に対応する '{endKeyword}' が見つかりません。"));
    }

    /// <summary>
    /// 埋め込みコードブロック内の文字列リテラルを読み飛ばす。
    /// </summary>
    /// <param name="quote">文字列を囲む引用符。</param>
    /// <remarks>
    /// HLSL の <c>#include "..."</c> や文字列を、終了キーワードの探索対象から外すために使う。
    /// バックスラッシュによるエスケープを解釈する点が ShaderLab の文字列と異なる。
    /// 行をまたいだ時点で打ち切るのは、閉じ忘れがあった場合に
    /// ファイル末尾まで読み飛ばしてブロック全体を失うのを避けるためである。
    /// </remarks>
    private void SkipProgramBlockStringLiteral(char quote)
    {
        _position++;

        while (_position < _text.Length && _text[_position] is not ('\r' or '\n'))
        {
            if (_text[_position] == '\\')
            {
                _position = Math.Min(_position + 2, _text.Length);
                continue;
            }

            if (_text[_position] == quote)
            {
                _position++;
                return;
            }

            _position++;
        }
    }

    /// <summary>指定した相対位置の文字を取得する。範囲外の場合は終端文字を返す。</summary>
    /// <param name="offset">現在位置からの相対オフセット。</param>
    /// <returns>その位置の文字。範囲外の場合は <c>'\0'</c>。</returns>
    private char Peek(int offset)
    {
        int index = _position + offset;
        return index < _text.Length ? _text[index] : '\0';
    }

    /// <summary>識別子の先頭に使える文字かを判定する。</summary>
    /// <param name="c">判定する文字。</param>
    /// <returns>使える場合は <see langword="true"/>。</returns>
    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    /// <summary>識別子の 2 文字目以降に使える文字かを判定する。</summary>
    /// <param name="c">判定する文字。</param>
    /// <returns>使える場合は <see langword="true"/>。</returns>
    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
