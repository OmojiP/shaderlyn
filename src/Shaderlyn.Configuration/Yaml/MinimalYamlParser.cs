using System.Collections.Immutable;
using System.Text;

namespace Shaderlyn.Configuration.Yaml;

/// <summary>
/// YAML の読み取りで見つかった誤り。
/// </summary>
/// <param name="Line">誤りのある 0 始まりの行番号。</param>
/// <param name="Message">誤りの内容。</param>
internal readonly record struct YamlError(int Line, string Message);

/// <summary>
/// 設定ファイルに必要な範囲だけを扱う YAML のパーサー。
/// </summary>
/// <remarks>
/// <para>
/// <b>YAML の全仕様は実装しない。</b>
/// 設定ファイルの表現に必要なのは、写像・並び・スカラーと、
/// それらの入れ子だけである。アンカー、別名、複数ドキュメント、
/// 複数行スカラー、明示的な型指定は扱わない。
/// </para>
/// <para>
/// 外部のライブラリを使わないのは Native AOT のためである。
/// 一般的な YAML ライブラリはリフレクションで型に対応付ける設計になっており、
/// AOT ではトリミング警告 (このリポジトリではビルドエラー) になるか、
/// 追加のコード生成器を要求する。
/// </para>
/// <para>
/// <b>扱えない書き方は、何も伝えずに無視するのではなく誤りとして報告する。</b>
/// 設定ファイルが読めていないことに気づかないまま
/// 「ルールを設定したつもり」で運用されるのが最悪の結果である。
/// </para>
/// </remarks>
internal static class MinimalYamlParser
{
    /// <summary>
    /// YAML のテキストを読み取る。
    /// </summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="errors">見つかった誤り。</param>
    /// <returns>
    /// 最上位の要素。内容が空の場合や読み取れなかった場合は <see langword="null"/>。
    /// </returns>
    /// <remarks>
    /// <b>このメソッドは入力がどれだけ壊れていても例外を投げない。</b>
    /// 解釈できない行は誤りとして報告し、可能な範囲を読み取って返す。
    /// </remarks>
    public static YamlNode? Parse(string text, out ImmutableArray<YamlError> errors)
    {
        ArgumentNullException.ThrowIfNull(text);

        Parser parser = new(text);
        YamlNode? root = parser.ParseDocument();
        errors = parser.Errors;
        return root;
    }

    /// <summary>
    /// 意味のある 1 行。
    /// </summary>
    /// <param name="Number">0 始まりの行番号。</param>
    private sealed record Line(int Number)
    {
        /// <summary>行頭の空白の数。入れ子の深さを表す。</summary>
        public required int Indent { get; set; }

        /// <summary>行頭の空白とコメントを除いた内容。</summary>
        public required string Text { get; set; }
    }

    private sealed class Parser
    {
        private readonly List<Line> _lines = [];
        private readonly ImmutableArray<YamlError>.Builder _errors = ImmutableArray.CreateBuilder<YamlError>();
        private int _index;

        public Parser(string text) => ReadLines(text);

        public ImmutableArray<YamlError> Errors => _errors.ToImmutable();

        /// <summary>
        /// テキストを行へ分解し、空行とコメント行を取り除く。
        /// </summary>
        /// <param name="text">対象のテキスト。</param>
        private void ReadLines(string text)
        {
            string[] rawLines = text.Split('\n');

            for (int number = 0; number < rawLines.Length; number++)
            {
                string raw = rawLines[number].TrimEnd('\r');

                int indent = 0;
                while (indent < raw.Length && raw[indent] == ' ')
                {
                    indent++;
                }

                // タブによる字下げは YAML では認められていない。
                // 何も伝えずに読み替えると入れ子の深さが意図とずれ、
                // 「書いたのに効かない設定」という形で現れる。
                if (indent < raw.Length && raw[indent] == '\t')
                {
                    _errors.Add(new YamlError(number, "字下げにタブを使うことはできません。空白を使ってください。"));
                    continue;
                }

                string content = StripComment(raw[indent..]).TrimEnd();

                if (content.Length == 0)
                {
                    continue;
                }

                // ドキュメントの開始を表す --- は読み飛ばす。単一ドキュメントしか扱わない。
                if (content == "---")
                {
                    continue;
                }

                _lines.Add(new Line(number) { Indent = indent, Text = content });
            }
        }

