using System.Collections.Immutable;
using Shaderlyn.Core.Text;

namespace Shaderlyn.CFamily.Syntax;

/// <summary>
/// HLSL / Cg のソーステキストをトークン列へ分解する。
/// </summary>
/// <remarks>
/// <para>
/// <b>この字句解析ツールは決して例外を投げない。</b>
/// 未知の文字は <see cref="ShaderSyntaxKind.BadToken"/> として 1 文字ずつ返し、
/// 閉じられていない文字列やコメントはその行またはファイル末尾までを 1 つのトークンとして返す。
/// </para>
/// <para>
/// プリプロセッサ指令は解釈しない。<c>#</c> をトークンとして返し、
/// 行頭かどうかの情報を各トークンに持たせるところまでが役割である。
/// 指令の解釈とマクロ展開は <see cref="Preprocessing.ShaderPreprocessor"/> が行う。
/// </para>
/// </remarks>
public sealed class ShaderLexer
{
    private readonly SourceText _text;
    private readonly List<LexerDiagnostic> _diagnostics = [];

    /// <summary>直前のトークンが、行頭の <c>#</c> か。</summary>
    private bool _afterDirectiveHash;

    /// <summary>今の行が <c>#error</c> / <c>#warning</c> の行か。後ろは文なので、閉じていないリテラルを誤りにしない。</summary>
    private bool _inMessageDirective;
    private int _position;
    private bool _atLineStart = true;

