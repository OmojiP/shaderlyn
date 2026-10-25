using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.Core.Syntax;

namespace Shaderlyn.Semantics.Analysis;

/// <summary>
/// ノードに対するアクションを、どのノードで呼ぶかの指定 (<see cref="ShaderAnalysisContext.RegisterNodeAction{TNode}(Action{ShaderNodeAnalysisContext{TNode}}, NodeActionOptions)"/>)。
/// </summary>
/// <remarks>
/// 何も指定しなければ、今までどおりすべてのノードで呼ぶ。
/// </remarks>
public sealed class NodeActionOptions
{
    /// <summary>
    /// エントリポイントから呼び出しをたどって届く関数の中のノードだけで呼ぶ。
    /// </summary>
    /// <remarks>
    /// 関数の外のノード (グローバルの宣言など) では、今までどおり呼ぶ。
    /// エントリポイントが分からない木 (Unity の <c>HLSLINCLUDE</c> だけのブロックなど) では、すべての関数の中で呼ぶ。
    /// 届くかは <see cref="ShaderCompilation.EnumerateCallees"/> でたどる。
    /// </remarks>
    public bool ReachableFromEntryPoints { get; init; }

    /// <summary>
    /// この構成で存在しうるノードだけで呼ぶ。その中の <see cref="ShaderNodeAnalysisContext{TNode}.ReportWhen(SymbolCondition, Core.Diagnostics.DiagnosticDescriptor, object?[])"/> には、この条件も掛かる。
    /// </summary>
    /// <remarks>
    /// <see cref="SymbolCondition"/> のほか、<c>#if</c> と同じ書き方の文字列を渡せる。
    /// <code>
    /// When = SymbolCondition.Symbol("SHADER_API_MOBILE").And(SymbolCondition.Symbol("_A", isDefined: false))
    /// When = "defined(SHADER_API_MOBILE) &amp;&amp; !defined(_A) &amp;&amp; QUALITY >= 2"
    /// </code>
    /// 値を持つシンボル (<c>QUALITY</c>) は、その単位の値の一覧で読む (<c>QUALITY >= 2</c> は <c>QUALITY == 2 || QUALITY == 3</c>)。
    /// </remarks>
    public NodeCondition? When { get; init; }
}

/// <summary>
/// ノードを選ぶ構成の条件。<see cref="SymbolCondition"/> か、<c>#if</c> と同じ書き方の文字列から作る。
/// </summary>
/// <remarks>
/// <para>
/// 文字列で書けるのは、<c>defined(X)</c> / <c>defined X</c> / <c>X</c> (値を持つシンボルなら <c>X != 0</c>)、
/// 値を持つシンボルと整数の比較 (<c>==</c> <c>!=</c> <c>&lt;</c> <c>&lt;=</c> <c>&gt;</c> <c>&gt;=</c>)、<c>!</c> <c>&amp;&amp;</c> <c>||</c> と括弧である。
/// </para>
/// <para>
/// 書き方の誤りは、作るとき (ルールを登録するとき) に <see cref="FormatException"/> にする。
/// 黙って「どの構成でも」として扱うと、ルールが全体に効いてしまう。
/// 値を持たないシンボルを数と比べた場合は、その単位を解析するときに <see cref="FormatException"/> にする (値の一覧はそこで分かる)。
/// </para>
/// </remarks>
public sealed class NodeCondition
{
    private readonly SymbolCondition? _condition;
    private readonly Expression? _expression;

    private NodeCondition(SymbolCondition? condition, Expression? expression, string text)
    {
        _condition = condition;
        _expression = expression;
        Text = text;
    }

    /// <summary>書いたときの形。</summary>
    public string Text { get; }

    /// <summary><see cref="SymbolCondition"/> から作る。</summary>
    /// <param name="condition">条件。</param>
    public static implicit operator NodeCondition(SymbolCondition condition) => FromCondition(condition);

    /// <summary><c>#if</c> と同じ書き方の文字列から作る。</summary>
    /// <param name="text">条件式。</param>
    public static implicit operator NodeCondition(string text) => Parse(text);

    /// <summary><see cref="SymbolCondition"/> から作る。</summary>
    /// <param name="condition">条件。</param>
    /// <returns>作った条件。</returns>
    public static NodeCondition FromCondition(SymbolCondition condition) => new(condition, null, condition.ToString());