        /// <summary>
        /// 行末のコメントを取り除く。
        /// </summary>
        /// <param name="content">行の内容 (字下げを除く)。</param>
        /// <returns>コメントを除いた内容。</returns>
        /// <remarks>
        /// 引用符の中の <c>#</c> はコメントの開始とみなさない。
        /// メッセージ文字列に <c>#</c> を書けなくなるためである。
        /// </remarks>
        private static string StripComment(string content)
        {
            char quote = '\0';

            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];

                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c is '"' or '\'')
                {
                    quote = c;
                    continue;
                }

                // コメントの開始は、行頭か空白の直後にある # である。
                // 値の途中の # (色の指定など) をコメントにしないための規則。
                if (c == '#' && (i == 0 || content[i - 1] == ' '))
                {
                    return content[..i];
                }
            }

            return content;
        }

        private Line? Current => _index < _lines.Count ? _lines[_index] : null;

        /// <summary>ドキュメント全体を読み取る。</summary>
        /// <returns>最上位の要素。内容が無い場合は <see langword="null"/>。</returns>
        public YamlNode? ParseDocument()
        {
            if (Current is null)
            {
                return null;
            }

            YamlNode root = ParseBlock(Current.Indent);

            if (Current is { } leftover)
            {
                _errors.Add(new YamlError(leftover.Number, "字下げの深さが揃っていません。"));
            }

            return root;
        }

        /// <summary>
        /// 指定した深さのブロックを読み取る。
        /// </summary>
        /// <param name="indent">読み取る深さ。</param>
        /// <returns>読み取った要素。</returns>
        private YamlNode ParseBlock(int indent)
            => Current is { } line && line.Text.StartsWith('-') && IsSequenceEntry(line.Text)
                ? ParseSequence(indent)
                : ParseMapping(indent);

        /// <summary>
        /// 行の内容が並びの要素かどうかを判定する。
        /// </summary>
        /// <param name="text">行の内容。</param>
        /// <returns>並びの要素であれば <see langword="true"/>。</returns>
        /// <remarks>
        /// <c>-</c> 単独、または <c>-</c> の直後が空白であるものを並びの要素とみなす。
        /// <c>-5</c> のような負の数をスカラーとして扱えるようにするための区別である。
        /// </remarks>
        private static bool IsSequenceEntry(string text)
            => text == "-" || (text.Length > 1 && text[0] == '-' && text[1] == ' ');

        /// <summary>写像を読み取る。</summary>
        /// <param name="indent">読み取る深さ。</param>
        /// <returns>読み取った写像。</returns>
        private YamlMapping ParseMapping(int indent)
        {
            int startLine = Current?.Number ?? 0;
            ImmutableDictionary<string, YamlNode>.Builder entries =
                ImmutableDictionary.CreateBuilder<string, YamlNode>(StringComparer.Ordinal);
            ImmutableArray<string>.Builder keys = ImmutableArray.CreateBuilder<string>();

            while (Current is { } line && line.Indent == indent && !IsSequenceEntry(line.Text))
            {
                int separator = FindKeySeparator(line.Text);

                if (separator < 0)
                {
                    _errors.Add(new YamlError(line.Number, $"'{line.Text}' はキーと値の対になっていません。"));
                    _index++;
                    continue;
                }

                string key = UnquoteScalar(line.Text[..separator].TrimEnd());
                string rest = line.Text[(separator + 1)..].Trim();
                int keyLine = line.Number;
                _index++;

                YamlNode value = rest.Length > 0
                    ? ParseInlineValue(rest, keyLine)
                    : ParseNestedBlock(indent, keyLine);

                if (entries.ContainsKey(key))
                {
                    // 同じキーを 2 度書いた場合、どちらが効くかは実装依存になる。
                    // 何も伝えずに一方を採用すると、設定したつもりの値が効かない。
                    _errors.Add(new YamlError(keyLine, $"キー '{key}' が重複しています。"));
                    continue;
                }

                entries.Add(key, value);
                keys.Add(key);
            }

            return new YamlMapping
            {
                Line = startLine,
                Entries = entries.ToImmutable(),
                Keys = keys.ToImmutable(),
            };
        }

        /// <summary>並びを読み取る。</summary>
        /// <param name="indent">読み取る深さ。</param>
        /// <returns>読み取った並び。</returns>
        private YamlSequence ParseSequence(int indent)
        {
            int startLine = Current?.Number ?? 0;
            ImmutableArray<YamlNode>.Builder items = ImmutableArray.CreateBuilder<YamlNode>();

            while (Current is { } line && line.Indent == indent && IsSequenceEntry(line.Text))
            {
                int lineNumber = line.Number;
                string rest = line.Text.Length > 1 ? line.Text[1..].TrimStart() : string.Empty;

                if (rest.Length == 0)
                {
                    _index++;
                    items.Add(ParseNestedBlock(indent, lineNumber));
                    continue;
                }

                // 「- key: value」の形。要素は写像であり、
                // 続きのキーは値が始まる桁に揃えて書かれる。
                // その桁を深さとみなして、この行を写像の 1 行目として読み直す。
                int spacesAfterDash = line.Text.Length - 1 - rest.Length;
                int contentColumn = indent + 1 + spacesAfterDash;

                if (FindKeySeparator(rest) >= 0)
                {
                    line.Indent = contentColumn;
                    line.Text = rest;
                    items.Add(ParseMapping(contentColumn));
                    continue;
                }

                _index++;
                items.Add(ParseInlineValue(rest, lineNumber));
            }

            return new YamlSequence { Line = startLine, Items = items.ToImmutable() };
        }

        /// <summary>
        /// より深い字下げのブロックを読み取る。
        /// </summary>
        /// <param name="parentIndent">親の深さ。</param>
        /// <param name="parentLine">親の行番号。</param>
        /// <returns>
        /// 読み取った要素。より深い行が無い場合は空のスカラー。
        /// </returns>
        private YamlNode ParseNestedBlock(int parentIndent, int parentLine)
        {
            if (Current is not { } line || line.Indent <= parentIndent)
            {
                return new YamlScalar { Line = parentLine, Value = string.Empty };
            }

            return ParseBlock(line.Indent);
        }

        /// <summary>
        /// キーと値を区切るコロンの位置を探す。
        /// </summary>
        /// <param name="text">行の内容。</param>
        /// <returns>コロンの位置。見つからない場合は -1。</returns>
        /// <remarks>
        /// 引用符と流れ形式の括弧の中にあるコロンは区切りとみなさない。
        /// 値が <c>{ a: 1 }</c> や <c>"http://..."</c> の場合に誤って切らないための規則である。
        /// </remarks>
        private static int FindKeySeparator(string text)
        {
            char quote = '\0';
            int depth = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                switch (c)
                {
                    case '"' or '\'':
                        quote = c;
                        break;

                    case '[' or '{':
                        depth++;
                        break;

                    case ']' or '}':
                        depth--;
                        break;

                    case ':' when depth == 0 && (i + 1 == text.Length || text[i + 1] == ' '):
                        return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 行に直接書かれた値を読み取る。
        /// </summary>
        /// <param name="text">値の文字列。</param>
        /// <param name="lineNumber">行番号。</param>
        /// <returns>読み取った要素。</returns>
        private YamlNode ParseInlineValue(string text, int lineNumber)
        {
            if (text.StartsWith('[') || text.StartsWith('{'))
            {
                FlowParser flow = new(text, lineNumber, _errors);
                return flow.Parse();
            }

            return new YamlScalar { Line = lineNumber, Value = UnquoteScalar(text) };
        }
    }

    /// <summary>
    /// 流れ形式 (<c>[a, b]</c> や <c>{ k: v }</c>) を読み取る。
    /// </summary>
    /// <param name="text">対象の文字列。</param>
    /// <param name="lineNumber">行番号。</param>
    /// <param name="errors">誤りの報告先。</param>
    private sealed class FlowParser(string text, int lineNumber, ImmutableArray<YamlError>.Builder errors)
    {
        private int _position;

        /// <summary>流れ形式の値を読み取る。</summary>
        /// <returns>読み取った要素。</returns>
        public YamlNode Parse()
        {
            YamlNode node = ParseValue();
            SkipWhitespace();

            if (_position < text.Length)
            {
                errors.Add(new YamlError(lineNumber, $"'{text[_position..]}' を解釈できません。"));
            }

            return node;
        }

        private YamlNode ParseValue()
        {
            SkipWhitespace();

            if (_position >= text.Length)
            {
                return new YamlScalar { Line = lineNumber, Value = string.Empty };
            }

            return text[_position] switch
            {
                '[' => ParseSequence(),
                '{' => ParseMapping(),
                _ => ParseScalar(),
            };
        }

        private YamlSequence ParseSequence()
        {
            _position++;
            ImmutableArray<YamlNode>.Builder items = ImmutableArray.CreateBuilder<YamlNode>();

            while (true)
            {
                SkipWhitespace();

                if (_position >= text.Length)
                {
                    errors.Add(new YamlError(lineNumber, "']' が閉じられていません。"));
                    break;
                }

                if (text[_position] == ']')
                {
                    _position++;
                    break;
                }

                items.Add(ParseValue());
                SkipWhitespace();

                if (_position < text.Length && text[_position] == ',')
                {
                    _position++;
                }
            }

            return new YamlSequence { Line = lineNumber, Items = items.ToImmutable() };
        }

        private YamlMapping ParseMapping()
        {
            _position++;
            ImmutableDictionary<string, YamlNode>.Builder entries =
                ImmutableDictionary.CreateBuilder<string, YamlNode>(StringComparer.Ordinal);
            ImmutableArray<string>.Builder keys = ImmutableArray.CreateBuilder<string>();

            while (true)
            {
                SkipWhitespace();

                if (_position >= text.Length)
                {
                    errors.Add(new YamlError(lineNumber, "'}' が閉じられていません。"));
                    break;
                }

                if (text[_position] == '}')
                {
                    _position++;
                    break;
                }

                string key = ReadScalarText();
                SkipWhitespace();

                if (_position < text.Length && text[_position] == ':')
                {
                    _position++;
                }
                else
                {
                    errors.Add(new YamlError(lineNumber, $"キー '{key}' に ':' がありません。"));
                }

                YamlNode value = ParseValue();

                if (!entries.ContainsKey(key))
                {
                    entries.Add(key, value);
                    keys.Add(key);
                }
                else
                {
                    errors.Add(new YamlError(lineNumber, $"キー '{key}' が重複しています。"));
                }

                SkipWhitespace();

                if (_position < text.Length && text[_position] == ',')
                {
                    _position++;
                }
            }

            return new YamlMapping
            {
                Line = lineNumber,
                Entries = entries.ToImmutable(),
                Keys = keys.ToImmutable(),
            };
        }

        private YamlScalar ParseScalar()
            => new() { Line = lineNumber, Value = ReadScalarText() };

        /// <summary>
        /// 流れ形式の中のスカラーを読み取る。
        /// </summary>
        /// <returns>引用符を外した値。</returns>
        private string ReadScalarText()
        {
            SkipWhitespace();

            if (_position < text.Length && text[_position] is '"' or '\'')
            {
                return ReadQuoted();
            }

            int start = _position;

            // 流れ形式では、区切り文字が値の終わりになる。
            while (_position < text.Length && text[_position] is not (',' or ']' or '}' or ':'))
            {
                _position++;
            }

            return text[start.._position].Trim();
        }

        private string ReadQuoted()
        {
            char quote = text[_position++];
            StringBuilder builder = new();

            while (_position < text.Length)
            {
                char c = text[_position++];

                if (c == quote)
                {
                    // 単引用符の中では '' が引用符 1 つを表す。
                    if (quote == '\'' && _position < text.Length && text[_position] == '\'')
                    {
                        builder.Append('\'');
                        _position++;
                        continue;
                    }

                    return builder.ToString();
                }

                if (c == '\\' && quote == '"' && _position < text.Length)
                {
                    builder.Append(UnescapeCharacter(text[_position++]));
                    continue;
                }

                builder.Append(c);
            }

            errors.Add(new YamlError(lineNumber, "引用符が閉じられていません。"));
            return builder.ToString();
        }

        private void SkipWhitespace()
        {
            while (_position < text.Length && text[_position] == ' ')
            {
                _position++;
            }
        }
    }

    /// <summary>
    /// スカラーの引用符を外す。
    /// </summary>
    /// <param name="text">対象の文字列。</param>
    /// <returns>引用符を外した値。引用符で囲まれていない場合はそのまま。</returns>
    private static string UnquoteScalar(string text)
    {
        if (text.Length < 2)
        {
            return text;
        }

        char first = text[0];

        if (first is not ('"' or '\'') || text[^1] != first)
        {
            return text;
        }

        string inner = text[1..^1];

        if (first == '\'')
        {
            return inner.Replace("''", "'", StringComparison.Ordinal);
        }

        StringBuilder builder = new(inner.Length);

        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                builder.Append(UnescapeCharacter(inner[++i]));
                continue;
            }

            builder.Append(inner[i]);
        }

        return builder.ToString();
    }

    /// <summary>二重引用符の中のエスケープを解決する。</summary>
    /// <param name="escaped">バックスラッシュに続く文字。</param>
    /// <returns>対応する文字。</returns>
    private static char UnescapeCharacter(char escaped) => escaped switch
    {
        'n' => '\n',
        't' => '\t',
        'r' => '\r',
        '0' => '\0',
        _ => escaped,
    };
}