    /// <summary>
    /// 字句解析ツールを生成する。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    public ShaderLexer(SourceText text)
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
    /// <returns>トークンの列。末尾には必ず終端トークンが 1 つ含まれる。</returns>
    public ImmutableArray<ShaderSyntaxToken> Lex(out ImmutableArray<LexerDiagnostic> diagnostics)
    {
        ImmutableArray<ShaderSyntaxToken>.Builder tokens = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        while (true)
        {
            ShaderSyntaxToken token = LexToken();
            tokens.Add(token);

            if (token.Kind == ShaderSyntaxKind.EndOfFileToken)
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
    private ShaderSyntaxToken LexToken()
    {
        ImmutableArray<ShaderSyntaxTrivia> leading = LexTrivia(isTrailing: false);
        bool atLineStart = _atLineStart;

        if (atLineStart)
        {
            _inMessageDirective = false;
        }

        int start = _position;
        (ShaderSyntaxKind kind, string? valueText) = LexTokenCore();
        TextSpan span = TextSpan.FromBounds(start, _position);
        string text = _text.ToString(span);

        // #error / #warning の後ろは文である。英語の doesn't のアポストロフィを、閉じていない文字のリテラルとして報告しない。
        if (_afterDirectiveHash && kind == ShaderSyntaxKind.IdentifierToken && text is "error" or "warning")
        {
            _inMessageDirective = true;
        }

        _afterDirectiveHash = atLineStart && kind == ShaderSyntaxKind.HashToken;

        // 本体を 1 つ読んだので、次のトークンはもう行頭ではない。
        _atLineStart = false;

        ImmutableArray<ShaderSyntaxTrivia> trailing = kind == ShaderSyntaxKind.EndOfFileToken
            ? []
            : LexTrivia(isTrailing: true);

        return new ShaderSyntaxToken(kind, _text, span, text, atLineStart, leading, trailing, valueText);
    }

    /// <summary>
    /// 空白・改行・行継続・コメントを読み進める。
    /// </summary>
    /// <param name="isTrailing">後続 trivia として読んでいるかどうか。</param>
    /// <returns>読み取った trivia の列。</returns>
    /// <remarks>
    /// 改行を読んだ時点で「次のトークンは行頭」と記録する。
    /// <b>行継続 (<c>\</c> + 改行) では記録しない。</b>
    /// 複数行にわたるマクロ定義の 2 行目以降が指令として誤解釈されるのを防ぐためである。
    /// </remarks>
    private ImmutableArray<ShaderSyntaxTrivia> LexTrivia(bool isTrailing)
    {
        ImmutableArray<ShaderSyntaxTrivia>.Builder trivia = ImmutableArray.CreateBuilder<ShaderSyntaxTrivia>();

        while (_position < _text.Length)
        {
            char current = _text[_position];

            if (current == '\\' && IsLineBreakAt(SkipHorizontalWhitespaceFrom(_position + 1)))
            {
                int start = _position;
                _position = SkipHorizontalWhitespaceFrom(_position + 1);
                ConsumeLineBreak();
                trivia.Add(new ShaderSyntaxTrivia(
                    ShaderSyntaxKind.LineContinuationTrivia, TextSpan.FromBounds(start, _position)));
                continue;
            }

            if (current is '\r' or '\n')
            {
                int start = _position;
                ConsumeLineBreak();
                trivia.Add(new ShaderSyntaxTrivia(
                    ShaderSyntaxKind.EndOfLineTrivia, TextSpan.FromBounds(start, _position)));
                _atLineStart = true;

                if (isTrailing)
                {
                    break;
                }

                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                int start = _position;
                _position = SkipHorizontalWhitespaceFrom(_position);
                trivia.Add(new ShaderSyntaxTrivia(
                    ShaderSyntaxKind.WhitespaceTrivia, TextSpan.FromBounds(start, _position)));
                continue;
            }

            if (current == '/' && Peek(1) == '/')
            {
                trivia.Add(LexSingleLineComment());
                continue;
            }

            if (current == '/' && Peek(1) == '*')
            {
                trivia.Add(LexMultiLineComment());
                continue;
            }

            break;
        }

        return trivia.ToImmutable();
    }

    /// <summary>
    /// 単一行コメントを読み進める。
    /// </summary>
    /// <returns>読み取った trivia。</returns>
    /// <remarks>
    /// 行継続で次の行へ続くコメントに対応する必要がある。
    /// <c>// コメント \</c> と書くと次の行もコメントの一部になり、
    /// これを見落とすと次の行のコードが有効なコードとして解析されてしまう。
    /// </remarks>
    private ShaderSyntaxTrivia LexSingleLineComment()
    {
        int start = _position;

        while (_position < _text.Length)
        {
            if (IsLineBreakAt(_position))
            {
                break;
            }

            if (_text[_position] == '\\' && IsLineBreakAt(SkipHorizontalWhitespaceFrom(_position + 1)))
            {
                _position = SkipHorizontalWhitespaceFrom(_position + 1);
                ConsumeLineBreak();
                continue;
            }

            _position++;
        }

        return new ShaderSyntaxTrivia(ShaderSyntaxKind.SingleLineCommentTrivia, TextSpan.FromBounds(start, _position));
    }

    /// <summary>複数行コメントを読み進める。</summary>
    /// <returns>読み取った trivia。</returns>
    private ShaderSyntaxTrivia LexMultiLineComment()
    {
        int start = _position;
        _position += 2;

        while (_position < _text.Length)
        {
            if (_text[_position] == '*' && Peek(1) == '/')
            {
                _position += 2;
                return new ShaderSyntaxTrivia(
                    ShaderSyntaxKind.MultiLineCommentTrivia, TextSpan.FromBounds(start, _position));
            }

            _position++;
        }

        _diagnostics.Add(new LexerDiagnostic(
            TextSpan.FromBounds(start, Math.Min(start + 2, _text.Length)),
            "複数行コメントが '*/' で閉じられていません。"));

        return new ShaderSyntaxTrivia(ShaderSyntaxKind.MultiLineCommentTrivia, TextSpan.FromBounds(start, _position));
    }

    /// <summary>トークン本体を 1 つ読み進める。</summary>
    /// <returns>読み取ったトークンの種別と、リテラルの場合はその値。</returns>
    private (ShaderSyntaxKind Kind, string? ValueText) LexTokenCore()
    {
        if (_position >= _text.Length)
        {
            return (ShaderSyntaxKind.EndOfFileToken, null);
        }

        char current = _text[_position];

        if (current == '"')
        {
            return LexStringLiteral('"', ShaderSyntaxKind.StringLiteralToken);
        }

        if (current == '\'')
        {
            return LexStringLiteral('\'', ShaderSyntaxKind.CharacterLiteralToken);
        }

        if (char.IsDigit(current) || (current == '.' && char.IsDigit(Peek(1))))
        {
            return (LexNumericLiteral(), null);
        }

        if (IsIdentifierStart(current))
        {
            while (_position < _text.Length && IsIdentifierPart(_text[_position]))
            {
                _position++;
            }

            return (ShaderSyntaxKind.IdentifierToken, null);
        }

        return (LexPunctuation(), null);
    }

    /// <summary>
    /// 区切り記号と演算子を読み進める。
    /// </summary>
    /// <returns>読み取ったトークンの種別。</returns>
    /// <remarks>
    /// 長い記号から順に照合する (最長一致)。
    /// <c>&gt;&gt;=</c> を <c>&gt;&gt;</c> と <c>=</c> に分けてしまうと式の意味が変わるため、
    /// 3 文字・2 文字・1 文字の順に試す必要がある。
    /// </remarks>
    private ShaderSyntaxKind LexPunctuation()
    {
        char c0 = _text[_position];
        char c1 = Peek(1);
        char c2 = Peek(2);

        // 3 文字の演算子
        if (c0 == '<' && c1 == '<' && c2 == '=')
        {
            _position += 3;
            return ShaderSyntaxKind.LessThanLessThanEqualsToken;
        }

        if (c0 == '>' && c1 == '>' && c2 == '=')
        {
            _position += 3;
            return ShaderSyntaxKind.GreaterThanGreaterThanEqualsToken;
        }

        // 2 文字の演算子
        ShaderSyntaxKind? twoCharacter = (c0, c1) switch
        {
            ('+', '+') => ShaderSyntaxKind.PlusPlusToken,
            ('-', '-') => ShaderSyntaxKind.MinusMinusToken,
            ('+', '=') => ShaderSyntaxKind.PlusEqualsToken,
            ('-', '=') => ShaderSyntaxKind.MinusEqualsToken,
            ('*', '=') => ShaderSyntaxKind.AsteriskEqualsToken,
            ('/', '=') => ShaderSyntaxKind.SlashEqualsToken,
            ('%', '=') => ShaderSyntaxKind.PercentEqualsToken,
            ('=', '=') => ShaderSyntaxKind.EqualsEqualsToken,
            ('!', '=') => ShaderSyntaxKind.ExclamationEqualsToken,
            ('<', '=') => ShaderSyntaxKind.LessThanEqualsToken,
            ('>', '=') => ShaderSyntaxKind.GreaterThanEqualsToken,
            ('&', '&') => ShaderSyntaxKind.AmpersandAmpersandToken,
            ('|', '|') => ShaderSyntaxKind.BarBarToken,
            ('&', '=') => ShaderSyntaxKind.AmpersandEqualsToken,
            ('|', '=') => ShaderSyntaxKind.BarEqualsToken,
            ('^', '=') => ShaderSyntaxKind.CaretEqualsToken,
            ('<', '<') => ShaderSyntaxKind.LessThanLessThanToken,
            ('>', '>') => ShaderSyntaxKind.GreaterThanGreaterThanToken,
            (':', ':') => ShaderSyntaxKind.ColonColonToken,
            ('#', '#') => ShaderSyntaxKind.HashHashToken,
            _ => null,
        };

        if (twoCharacter is not null)
        {
            _position += 2;
            return twoCharacter.Value;
        }

        // 1 文字の記号
        ShaderSyntaxKind? oneCharacter = c0 switch
        {
            '{' => ShaderSyntaxKind.OpenBraceToken,
            '}' => ShaderSyntaxKind.CloseBraceToken,
            '(' => ShaderSyntaxKind.OpenParenToken,
            ')' => ShaderSyntaxKind.CloseParenToken,
            '[' => ShaderSyntaxKind.OpenBracketToken,
            ']' => ShaderSyntaxKind.CloseBracketToken,
            ';' => ShaderSyntaxKind.SemicolonToken,
            ',' => ShaderSyntaxKind.CommaToken,
            '.' => ShaderSyntaxKind.DotToken,
            ':' => ShaderSyntaxKind.ColonToken,
            '?' => ShaderSyntaxKind.QuestionToken,
            '+' => ShaderSyntaxKind.PlusToken,
            '-' => ShaderSyntaxKind.MinusToken,
            '*' => ShaderSyntaxKind.AsteriskToken,
            '/' => ShaderSyntaxKind.SlashToken,
            '%' => ShaderSyntaxKind.PercentToken,
            '=' => ShaderSyntaxKind.EqualsToken,
            '<' => ShaderSyntaxKind.LessThanToken,
            '>' => ShaderSyntaxKind.GreaterThanToken,
            '!' => ShaderSyntaxKind.ExclamationToken,
            '&' => ShaderSyntaxKind.AmpersandToken,
            '|' => ShaderSyntaxKind.BarToken,
            '^' => ShaderSyntaxKind.CaretToken,
            '~' => ShaderSyntaxKind.TildeToken,
            '#' => ShaderSyntaxKind.HashToken,
            _ => null,
        };

        _position++;
        return oneCharacter ?? ShaderSyntaxKind.BadToken;
    }

    /// <summary>
    /// 引用符で囲まれたリテラルを読み進める。
    /// </summary>
    /// <param name="quote">囲んでいる引用符。</param>
    /// <param name="kind">生成するトークンの種別。</param>
    /// <returns>トークンの種別と、引用符を外した値。</returns>
    /// <remarks>
    /// バックスラッシュによるエスケープを解釈する。
    /// 改行に達した時点で閉じ忘れとみなすのは、
    /// 閉じ忘れ 1 か所でファイル全体を文字列として飲み込まないためである。
    /// </remarks>
    private (ShaderSyntaxKind Kind, string? ValueText) LexStringLiteral(char quote, ShaderSyntaxKind kind)
    {
        int start = _position;
        _position++;

        while (_position < _text.Length && !IsLineBreakAt(_position))
        {
            if (_text[_position] == '\\')
            {
                // 行末のバックスラッシュは行の継続である。文字列は次の行へ続く
                // (#define ROOT_SIG "CBV(b0), \ の形。ルートシグネチャをマクロに書くときに使う)。
                if (IsLineBreakAt(_position + 1))
                {
                    _position += _text[_position + 1] == '\r' && _position + 2 < _text.Length && _text[_position + 2] == '\n' ? 3 : 2;
                    continue;
                }

                _position = Math.Min(_position + 2, _text.Length);
                continue;
            }

            if (_text[_position] == quote)
            {
                _position++;
                return (kind, _text.ToString(TextSpan.FromBounds(start + 1, _position - 1)));
            }

            _position++;
        }

        if (!_inMessageDirective)
        {
            _diagnostics.Add(new LexerDiagnostic(
                TextSpan.FromBounds(start, _position),
                $"リテラルが '{quote}' で閉じられていません。"));
        }

        return (kind, _text.ToString(TextSpan.FromBounds(Math.Min(start + 1, _position), _position)));
    }

    /// <summary>MSVC 流の、無限大と非数を表す浮動小数の書き方 (<c>1.#INF</c>)。長いものを先に並べる。</summary>
    private static readonly string[] SpecialFloatSuffixes = ["#QNAN", "#SNAN", "#INF", "#IND"];

    /// <summary>
    /// 数値リテラルを読み進める。
    /// </summary>
    /// <returns>トークンの種別。</returns>
    /// <remarks>
    /// <para>
    /// 10 進数・16 進数 (<c>0x</c>)・2 進数 (<c>0b</c>、DXC)・浮動小数・指数表記・型接尾辞に対応する。
    /// HLSL の接尾辞は <c>f</c> (float)、<c>h</c> (half)、<c>u</c> (uint)、<c>l</c> (long) など。
    /// </para>
    /// <para>
    /// 値の妥当性 (範囲や桁数) は検査しない。
    /// リテラルとして読めることだけを保証し、意味の検査は後段に委ねる。
    /// </para>
    /// </remarks>
    private ShaderSyntaxKind LexNumericLiteral()
    {
        // 16 進数
        if (_text[_position] == '0' && (Peek(1) is 'x' or 'X'))
        {
            _position += 2;
            while (_position < _text.Length && Uri.IsHexDigit(_text[_position]))
            {
                _position++;
            }

            ConsumeNumericSuffix();
            return ShaderSyntaxKind.NumericLiteralToken;
        }

        // 2 進数。DXC は受け付ける (Unreal Engine の Nanite のシェーダーが 0b1111 のように書く)。
        // 読まずにいると 0 と b1111 に分かれ、構文エラーになっていた。
        if (_text[_position] == '0' && (Peek(1) is 'b' or 'B') && (Peek(2) is '0' or '1'))
        {
            _position += 2;
            while (_position < _text.Length && _text[_position] is '0' or '1')
            {
                _position++;
            }

            ConsumeNumericSuffix();
            return ShaderSyntaxKind.NumericLiteralToken;
        }

        while (_position < _text.Length && char.IsDigit(_text[_position]))
        {
            _position++;
        }

        if (_position < _text.Length && _text[_position] == '.')
        {
            _position++;
            while (_position < _text.Length && char.IsDigit(_text[_position]))
            {
                _position++;
            }

            // MSVC 流の無限大と非数 (1.#INF / 1.#IND / 1.#QNAN)。FXC と DXC が受け付ける。
            foreach (string special in SpecialFloatSuffixes)
            {
                if (string.CompareOrdinal(_text.ToString(new TextSpan(_position, Math.Min(special.Length, _text.Length - _position))), special) == 0)
                {
                    _position += special.Length;
                    return ShaderSyntaxKind.NumericLiteralToken;
                }
            }
        }

        if (_position < _text.Length && _text[_position] is 'e' or 'E')
        {
            int savedPosition = _position;
            _position++;

            if (_position < _text.Length && _text[_position] is '+' or '-')
            {
                _position++;
            }

            if (_position < _text.Length && char.IsDigit(_text[_position]))
            {
                while (_position < _text.Length && char.IsDigit(_text[_position]))
                {
                    _position++;
                }
            }
            else
            {
                // 'e' の後ろが数字でなければ指数表記ではない。位置を戻す。
                _position = savedPosition;
            }
        }

        ConsumeNumericSuffix();
        return ShaderSyntaxKind.NumericLiteralToken;
    }

    /// <summary>数値リテラルの型接尾辞を読み飛ばす。</summary>
    private void ConsumeNumericSuffix()
    {
        while (_position < _text.Length && _text[_position] is 'f' or 'F' or 'h' or 'H' or 'u' or 'U' or 'l' or 'L')
        {
            _position++;
        }
    }

    /// <summary>改行を 1 つ読み進める。CRLF は 1 つの改行として扱う。</summary>
    private void ConsumeLineBreak()
    {
        if (_position < _text.Length && _text[_position] == '\r' && Peek(1) == '\n')
        {
            _position += 2;
            return;
        }

        if (_position < _text.Length)
        {
            _position++;
        }
    }

    /// <summary>指定位置から水平方向の空白を読み飛ばした位置を返す。</summary>
    /// <param name="from">開始位置。</param>
    /// <returns>空白でない最初の位置。</returns>
    private int SkipHorizontalWhitespaceFrom(int from)
    {
        int index = from;
        while (index < _text.Length && char.IsWhiteSpace(_text[index]) && !IsLineBreakAt(index))
        {
            index++;
        }

        return index;
    }

    /// <summary>指定位置が改行かどうかを判定する。</summary>
    /// <param name="index">判定する位置。</param>
    /// <returns>改行の場合は <see langword="true"/>。</returns>
    private bool IsLineBreakAt(int index)
        => index < _text.Length && _text[index] is '\r' or '\n';

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