    /// <summary><c>#if</c> と同じ書き方の文字列から作る。</summary>
    /// <param name="text">条件式。</param>
    /// <returns>作った条件。</returns>
    /// <exception cref="FormatException">書き方が誤っている。</exception>
    public static NodeCondition Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Parser parser = new(text);
        Expression expression = parser.ParseOr();
        parser.ExpectEnd();
        return new NodeCondition(null, expression, text);
    }

    /// <summary>値を持つシンボルの値の一覧で、<see cref="SymbolCondition"/> にする。</summary>
    /// <param name="values">値を持つシンボルと、その値 (<see cref="VariantSpace.ValueSymbols"/>)。</param>
    /// <returns>条件。</returns>
    /// <exception cref="FormatException">値を持たないシンボルを数と比べている。</exception>
    public SymbolCondition Resolve(ImmutableDictionary<string, ImmutableArray<int>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return _condition ?? _expression!.Resolve(values, Text);
    }

    /// <inheritdoc/>
    public override string ToString() => Text;

    /// <summary>読んだ式。</summary>
    private abstract record Expression
    {
        public abstract SymbolCondition Resolve(ImmutableDictionary<string, ImmutableArray<int>> values, string text);
    }

    private sealed record Not(Expression Operand) : Expression
    {
        public override SymbolCondition Resolve(ImmutableDictionary<string, ImmutableArray<int>> values, string text)
            => Operand.Resolve(values, text).Negate();
    }

    private sealed record Binary(Expression Left, Expression Right, bool IsAnd) : Expression
    {
        public override SymbolCondition Resolve(ImmutableDictionary<string, ImmutableArray<int>> values, string text)
            => IsAnd
                ? Left.Resolve(values, text).And(Right.Resolve(values, text))
                : Left.Resolve(values, text).Or(Right.Resolve(values, text));
    }

    /// <summary><c>defined(X)</c>。値を持つシンボルなら、どれかの値のとき (宣言した値のどれかは必ず定義される)。</summary>
    private sealed record Defined(string Name) : Expression
    {
        public override SymbolCondition Resolve(ImmutableDictionary<string, ImmutableArray<int>> values, string text)
            => values.ContainsKey(Name) ? SymbolCondition.Always : SymbolCondition.Symbol(Name);
    }

    /// <summary><c>X</c> だけ。値を持つシンボルなら <c>X != 0</c>、そうでなければ定義されているか。</summary>
    private sealed record Name(string Symbol) : Expression
    {
        public override SymbolCondition Resolve(ImmutableDictionary<string, ImmutableArray<int>> values, string text)
            => values.ContainsKey(Symbol)
                ? new Comparison(Symbol, "!=", 0).Resolve(values, text)
                : SymbolCondition.Symbol(Symbol);
    }

    private sealed record Comparison(string Symbol, string Operator, int Value) : Expression
    {
        public override SymbolCondition Resolve(ImmutableDictionary<string, ImmutableArray<int>> values, string text)
        {
            if (!values.TryGetValue(Symbol, out ImmutableArray<int> candidates))
            {
                throw new FormatException($"'{text}' の '{Symbol}' は値を持つシンボルではないので、数と比べられません。");
            }

            SymbolCondition result = SymbolCondition.Never;

            foreach (int candidate in candidates)
            {
                bool matches = Operator switch
                {
                    "==" => candidate == Value,
                    "!=" => candidate != Value,
                    "<" => candidate < Value,
                    "<=" => candidate <= Value,
                    ">" => candidate > Value,
                    _ => candidate >= Value,
                };

                if (matches)
                {
                    result = result.Or(SymbolCondition.Symbol(VariantValues.Atom(Symbol, candidate)));
                }
            }

            return result;
        }
    }

    /// <summary>条件式の読み手。</summary>
    private sealed class Parser(string text)
    {
        private int _position;

        public Expression ParseOr()
        {
            Expression left = ParseAnd();

            while (Accept("||"))
            {
                left = new Binary(left, ParseAnd(), IsAnd: false);
            }

            return left;
        }

        public void ExpectEnd()
        {
            SkipSpaces();

            if (_position < text.Length)
            {
                throw Error($"{_position + 1} 文字目の '{text[_position..]}' を読めません");
            }
        }

        private Expression ParseAnd()
        {
            Expression left = ParseUnary();

            while (Accept("&&"))
            {
                left = new Binary(left, ParseUnary(), IsAnd: true);
            }

            return left;
        }

        private Expression ParseUnary()
        {
            if (Accept("!") )
            {
                return new Not(ParseUnary());
            }

            if (Accept("("))
            {
                Expression inner = ParseOr();
                Expect(")");
                return inner;
            }

            string name = ReadName();

            if (name == "defined")
            {
                bool parenthesized = Accept("(");
                string symbol = ReadName();

                if (parenthesized)
                {
                    Expect(")");
                }

                return new Defined(symbol);
            }

            foreach (string op in (string[])["==", "!=", "<=", ">=", "<", ">"])
            {
                if (Accept(op))
                {
                    return new Comparison(name, op, ReadInteger());
                }
            }

            return new Name(name);
        }

        private string ReadName()
        {
            SkipSpaces();
            int start = _position;

            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
            {
                _position++;
            }

            if (start == _position || char.IsAsciiDigit(text[start]))
            {
                throw Error($"{start + 1} 文字目にシンボルの名前がありません");
            }

            return text[start.._position];
        }

        private int ReadInteger()
        {
            SkipSpaces();
            int start = _position;

            if (_position < text.Length && text[_position] == '-')
            {
                _position++;
            }

            while (_position < text.Length && char.IsAsciiDigit(text[_position]))
            {
                _position++;
            }

            return int.TryParse(text.AsSpan(start, _position - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
                ? value
                : throw Error($"{start + 1} 文字目に整数がありません");
        }

        private bool Accept(string token)
        {
            SkipSpaces();

            // "!" が "!=" の頭を取らないようにする (比較は名前の後でしか読まない)。
            if (string.CompareOrdinal(text, _position, token, 0, token.Length) == 0
                && !(token == "!" && _position + 1 < text.Length && text[_position + 1] == '='))
            {
                _position += token.Length;
                return true;
            }

            return false;
        }

        private void Expect(string token)
        {
            if (!Accept(token))
            {
                throw Error($"{_position + 1} 文字目に '{token}' が要ります");
            }
        }

        private void SkipSpaces()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }

        private FormatException Error(string reason) => new($"条件 '{text}' を読めません: {reason}。");
    }
}
