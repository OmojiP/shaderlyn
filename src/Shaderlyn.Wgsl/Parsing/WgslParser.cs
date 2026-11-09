using System.Collections.Immutable;
using Shaderlyn.CFamily;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Wgsl.Parsing;

/// <summary>
/// WGSL のトークン列を、C 系の言語と共通の構文木 (<see cref="CompilationUnitSyntax"/>) に読む。
/// </summary>
/// <remarks>
/// <para>
/// <b>木の形は C 系の言語にそろえる。</b>意味解析とルールを、HLSL・GLSL・Slang と同じものにするためである。
/// WGSL の書き方は、次のように C 系のノードにする。
/// </para>
/// <list type="bullet">
/// <item><c>var</c> / <c>let</c> / <c>const</c> / <c>override</c> は変数の宣言。キーワード (と <c>var&lt;storage, read&gt;</c> の山括弧の中) と属性のトークンは修飾子にする。
/// 型を書かなければ、型の名前をキーワードのトークンにする (Slang の <c>let</c> と同じ。型は意味解析が初期値から求める)。</item>
/// <item><c>fn</c> は関数の宣言。<c>-&gt;</c> が無ければ <c>void</c> を返す。戻り値の属性 (<c>-&gt; @location(0)</c>) は、関数の後ろの修飾 (セマンティクス) にする。</item>
/// <item>構造体のメンバーは、1 つずつ変数の宣言にする (区切りの <c>,</c> を終端のトークンにする)。</item>
/// <item><c>if</c> / <c>while</c> / <c>switch</c> の条件は、括弧を書かないので、欠けた括弧を補う。</item>
/// <item><c>loop { … continuing { … } }</c> は、条件が <c>true</c> の <c>while</c> にする。<c>continuing</c> の中身は、本体の最後のブロックにする。</item>
/// <item><c>break if c;</c> は、<c>if (c) break;</c> にする。</item>
/// <item><c>case 1, 2:</c> は、選ぶ値ごとの <c>case</c> の札にして、後ろにブロックを置く。</item>
/// </list>
/// <para>
/// <b>テンプレートの山括弧は、WGSL の仕様の手順で見つける</b> (template list discovery)。
/// <c>a &lt; b</c> と <c>vec3&lt;f32&gt;</c> は、名前を知らなくても、後ろのトークンから見分けられる。
/// <c>array&lt;vec3&lt;f32&gt;&gt;</c> の <c>&gt;&gt;</c> は、2 つの <c>&gt;</c> に分ける。
/// </para>
/// </remarks>
internal sealed class WgslParser
{
    private const int MaxReportedErrors = 100;

    private readonly ImmutableArray<ShaderSyntaxToken> _tokens;
    private readonly Dictionary<int, int> _templateEnds;
    private readonly Dictionary<string, string> _aliases;
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
    private readonly List<ShaderSyntaxToken> _unanalyzed = [];
    private int _index;
    private int _reportedErrors;
    private ShaderSyntaxToken? _lastErrorToken;

    /// <summary>解析ツールを作る。</summary>
    /// <param name="tokens">プリプロセス後のトークン列。</param>
    public WgslParser(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        List<ShaderSyntaxToken> list = MergeIntegerSuffixes(tokens.IsDefault ? [] : tokens);

        if (list.Count == 0 || list[^1].Kind != ShaderSyntaxKind.EndOfFileToken)
        {
            ShaderSyntaxToken? last = list.Count > 0 ? list[^1] : null;
            list.Add(last is not null
                ? new ShaderSyntaxToken(ShaderSyntaxKind.EndOfFileToken, last.Source, new TextSpan(last.Span.End, 0), string.Empty)
                : new ShaderSyntaxToken(ShaderSyntaxKind.EndOfFileToken, SourceText.From(string.Empty, "<空>"), new TextSpan(0, 0), string.Empty));
        }

        _templateEnds = DiscoverTemplates(list);
        _tokens = [.. list];
        _aliases = ScanAliases();
    }

    /// <summary>構文木にせずに読み飛ばした所に現れた識別子。</summary>
    public ImmutableArray<ShaderSyntaxToken> UnanalyzedIdentifiers => [.. _unanalyzed];

    // --------------------------------------------------------------------
    // トークンの下ごしらえ
    // --------------------------------------------------------------------

    /// <summary>
    /// 整数の接尾辞 <c>i</c> (<c>1i</c>) を数のトークンに含める。C 系の字句解析は <c>i</c> を接尾辞として読まない。
    /// </summary>
    private static List<ShaderSyntaxToken> MergeIntegerSuffixes(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        List<ShaderSyntaxToken> list = new(tokens.Length + 1);

        for (int i = 0; i < tokens.Length; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind == ShaderSyntaxKind.NumericLiteralToken
                && i + 1 < tokens.Length
                && tokens[i + 1] is { Kind: ShaderSyntaxKind.IdentifierToken, Text: "i", FollowsWithoutSpace: true } suffix
                && ReferenceEquals(suffix.Source, token.Source)
                && suffix.Span.Start == token.Span.End)
            {
                list.Add(new ShaderSyntaxToken(
                    ShaderSyntaxKind.NumericLiteralToken, token.Source, new TextSpan(token.Span.Start, suffix.Span.End - token.Span.Start),
                    token.Text + "i", token.IsAtLineStart, token.LeadingTrivia, suffix.TrailingTrivia)
                {
                    FollowsWithoutSpace = token.FollowsWithoutSpace,
                });
                i++;
                continue;
            }

            list.Add(token);
        }

        return list;
    }

    /// <summary>
    /// テンプレートの山括弧を見つける (WGSL の仕様の template list discovery)。
    /// </summary>
    /// <returns>開きの <c>&lt;</c> の位置から、閉じの <c>&gt;</c> の位置への対応。</returns>
    /// <remarks>
    /// 名前の後ろの <c>&lt;</c> を候補にし、同じ括弧の深さで <c>&gt;</c> が来れば山括弧とする。
    /// <c>;</c> ・ <c>{</c> ・ <c>:</c> ・代入の <c>=</c> は候補をすべて捨て、<c>&amp;&amp;</c> と <c>||</c> は今の深さの候補を捨てる。
    /// 閉じの <c>&gt;</c> が <c>&gt;&gt;</c> ・ <c>&gt;=</c> ・ <c>&gt;&gt;=</c> の頭なら、トークンを分ける。
    /// </remarks>
    private static Dictionary<int, int> DiscoverTemplates(List<ShaderSyntaxToken> tokens)
    {
        Dictionary<int, int> ends = [];
        List<(int Open, int Depth)> pending = [];
        int depth = 0;

        for (int i = 0; i < tokens.Count; i++)
        {
            ShaderSyntaxToken token = tokens[i];

            if (token.Kind == ShaderSyntaxKind.IdentifierToken
                && i + 1 < tokens.Count
                && tokens[i + 1].Kind == ShaderSyntaxKind.LessThanToken)
            {
                pending.Add((i + 1, depth));
                i++;
                continue;
            }

            switch (token.Kind)
            {
                case ShaderSyntaxKind.GreaterThanToken:
                    // -> の > は山括弧を閉じない。
                    if (i > 0 && tokens[i - 1].Kind == ShaderSyntaxKind.MinusToken && token.FollowsWithoutSpace)
                    {
                        break;
                    }

                    if (pending.Count > 0 && pending[^1].Depth == depth)
                    {
                        ends[pending[^1].Open] = i;
                        pending.RemoveAt(pending.Count - 1);
                    }

                    break;

                case ShaderSyntaxKind.GreaterThanGreaterThanToken
                    or ShaderSyntaxKind.GreaterThanEqualsToken
                    or ShaderSyntaxKind.GreaterThanGreaterThanEqualsToken:
                    if (pending.Count > 0 && pending[^1].Depth == depth)
                    {
                        // 頭の > を分けて、もう一度見る。
                        (ShaderSyntaxToken head, ShaderSyntaxToken rest) = Split(token);
                        tokens[i] = head;
                        tokens.Insert(i + 1, rest);
                        i--;
                        continue;
                    }

                    if (token.Kind == ShaderSyntaxKind.GreaterThanGreaterThanEqualsToken)
                    {
                        pending.Clear();
                        depth = 0;
                    }

                    break;

                case ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.OpenBracketToken:
                    depth++;
                    break;

                case ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.CloseBracketToken:
                    while (pending.Count > 0 && pending[^1].Depth >= depth)
                    {
                        pending.RemoveAt(pending.Count - 1);
                    }

                    depth = Math.Max(0, depth - 1);
                    break;

                case ShaderSyntaxKind.EqualsToken
                    or ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken
                    or ShaderSyntaxKind.AsteriskEqualsToken or ShaderSyntaxKind.SlashEqualsToken
                    or ShaderSyntaxKind.PercentEqualsToken or ShaderSyntaxKind.AmpersandEqualsToken
                    or ShaderSyntaxKind.BarEqualsToken or ShaderSyntaxKind.CaretEqualsToken
                    or ShaderSyntaxKind.LessThanLessThanEqualsToken
                    or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken
                    or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.ColonToken:
                    pending.Clear();
                    depth = 0;
                    break;

                case ShaderSyntaxKind.AmpersandAmpersandToken or ShaderSyntaxKind.BarBarToken:
                    while (pending.Count > 0 && pending[^1].Depth == depth)
                    {
                        pending.RemoveAt(pending.Count - 1);
                    }

                    break;
            }
        }

        return ends;
    }

    /// <summary><c>&gt;&gt;</c> ・ <c>&gt;=</c> ・ <c>&gt;&gt;=</c> を、頭の <c>&gt;</c> と残りに分ける。</summary>
    private static (ShaderSyntaxToken First, ShaderSyntaxToken Remainder) Split(ShaderSyntaxToken token)
    {
        string restText = token.Text[1..];
        ShaderSyntaxKind restKind = restText switch
        {
            ">" => ShaderSyntaxKind.GreaterThanToken,
            "=" => ShaderSyntaxKind.EqualsToken,
            _ => ShaderSyntaxKind.GreaterThanEqualsToken,
        };

        ShaderSyntaxToken head = new(
            ShaderSyntaxKind.GreaterThanToken, token.Source, new TextSpan(token.Span.Start, 1), ">", token.IsAtLineStart, token.LeadingTrivia, [])
        {
            FollowsWithoutSpace = token.FollowsWithoutSpace,
        };
        ShaderSyntaxToken rest = new(
            restKind, token.Source, new TextSpan(token.Span.Start + 1, token.Span.Length - 1), restText, false, [], token.TrailingTrivia)
        {
            FollowsWithoutSpace = true,
        };

        return (head, rest);
    }

    /// <summary>
    /// ファイルの直下の <c>alias A = T;</c> を先に読む。WGSL の宣言は順序に依らず使えるので、使う所より後ろにあってもよい。
    /// </summary>
    private Dictionary<string, string> ScanAliases()
    {
        Dictionary<string, (ShaderSyntaxToken Name, ImmutableArray<ShaderSyntaxToken> Arguments)> raw = new(StringComparer.Ordinal);
        int depth = 0;

        for (int i = 0; i < _tokens.Length; i++)
        {
            switch (_tokens[i].Kind)
            {
                case ShaderSyntaxKind.OpenBraceToken:
                    depth++;
                    continue;
                case ShaderSyntaxKind.CloseBraceToken:
                    depth = Math.Max(0, depth - 1);
                    continue;
            }

            if (depth == 0
                && _tokens[i] is { Kind: ShaderSyntaxKind.IdentifierToken, Text: "alias" }
                && i + 3 < _tokens.Length
                && _tokens[i + 1].Kind == ShaderSyntaxKind.IdentifierToken
                && _tokens[i + 2].Kind == ShaderSyntaxKind.EqualsToken
                && _tokens[i + 3].Kind == ShaderSyntaxKind.IdentifierToken)
            {
                ImmutableArray<ShaderSyntaxToken> arguments = [];

                if (_templateEnds.TryGetValue(i + 4, out int end))
                {
                    arguments = [.. _tokens.Skip(i + 5).Take(end - i - 5)];
                }

                raw[_tokens[i + 1].Text] = (_tokens[i + 3], arguments);
            }
        }

        Dictionary<string, string> aliases = new(StringComparer.Ordinal);

        foreach ((string alias, (ShaderSyntaxToken name, ImmutableArray<ShaderSyntaxToken> arguments)) in raw)
        {
            string target = Canonicalize(name.Text, arguments) ?? name.Text;
            HashSet<string> seen = [alias];

            while (raw.TryGetValue(target, out (ShaderSyntaxToken Name, ImmutableArray<ShaderSyntaxToken> Arguments) next) && seen.Add(target))
            {
                target = Canonicalize(next.Name.Text, next.Arguments) ?? next.Name.Text;
            }

            aliases[alias] = target;
        }

        return aliases;
    }

    /// <summary>
    /// 型引数を書いたベクトルと行列 (<c>vec3&lt;f32&gt;</c>、<c>mat4x4&lt;f16&gt;</c>) を、短い名前 (<c>vec3f</c>、<c>mat4x4h</c>) にする。短い名前の無いもの (<c>vec3&lt;bool&gt;</c>) は、型引数を付けた名前にする。
    /// </summary>
    /// <returns>短い名前。そう書けない型なら <see langword="null"/>。</returns>
    internal static string? Canonicalize(string name, ImmutableArray<ShaderSyntaxToken> arguments)
    {
        if (arguments is not [{ Kind: ShaderSyntaxKind.IdentifierToken } element])
        {
            return null;
        }

        bool vector = name is "vec2" or "vec3" or "vec4";
        bool matrix = name.Length == 6 && name.StartsWith("mat", StringComparison.Ordinal)
                      && name[3] is >= '2' and <= '4' && name[4] == 'x' && name[5] is >= '2' and <= '4';

        if (!vector && !matrix)
        {
            return null;
        }

        // 短い名前の無い要素の型は、型引数を付けたまま 1 つの名前にする (vec3<bool>)。
        string? suffix = element.Text switch
        {
            "f32" => "f",
            "f16" => "h",
            "i32" when vector => "i",
            "u32" when vector => "u",
            "bool" or "i64" or "u64" or "i16" or "u16" when vector => $"<{element.Text}>",
            "f64" => "<f64>",
            _ => null,
        };

        return suffix is null ? null : name + suffix;
    }

    // --------------------------------------------------------------------
    // ファイル
    // --------------------------------------------------------------------

    /// <summary>ファイル全体を読む。</summary>
    /// <param name="diagnostics">構文エラー。</param>
    /// <returns>木の根。</returns>
    public CompilationUnitSyntax ParseCompilationUnit(out ImmutableArray<Diagnostic> diagnostics)
    {
        ImmutableArray<DeclarationSyntax>.Builder declarations = ImmutableArray.CreateBuilder<DeclarationSyntax>();

        while (Current.Kind != ShaderSyntaxKind.EndOfFileToken)
        {
            int start = _index;

            if (Current.Kind == ShaderSyntaxKind.SemicolonToken)
            {
                Advance();
                continue;
            }

            if (ParseGlobalDeclaration() is { } declaration)
            {
                declarations.Add(declaration);
            }

            if (_index == start)
            {
                declarations.Add(Recover(DescribeExpected("宣言")));
            }
        }

        diagnostics = _diagnostics.ToImmutable();
        return new CompilationUnitSyntax(declarations.ToImmutable(), Current)
        {
            TypeAliases = System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(_aliases, StringComparer.Ordinal),
        };
    }

    /// <summary>ファイルの直下の宣言を 1 つ読む。</summary>
    private DeclarationSyntax? ParseGlobalDeclaration()
    {
        if (Current.Kind == ShaderSyntaxKind.IdentifierToken)
        {
            switch (Current.Text)
            {
                case "enable" or "requires" or "diagnostic" when Peek(1).Kind is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.OpenParenToken:
                    return SkipToSemicolon(markNames: false);

                case "const_assert":
                    return SkipToSemicolon(markNames: true);

                case "alias":
                    return ParseAlias();

                case "struct":
                    return ParseStruct();
            }
        }

        ImmutableArray<AttributeSyntax> attributes = ParseAttributes();

        if (Current.Kind == ShaderSyntaxKind.IdentifierToken)
        {
            switch (Current.Text)
            {
                case "fn":
                    return ParseFunction(attributes);

                case "var" or "let" or "const" or "override":
                    return ParseVariable(AttributeTokens(attributes));
            }
        }

        return attributes.IsEmpty ? null : Recover(DescribeExpected("宣言"), [.. AttributeTokens(attributes)]);
    }

    /// <summary><c>alias A = T;</c> を読む。</summary>
    private DeclarationSyntax ParseAlias()
    {
        ShaderSyntaxToken keyword = Advance();
        ImmutableArray<ShaderSyntaxToken>.Builder tokens = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();
        tokens.Add(Expect(ShaderSyntaxKind.IdentifierToken, "別名"));
        tokens.Add(Expect(ShaderSyntaxKind.EqualsToken, "'='"));
        tokens.AddRange(ParseType().DescendantTokens());
        return new TypedefDeclarationSyntax(keyword, tokens.ToImmutable(), Expect(ShaderSyntaxKind.SemicolonToken, "';'"));
    }

    /// <summary><c>struct S { a: f32, @location(0) b: vec4f, }</c> を読む。</summary>
    private DeclarationSyntax ParseStruct()
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken name = Expect(ShaderSyntaxKind.IdentifierToken, "構造体の名前");
        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<DeclarationSyntax>.Builder members = ImmutableArray.CreateBuilder<DeclarationSyntax>();

        while (Current.Kind is not (ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.EndOfFileToken))
        {
            int start = _index;
            ImmutableArray<ShaderSyntaxToken> modifiers = AttributeTokens(ParseAttributes());
            ShaderSyntaxToken member = Expect(ShaderSyntaxKind.IdentifierToken, "メンバーの名前");
            _ = Expect(ShaderSyntaxKind.ColonToken, "':'");
            TypeSyntax type = ParseType();
            ShaderSyntaxToken separator = Current.Kind == ShaderSyntaxKind.CommaToken
                ? Advance()
                : Current.Kind == ShaderSyntaxKind.CloseBraceToken
                    ? CreateMissingToken(ShaderSyntaxKind.CommaToken)
                    : Expect(ShaderSyntaxKind.CommaToken, "','");

            members.Add(new VariableDeclarationSyntax(
                modifiers, type, [ShaderNodeOrTokenEntry.FromNode(new VariableDeclaratorSyntax(member, [], [], null, null))], separator));

            if (_index == start)
            {
                Advance();
            }
            else if (separator.IsMissing && Current.Kind != ShaderSyntaxKind.CloseBraceToken)
            {
                SkipUntil(ShaderSyntaxKind.CommaToken, ShaderSyntaxKind.CloseBraceToken);
                if (Current.Kind == ShaderSyntaxKind.CommaToken)
                {
                    Advance();
                }
            }
        }

        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");
        ShaderSyntaxToken semicolon = Current.Kind == ShaderSyntaxKind.SemicolonToken ? Advance() : CreateMissingToken(ShaderSyntaxKind.SemicolonToken);
        return new StructDeclarationSyntax(keyword, name, openBrace, members.ToImmutable(), closeBrace, [], semicolon);
    }

    /// <summary><c>@vertex fn main(@location(0) p: vec3f) -&gt; @builtin(position) vec4f { … }</c> を読む。</summary>
    private DeclarationSyntax ParseFunction(ImmutableArray<AttributeSyntax> attributes)
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken name = Expect(ShaderSyntaxKind.IdentifierToken, "関数の名前");
        ShaderSyntaxToken openParen = Expect(ShaderSyntaxKind.OpenParenToken, "'('");
        ImmutableArray<ShaderNodeOrTokenEntry>.Builder parameters = ImmutableArray.CreateBuilder<ShaderNodeOrTokenEntry>();

        while (!openParen.IsMissing && Current.Kind is not (ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.EndOfFileToken))
        {
            int start = _index;
            ImmutableArray<ShaderSyntaxToken> modifiers = AttributeTokens(ParseAttributes());
            ShaderSyntaxToken parameter = Expect(ShaderSyntaxKind.IdentifierToken, "引数の名前");
            _ = Expect(ShaderSyntaxKind.ColonToken, "':'");
            TypeSyntax type = ParseType();
            parameters.Add(ShaderNodeOrTokenEntry.FromNode(new ParameterSyntax(modifiers, type, parameter, [], [], null, null)));

            if (Current.Kind == ShaderSyntaxKind.CommaToken)
            {
                parameters.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            if (Current.Kind != ShaderSyntaxKind.CloseParenToken)
            {
                ReportError(DescribeExpected("',' か ')'"));
                SkipUntil(ShaderSyntaxKind.CloseParenToken, ShaderSyntaxKind.OpenBraceToken);
            }

            if (_index == start)
            {
                Advance();
            }
        }

        ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
        TypeSyntax returnType = MakeType(new ShaderSyntaxToken(ShaderSyntaxKind.IdentifierToken, keyword.Source, new TextSpan(keyword.Span.Start, 0), "void"));
        ImmutableArray<SemanticSyntax> semantics = [];

        if (IsArrow())
        {
            Advance();
            Advance();
            semantics = [.. ParseAttributes().Select(a => new SemanticSyntax(a.OpenBracket, a.NameToken, a.OpenParen, a.ArgumentTokens, a.CloseParen))];
            returnType = ParseType();
        }

        BlockStatementSyntax? body = null;

        if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
        {
            body = ParseBlock();
        }
        else
        {
            ReportError(DescribeExpected("'{'"));
        }

        return new FunctionDeclarationSyntax(
            attributes, [keyword], returnType, name, openParen, parameters.ToImmutable(), closeParen, semantics, body,
            body is null ? CreateMissingToken(ShaderSyntaxKind.SemicolonToken) : null);
    }

    /// <summary><c>var&lt;private&gt; x: f32 = 1.0;</c> ・ <c>let y = 2;</c> ・ <c>const N = 4u;</c> ・ <c>override k: f32;</c> を読む。</summary>
    /// <param name="prefix">前に書いた属性のトークン。</param>
    /// <param name="requireSemicolon"><c>;</c> を読むか (<c>for</c> の初期化の宣言も <c>;</c> で終わる)。</param>
    private VariableDeclarationSyntax ParseVariable(ImmutableArray<ShaderSyntaxToken> prefix, bool requireSemicolon = true)
    {
        ShaderSyntaxToken keyword = Advance();
        ImmutableArray<ShaderSyntaxToken>.Builder modifiers = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();
        modifiers.AddRange(prefix);
        modifiers.Add(keyword);

        if (Current.Kind == ShaderSyntaxKind.LessThanToken && _templateEnds.TryGetValue(_index, out int end))
        {
            while (_index <= end)
            {
                modifiers.Add(Advance());
            }
        }

        ShaderSyntaxToken name = Expect(ShaderSyntaxKind.IdentifierToken, "変数の名前");
        TypeSyntax type = MakeType(keyword);

        if (Current.Kind == ShaderSyntaxKind.ColonToken)
        {
            Advance();
            type = ParseType();
        }

        ShaderSyntaxToken? equals = null;
        ExpressionSyntax? initializer = null;

        if (Current.Kind == ShaderSyntaxKind.EqualsToken)
        {
            equals = Advance();
            initializer = ParseExpression();
        }

        ShaderSyntaxToken semicolon = requireSemicolon ? ExpectSemicolon() : CreateMissingToken(ShaderSyntaxKind.SemicolonToken);
        VariableDeclaratorSyntax declarator = new(name, [], [], equals, initializer);
        return new VariableDeclarationSyntax(modifiers.ToImmutable(), type, [ShaderNodeOrTokenEntry.FromNode(declarator)], semicolon);
    }

    // --------------------------------------------------------------------
    // 型と属性
    // --------------------------------------------------------------------

    /// <summary>型 (<c>f32</c>、<c>vec3&lt;f32&gt;</c>、<c>array&lt;f32, 4&gt;</c>、<c>ptr&lt;function, i32&gt;</c>) を読む。</summary>
    private TypeSyntax ParseType()
    {
        if (Current.Kind != ShaderSyntaxKind.IdentifierToken)
        {
            ReportError(DescribeExpected("型"));
            return MakeType(CreateMissingToken(ShaderSyntaxKind.IdentifierToken));
        }

        ShaderSyntaxToken name = Advance();
        ShaderSyntaxToken? openAngle = null;
        ShaderSyntaxToken? closeAngle = null;
        ImmutableArray<ShaderSyntaxToken> arguments = [];

        if (Current.Kind == ShaderSyntaxKind.LessThanToken && _templateEnds.TryGetValue(_index, out int end))
        {
            openAngle = Advance();
            ImmutableArray<ShaderSyntaxToken>.Builder builder = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

            while (_index < end)
            {
                builder.Add(Advance());
            }

            arguments = builder.ToImmutable();
            closeAngle = Advance();
        }

        string? canonical = Canonicalize(name.Text, arguments);
        string? aliasOf = arguments.IsEmpty && _aliases.TryGetValue(name.Text, out string? target) ? target : null;
        return new TypeSyntax([], name, [], openAngle, arguments, closeAngle, aliasOf, canonical);
    }

    /// <summary>名前のトークンだけの型を作る。</summary>
    private static TypeSyntax MakeType(ShaderSyntaxToken name) => new([], name, [], null, [], null, null, null);

    /// <summary>属性 (<c>@group(0)</c>、<c>@vertex</c>、<c>@workgroup_size(8, 8)</c>) を並びの終わりまで読む。</summary>
    /// <remarks>C 系の属性のノードにする。<c>@</c> を開きの角括弧の位置に置き、閉じの角括弧は欠けたものにする。</remarks>
    private ImmutableArray<AttributeSyntax> ParseAttributes()
    {
        ImmutableArray<AttributeSyntax>.Builder attributes = ImmutableArray.CreateBuilder<AttributeSyntax>();

        while (IsAt())
        {
            ShaderSyntaxToken at = Advance();
            ShaderSyntaxToken name = Expect(ShaderSyntaxKind.IdentifierToken, "属性の名前");
            ShaderSyntaxToken? openParen = null;
            ShaderSyntaxToken? closeParen = null;
            ImmutableArray<ShaderSyntaxToken>.Builder arguments = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

            if (Current.Kind == ShaderSyntaxKind.OpenParenToken)
            {
                openParen = Advance();
                int depth = 0;

                while (Current.Kind != ShaderSyntaxKind.EndOfFileToken && !(depth == 0 && Current.Kind == ShaderSyntaxKind.CloseParenToken))
                {
                    depth += Current.Kind switch
                    {
                        ShaderSyntaxKind.OpenParenToken => 1,
                        ShaderSyntaxKind.CloseParenToken => -1,
                        _ => 0,
                    };
                    arguments.Add(Advance());
                }

                closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
            }

            attributes.Add(new AttributeSyntax(at, name, openParen, arguments.ToImmutable(), closeParen, CreateMissingToken(ShaderSyntaxKind.CloseBracketToken)));
        }

        return attributes.ToImmutable();
    }

    /// <summary>属性のトークンを並べる (変数・引数・メンバーの修飾子にする)。</summary>
    private static ImmutableArray<ShaderSyntaxToken> AttributeTokens(ImmutableArray<AttributeSyntax> attributes)
        => [.. attributes.SelectMany(a => a.DescendantTokens()).Where(t => !t.IsMissing)];

    // --------------------------------------------------------------------
    // 文
    // --------------------------------------------------------------------

    /// <summary><c>{ … }</c> を読む。</summary>
    private BlockStatementSyntax ParseBlock()
    {
        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<StatementSyntax> statements = ParseStatements();
        return new BlockStatementSyntax(openBrace, statements, Expect(ShaderSyntaxKind.CloseBraceToken, "'}'"));
    }

    /// <summary><c>}</c> の手前までの文を読む。</summary>
    private ImmutableArray<StatementSyntax> ParseStatements(bool inLoop = false)
    {
        ImmutableArray<StatementSyntax>.Builder statements = ImmutableArray.CreateBuilder<StatementSyntax>();

        while (Current.Kind is not (ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.EndOfFileToken))
        {
            int start = _index;

            // loop の最後の continuing { … }。本体の最後のブロックにする。
            if (inLoop && CurrentText("continuing") && (Peek(1).Kind == ShaderSyntaxKind.OpenBraceToken || IsAt(1)))
            {
                Advance();
                _ = ParseAttributes();
                statements.Add(ParseBlock());
                continue;
            }

            statements.Add(ParseStatement());

            if (_index == start)
            {
                ReportError(DescribeExpected("文"));
                statements.Add(new IncompleteStatementSyntax([Advance()]));
            }
        }

        return statements.ToImmutable();
    }

    /// <summary>文を 1 つ読む。</summary>
    private StatementSyntax ParseStatement()
    {
        ImmutableArray<AttributeSyntax> attributes = ParseAttributes();

        switch (Current.Kind)
        {
            case ShaderSyntaxKind.OpenBraceToken:
                return ParseBlock();

            case ShaderSyntaxKind.SemicolonToken:
                return new EmptyStatementSyntax(Advance());

            case ShaderSyntaxKind.IdentifierToken:
                switch (Current.Text)
                {
                    case "var" or "let" or "const":
                        return new LocalDeclarationStatementSyntax(ParseVariable(AttributeTokens(attributes)));

                    case "return":
                    {
                        ShaderSyntaxToken keyword = Advance();
                        ExpressionSyntax? value = Current.Kind == ShaderSyntaxKind.SemicolonToken ? null : ParseExpression();
                        return new ReturnStatementSyntax(keyword, value, ExpectSemicolon());
                    }

                    case "if":
                        return ParseIf(attributes);

                    case "switch":
                        return ParseSwitch(attributes);

                    case "loop":
                        return ParseLoop(attributes);

                    case "for":
                        return ParseFor(attributes);

                    case "while":
                    {
                        ShaderSyntaxToken keyword = Advance();
                        ShaderSyntaxToken openParen = CreateMissingToken(ShaderSyntaxKind.OpenParenToken);
                        ExpressionSyntax condition = ParseExpression();
                        ShaderSyntaxToken closeParen = CreateMissingToken(ShaderSyntaxKind.CloseParenToken);
                        return new WhileStatementSyntax(attributes, keyword, openParen, condition, closeParen, ParseBlock());
                    }

                    case "break" when Peek(1) is { Kind: ShaderSyntaxKind.IdentifierToken, Text: "if" }:
                    {
                        // break if c; → if (c) break;
                        ShaderSyntaxToken keyword = Advance();
                        Advance();
                        ShaderSyntaxToken openParen = CreateMissingToken(ShaderSyntaxKind.OpenParenToken);
                        ExpressionSyntax condition = ParseExpression();
                        ShaderSyntaxToken closeParen = CreateMissingToken(ShaderSyntaxKind.CloseParenToken);
                        ShaderSyntaxToken semicolon = ExpectSemicolon();
                        ShaderSyntaxToken jump = new(ShaderSyntaxKind.IdentifierToken, keyword.Source, new TextSpan(semicolon.Span.Start, 0), "break", isMissing: true);
                        return new IfStatementSyntax([], keyword, openParen, condition, closeParen, new JumpStatementSyntax(jump, semicolon), null, null);
                    }

                    case "break" or "continue" or "discard":
                        return new JumpStatementSyntax(Advance(), ExpectSemicolon());

                    case "const_assert":
                    {
                        Advance();
                        ExpressionSyntax condition = ParseExpression();
                        return new ExpressionStatementSyntax(condition, ExpectSemicolon());
                    }
                }

                break;
        }

        int start = _index;
        ExpressionSyntax? simple = ParseSimpleStatement();

        if (simple is null || _index == start)
        {
            return new IncompleteStatementSyntax([]);
        }

        return new ExpressionStatementSyntax(simple, ExpectSemicolon());
    }

    /// <summary>代入・増減・呼び出しの文 (<c>;</c> を除く) を読む。<c>for</c> の初期化と更新にも使う。</summary>
    private ExpressionSyntax? ParseSimpleStatement()
    {
        if (!StartsExpression())
        {
            return null;
        }

        // _ = e; (値を捨てる)。_ は名前ではないので、名前の参照にしない。
        ExpressionSyntax left = CurrentText("_") && Peek(1).Kind == ShaderSyntaxKind.EqualsToken
            ? new IncompleteExpressionSyntax([Advance()])
            : ParseExpression();

        switch (Current.Kind)
        {
            case ShaderSyntaxKind.EqualsToken
                or ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken
                or ShaderSyntaxKind.AsteriskEqualsToken or ShaderSyntaxKind.SlashEqualsToken
                or ShaderSyntaxKind.PercentEqualsToken or ShaderSyntaxKind.AmpersandEqualsToken
                or ShaderSyntaxKind.BarEqualsToken or ShaderSyntaxKind.CaretEqualsToken
                or ShaderSyntaxKind.LessThanLessThanEqualsToken or ShaderSyntaxKind.GreaterThanGreaterThanEqualsToken:
            {
                ShaderSyntaxToken op = Advance();
                return new AssignmentExpressionSyntax(left, op, ParseExpression());
            }

            case ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken:
                return new PostfixUnaryExpressionSyntax(left, Advance());
        }

        return left;
    }

    /// <summary><c>if c { … } else if d { … } else { … }</c> を読む。</summary>
    private IfStatementSyntax ParseIf(ImmutableArray<AttributeSyntax> attributes)
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken openParen = CreateMissingToken(ShaderSyntaxKind.OpenParenToken);
        ExpressionSyntax condition = ParseExpression();
        ShaderSyntaxToken closeParen = CreateMissingToken(ShaderSyntaxKind.CloseParenToken);
        BlockStatementSyntax then = ParseBlock();
        ShaderSyntaxToken? elseKeyword = null;
        StatementSyntax? otherwise = null;

        if (CurrentText("else"))
        {
            elseKeyword = Advance();
            otherwise = CurrentText("if") ? ParseIf([]) : ParseBlock();
        }

        return new IfStatementSyntax(attributes, keyword, openParen, condition, closeParen, then, elseKeyword, otherwise);
    }

    /// <summary><c>switch e { case 1, 2: { … } default { … } }</c> を読む。</summary>
    private SwitchStatementSyntax ParseSwitch(ImmutableArray<AttributeSyntax> attributes)
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken openParen = CreateMissingToken(ShaderSyntaxKind.OpenParenToken);
        ExpressionSyntax value = ParseExpression();
        ShaderSyntaxToken closeParen = CreateMissingToken(ShaderSyntaxKind.CloseParenToken);
        _ = ParseAttributes();
        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<StatementSyntax>.Builder statements = ImmutableArray.CreateBuilder<StatementSyntax>();

        while (Current.Kind is not (ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.EndOfFileToken))
        {
            int start = _index;

            if (CurrentText("case"))
            {
                ShaderSyntaxToken label = Advance();
                List<(ShaderSyntaxToken Keyword, ExpressionSyntax? Value)> selectors = [];

                while (true)
                {
                    if (CurrentText("default"))
                    {
                        selectors.Add((Advance(), null));
                    }
                    else
                    {
                        selectors.Add((label, ParseExpression()));
                    }

                    if (Current.Kind != ShaderSyntaxKind.CommaToken)
                    {
                        break;
                    }

                    label = Advance();

                    if (Current.Kind is ShaderSyntaxKind.ColonToken or ShaderSyntaxKind.OpenBraceToken)
                    {
                        break;
                    }
                }

                ShaderSyntaxToken colon = Current.Kind == ShaderSyntaxKind.ColonToken ? Advance() : CreateMissingToken(ShaderSyntaxKind.ColonToken);

                for (int i = 0; i < selectors.Count; i++)
                {
                    statements.Add(new SwitchLabelStatementSyntax(
                        selectors[i].Keyword, selectors[i].Value, i == selectors.Count - 1 ? colon : CreateMissingToken(ShaderSyntaxKind.ColonToken)));
                }

                _ = ParseAttributes();
                statements.Add(ParseBlock());
            }
            else if (CurrentText("default"))
            {
                ShaderSyntaxToken label = Advance();
                ShaderSyntaxToken colon = Current.Kind == ShaderSyntaxKind.ColonToken ? Advance() : CreateMissingToken(ShaderSyntaxKind.ColonToken);
                statements.Add(new SwitchLabelStatementSyntax(label, null, colon));
                _ = ParseAttributes();
                statements.Add(ParseBlock());
            }
            else
            {
                ReportError(DescribeExpected("'case' か 'default'"));
            }

            if (_index == start)
            {
                statements.Add(new IncompleteStatementSyntax([Advance()]));
            }
        }

        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");
        return new SwitchStatementSyntax(attributes, keyword, openParen, value, closeParen, openBrace, statements.ToImmutable(), closeBrace);
    }

    /// <summary><c>loop { … continuing { … break if c; } }</c> を読む。条件が <c>true</c> の <c>while</c> にする。</summary>
    private WhileStatementSyntax ParseLoop(ImmutableArray<AttributeSyntax> attributes)
    {
        ShaderSyntaxToken keyword = Advance();
        _ = ParseAttributes();
        ShaderSyntaxToken openParen = CreateMissingToken(ShaderSyntaxKind.OpenParenToken);
        ShaderSyntaxToken alwaysTrue = new(ShaderSyntaxKind.IdentifierToken, keyword.Source, new TextSpan(keyword.Span.End, 0), "true", isMissing: true);
        ShaderSyntaxToken closeParen = CreateMissingToken(ShaderSyntaxKind.CloseParenToken);

        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<StatementSyntax> statements = ParseStatements(inLoop: true);
        BlockStatementSyntax body = new(openBrace, statements, Expect(ShaderSyntaxKind.CloseBraceToken, "'}'"));

        return new WhileStatementSyntax(attributes, keyword, openParen, new IdentifierExpressionSyntax(alwaysTrue), closeParen, body);
    }

    /// <summary><c>for (var i = 0; i &lt; n; i++) { … }</c> を読む。</summary>
    private ForStatementSyntax ParseFor(ImmutableArray<AttributeSyntax> attributes)
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken openParen = Expect(ShaderSyntaxKind.OpenParenToken, "'('");
        ShaderSyntaxNode? initializer = null;
        ShaderSyntaxToken? firstSemicolon = null;

        if (Current.Kind == ShaderSyntaxKind.IdentifierToken && Current.Text is "var" or "let" or "const")
        {
            initializer = ParseVariable([]);
        }
        else
        {
            initializer = ParseSimpleStatement();
            firstSemicolon = ExpectSemicolon();
        }

        ExpressionSyntax? condition = Current.Kind == ShaderSyntaxKind.SemicolonToken ? null : ParseExpression();
        ShaderSyntaxToken secondSemicolon = ExpectSemicolon();
        ImmutableArray<ShaderNodeOrTokenEntry> incrementors = [];

        if (Current.Kind != ShaderSyntaxKind.CloseParenToken && ParseSimpleStatement() is { } update)
        {
            incrementors = [ShaderNodeOrTokenEntry.FromNode(update)];
        }

        ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
        return new ForStatementSyntax(attributes, keyword, openParen, initializer, firstSemicolon, condition, secondSemicolon, incrementors, closeParen, ParseBlock());
    }

    // --------------------------------------------------------------------
    // 式
    // --------------------------------------------------------------------

    /// <summary>式を読む。</summary>
    private ExpressionSyntax ParseExpression() => ParseBinary(1);

    /// <summary>二項演算子の優先順位。演算子でなければ 0。</summary>
    private static int Precedence(ShaderSyntaxKind kind) => kind switch
    {
        ShaderSyntaxKind.BarBarToken => 1,
        ShaderSyntaxKind.AmpersandAmpersandToken => 2,
        ShaderSyntaxKind.BarToken => 3,
        ShaderSyntaxKind.CaretToken => 4,
        ShaderSyntaxKind.AmpersandToken => 5,
        ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken => 6,
        ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.GreaterThanToken
            or ShaderSyntaxKind.LessThanEqualsToken or ShaderSyntaxKind.GreaterThanEqualsToken => 7,
        ShaderSyntaxKind.LessThanLessThanToken or ShaderSyntaxKind.GreaterThanGreaterThanToken => 8,
        ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken => 9,
        ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.SlashToken or ShaderSyntaxKind.PercentToken => 10,
        _ => 0,
    };

    private ExpressionSyntax ParseBinary(int minimum)
    {
        ExpressionSyntax left = ParseUnary();

        while (Precedence(Current.Kind) is var precedence && precedence >= minimum && !IsArrow())
        {
            ShaderSyntaxToken op = Advance();
            ExpressionSyntax right = ParseBinary(precedence + 1);
            left = new BinaryExpressionSyntax(left, op, right);
        }

        return left;
    }

    private ExpressionSyntax ParseUnary()
    {
        if (Current.Kind is ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.ExclamationToken or ShaderSyntaxKind.TildeToken
            or ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.AmpersandToken)
        {
            ShaderSyntaxToken op = Advance();
            return new PrefixUnaryExpressionSyntax(op, ParseUnary());
        }

        // && は & を 2 つ並べたもの (&&x は &(&x))。C 系の字句解析は 1 つのトークンにする。
        if (Current.Kind == ShaderSyntaxKind.AmpersandAmpersandToken)
        {
            ShaderSyntaxToken op = Advance();
            return new PrefixUnaryExpressionSyntax(op, ParseUnary());
        }

        return ParsePostfix(ParsePrimary());
    }

    private ExpressionSyntax ParsePostfix(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (Current.Kind)
            {
                case ShaderSyntaxKind.OpenBracketToken:
                {
                    ShaderSyntaxToken open = Advance();
                    ExpressionSyntax index = ParseExpression();
                    expression = new ElementAccessExpressionSyntax(expression, open, index, Expect(ShaderSyntaxKind.CloseBracketToken, "']'"));
                    continue;
                }

                case ShaderSyntaxKind.DotToken:
                {
                    ShaderSyntaxToken dot = Advance();
                    expression = new MemberAccessExpressionSyntax(expression, dot, Expect(ShaderSyntaxKind.IdentifierToken, "メンバーの名前"));
                    continue;
                }
            }

            return expression;
        }
    }

    private ExpressionSyntax ParsePrimary()
    {
        switch (Current.Kind)
        {
            case ShaderSyntaxKind.NumericLiteralToken:
                return new LiteralExpressionSyntax(Advance());

            case ShaderSyntaxKind.OpenParenToken:
            {
                ShaderSyntaxToken open = Advance();
                ExpressionSyntax inner = ParseExpression();
                return new ParenthesizedExpressionSyntax(open, inner, Expect(ShaderSyntaxKind.CloseParenToken, "')'"));
            }

            case ShaderSyntaxKind.IdentifierToken:
            {
                ShaderSyntaxToken name = Advance();
                ShaderSyntaxToken? openAngle = null;
                ShaderSyntaxToken? closeAngle = null;
                ImmutableArray<ShaderSyntaxToken> arguments = [];

                if (Current.Kind == ShaderSyntaxKind.LessThanToken && _templateEnds.TryGetValue(_index, out int end))
                {
                    openAngle = Advance();
                    ImmutableArray<ShaderSyntaxToken>.Builder builder = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

                    while (_index < end)
                    {
                        builder.Add(Advance());
                    }

                    arguments = builder.ToImmutable();
                    closeAngle = Advance();
                }

                if (Current.Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    ShaderSyntaxToken openParen = Advance();
                    ImmutableArray<ShaderNodeOrTokenEntry> callArguments = ParseArguments();
                    ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
                    ShaderSyntaxToken target = name;

                    // vec3<f32>(…) は vec3f(…) と同じ。呼び出す名前を短い名前にして、型引数を書かない呼び出しにする
                    // (型引数を書いた呼び出しの型は、意味解析が主張しない)。
                    if (Canonicalize(name.Text, arguments) is { } canonical)
                    {
                        target = new ShaderSyntaxToken(name.Kind, name.Source, name.Span, canonical, name.IsAtLineStart, name.LeadingTrivia, name.TrailingTrivia)
                        {
                            FollowsWithoutSpace = name.FollowsWithoutSpace,
                        };
                        openAngle = null;
                        closeAngle = null;
                        arguments = [];
                    }

                    return new InvocationExpressionSyntax(new IdentifierExpressionSyntax(target), openParen, callArguments, closeParen, openAngle, arguments, closeAngle);
                }

                return openAngle is not null
                    ? new GenericNameExpressionSyntax(name, openAngle, arguments, closeAngle!)
                    : new IdentifierExpressionSyntax(name);
            }
        }

        ReportError(DescribeExpected("式"));
        return new IncompleteExpressionSyntax([CreateMissingToken(ShaderSyntaxKind.IdentifierToken)]);
    }

    /// <summary>呼び出しの引数を、<c>)</c> の手前まで読む (最後の <c>,</c> は書いてよい)。</summary>
    private ImmutableArray<ShaderNodeOrTokenEntry> ParseArguments()
    {
        ImmutableArray<ShaderNodeOrTokenEntry>.Builder arguments = ImmutableArray.CreateBuilder<ShaderNodeOrTokenEntry>();

        while (Current.Kind is not (ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.EndOfFileToken))
        {
            int start = _index;
            arguments.Add(ShaderNodeOrTokenEntry.FromNode(ParseExpression()));

            if (Current.Kind != ShaderSyntaxKind.CommaToken)
            {
                break;
            }

            arguments.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));

            if (_index == start)
            {
                break;
            }
        }

        return arguments.ToImmutable();
    }

    /// <summary>今の位置から式が始まるか。</summary>
    private bool StartsExpression()
        => Current.Kind is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.NumericLiteralToken or ShaderSyntaxKind.OpenParenToken
            or ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.ExclamationToken or ShaderSyntaxKind.TildeToken
            or ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.AmpersandToken or ShaderSyntaxKind.AmpersandAmpersandToken;

    // --------------------------------------------------------------------
    // 立て直しとトークンの操作
    // --------------------------------------------------------------------

    /// <summary><c>;</c> までを読み飛ばして、1 つの宣言にする。</summary>
    private IncompleteDeclarationSyntax SkipToSemicolon(bool markNames)
    {
        ImmutableArray<ShaderSyntaxToken>.Builder tokens = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        while (Current.Kind is not (ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.EndOfFileToken))
        {
            tokens.Add(Advance());
        }

        tokens.Add(ExpectSemicolon());

        if (markNames)
        {
            _unanalyzed.AddRange(tokens.Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken));
        }

        return new IncompleteDeclarationSyntax(tokens.ToImmutable());
    }

    /// <summary>構文エラーを報告し、次の宣言の始まりまで読み飛ばす。</summary>
    private IncompleteDeclarationSyntax Recover(string message, IEnumerable<ShaderSyntaxToken>? prefix = null)
    {
        ReportError(message);
        List<ShaderSyntaxToken> tokens = [.. prefix ?? []];
        int depth = 0;

        do
        {
            ShaderSyntaxToken token = Advance();
            tokens.Add(token);

            depth += token.Kind switch
            {
                ShaderSyntaxKind.OpenBraceToken => 1,
                ShaderSyntaxKind.CloseBraceToken => -1,
                _ => 0,
            };

            if (depth <= 0 && token.Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken)
            {
                break;
            }
        }
        while (Current.Kind != ShaderSyntaxKind.EndOfFileToken
               && !(depth == 0 && (IsAt() || Current is { Kind: ShaderSyntaxKind.IdentifierToken, IsAtLineStart: true, Text: "fn" or "struct" or "var" or "const" or "override" or "alias" })));

        _unanalyzed.AddRange(tokens.Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken));
        return new IncompleteDeclarationSyntax([.. tokens]);
    }

    /// <summary>指定の種類のトークンの手前まで (括弧の中は飛ばして) 読み飛ばす。</summary>
    private void SkipUntil(params ShaderSyntaxKind[] kinds)
    {
        int depth = 0;

        while (Current.Kind != ShaderSyntaxKind.EndOfFileToken && !(depth == 0 && kinds.Contains(Current.Kind)))
        {
            depth += Current.Kind switch
            {
                ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.OpenBracketToken => 1,
                ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.CloseBracketToken => -1,
                _ => 0,
            };

            if (depth < 0)
            {
                return;
            }

            _unanalyzed.Add(Advance());
        }
    }

    private ShaderSyntaxToken Current => _tokens[Math.Min(_index, _tokens.Length - 1)];

    private ShaderSyntaxToken Peek(int offset) => _tokens[Math.Clamp(_index + offset, 0, _tokens.Length - 1)];

    private ShaderSyntaxToken Advance()
    {
        ShaderSyntaxToken token = Current;

        if (_index < _tokens.Length - 1)
        {
            _index++;
        }

        return token;
    }

    private bool CurrentText(string text) => Current.Kind == ShaderSyntaxKind.IdentifierToken && Current.Text == text;

    /// <summary>今の位置 (から <paramref name="offset"/> 先) が属性の <c>@</c> か。</summary>
    private bool IsAt(int offset = 0) => Peek(offset) is { Kind: ShaderSyntaxKind.BadToken, Text: "@" };

    /// <summary>今の位置が <c>-&gt;</c> か。</summary>
    private bool IsArrow()
        => Current.Kind == ShaderSyntaxKind.MinusToken && Peek(1) is { Kind: ShaderSyntaxKind.GreaterThanToken, FollowsWithoutSpace: true };

    private ShaderSyntaxToken Expect(ShaderSyntaxKind kind, string expectation)
    {
        if (Current.Kind == kind)
        {
            return Advance();
        }

        ReportError(DescribeExpected(expectation));
        return CreateMissingToken(kind);
    }

    private ShaderSyntaxToken ExpectSemicolon() => Expect(ShaderSyntaxKind.SemicolonToken, "';'");

    private ShaderSyntaxToken CreateMissingToken(ShaderSyntaxKind kind)
        => new(kind, Current.Source, new TextSpan(Current.Span.Start, 0), string.Empty, isMissing: true);

    private string DescribeExpected(string expectation)
        => Current.Kind == ShaderSyntaxKind.EndOfFileToken
            ? $"ファイルの末尾には {expectation} が必要です。"
            : $"{expectation} が必要ですが '{Current.Text}' がありました。";

    private void ReportError(string message)
    {
        if (_reportedErrors >= MaxReportedErrors)
        {
            return;
        }

        if (_lastErrorToken is not null
            && ReferenceEquals(_lastErrorToken.Source, Current.Source)
            && _lastErrorToken.Span.Start == Current.Span.Start)
        {
            return;
        }

        _lastErrorToken = Current;
        _reportedErrors++;
        _diagnostics.Add(Diagnostic.Create(ShaderDescriptors.SyntaxError, Current.GetLocation(), message));
    }
}
