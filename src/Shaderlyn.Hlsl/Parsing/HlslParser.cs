using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Parsing;

/// <summary>
/// プリプロセス済みの HLSL トークン列を構文木へ組み立てる再帰下降パーサ。
/// </summary>
/// <remarks>
/// <para>
/// <b>このパーサは決して例外を投げない。</b>
/// 期待するトークンが無い場合は長さ 0 の欠落トークンを合成し、
/// 解釈できない区間は <c>Incomplete</c> 系のノードへ回収する。
/// </para>
/// <para>
/// 入力はプリプロセス済みであることを前提とする。
/// マクロは展開済みで、条件分岐の非活性領域は取り除かれ、指令は存在しない。
/// </para>
/// </remarks>
internal sealed class HlslParser
{
    /// <summary>
    /// 1 回の解析で報告する構文エラーの上限。
    /// </summary>
    /// <remarks>
    /// マクロが期待どおり展開されないと、1 ファイルから数千件のエラーが出ることがある。
    /// そのすべてを報告しても読み手の役に立たず、他のファイルの指摘が埋もれるだけなので、
    /// 一定数で打ち切る。原因は先頭の数件を見れば分かる。
    /// </remarks>
    public const int MaxReportedErrors = 32;

    /// <summary>
    /// 1 つの式につなげられる二項演算の項の数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>木の深さに上限を置くためにある。</b>
    /// <c>a + b + c ...</c> は左へ伸びる二項式の連なりになり、
    /// その木を歩く処理は項の数だけ再帰する。
    /// </para>
    /// <para>
    /// 人が書く式がこの数に届くことはない。
    /// 届くのは、マクロを重ねて作った展開結果である。
    /// </para>
    /// </remarks>
    public const int MaxBinaryOperands = 8192;

    private readonly ImmutableArray<HlslSyntaxToken> _tokens;
    private readonly SourceText? _ownSource;
    private readonly Func<string, bool>? _isUserInclude;
    private readonly Dictionary<SourceText, bool> _ownSources = [];
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

    /// <summary>ここまでに読んだ typedef の、別名から元の型の名前への対応。</summary>
    private readonly Dictionary<string, string> _typeAliases = new(StringComparer.Ordinal);
    private int _index;
    private int _reportedErrors;

    /// <summary>
    /// パーサを生成する。
    /// </summary>
    /// <param name="tokens">プリプロセス済みのトークン列。</param>
    public HlslParser(ImmutableArray<HlslSyntaxToken> tokens)
        => _tokens = tokens.IsDefault ? [] : tokens;

    /// <summary>
    /// 解析ツールを生成する。利用者のファイルでないヘッダの関数の中身は読み飛ばす。
    /// </summary>
    /// <param name="tokens">解析するトークン列。</param>
    /// <param name="ownSource">解析しているファイル自身のテキスト。</param>
    /// <param name="isUserInclude">
    /// 取り込んだファイルが利用者のものかをパスから判定する。利用者のヘッダの関数は中身まで作る。
    /// <see langword="null"/> なら、解析しているファイルの関数だけを作る。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Unity や外部パッケージのヘッダの関数の中身は、誰も見ていない。</b>
    /// ルールがそれらのヘッダに求めるのは関数の形・構造体・宣言であり、
    /// 本体の文は見ない。指摘するのも利用者が書いたコードだけである。
    /// </para>
    /// <para>
    /// <b>利用者が書いたヘッダは別である。</b>共通の <c>.hlsl</c> の関数も利用者が直すコードであり、
    /// 取り込む側の文脈で中身まで検査する。
    /// </para>
    /// <para>
    /// それでも今までは中身をすべてノードにしていた。
    /// Unity 同梱のシェーダーを 200 件並べた形で測ると、
    /// <b>構文解析と親の設定だけで CPU 時間の半分</b>を使っていた。
    /// </para>
    /// <para>
    /// 波括弧は残す。関数が実装を持つかどうか (HL0311) は括弧の有無で決まり、
    /// 範囲も括弧から求まる。中の文だけを作らない。
    /// </para>
    /// </remarks>
    public HlslParser(ImmutableArray<HlslSyntaxToken> tokens, SourceText? ownSource, Func<string, bool>? isUserInclude = null)
        : this(tokens)
    {
        _ownSource = ownSource;
        _isUserInclude = isUserInclude;
    }

    /// <summary>
    /// そのトークン列が、文・宣言として過不足なく閉じているかを判定する。
    /// </summary>
    /// <param name="tokens">判定するトークン列。</param>
    /// <returns>閉じていれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>括弧の数では判定できない。</b>
    /// <c>float v = ;</c> は括弧が釣り合い末尾も <c>;</c> だが、文ではない。
    /// 中身が構成によって変わるマクロを複製すると、この形が現れる
    /// (<c>#define VAL</c> と <c>#define VAL 1</c> を切り替えた場合)。
    /// </para>
    /// <para>
    /// 文法で確かめれば、あとで本番の構文解析が見るものと同じ入力を、同じ規則で読むことになる。
    /// 判断がずれる余地が無い。
    /// </para>
    /// <para>
    /// 1 つに限らない。マクロが 2 つの文に展開されることはあり、
    /// どちらも閉じているならそのまま並べてよい。
    /// </para>
    /// </remarks>
    public static bool IsCompleteUnits(ImmutableArray<HlslSyntaxToken> tokens)
    {
        if (tokens.IsDefaultOrEmpty)
        {
            return true;
        }

        int consumed = 0;

        while (consumed < tokens.Length)
        {
            if (!TryParseSingleUnit(tokens[consumed..], out int length))
            {
                return false;
            }

            consumed += length;
        }

        return true;
    }

    /// <summary>
    /// トークン列の先頭から、文・宣言を 1 つだけ読む。
    /// </summary>
    /// <param name="tokens">読むトークン列。</param>
    /// <param name="consumed">読んだトークンの数。読めなかった場合は 0。</param>
    /// <returns>誤り無く読めた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>文として読めなければ宣言として読み直す。</b>
    /// 関数の中なら文、ファイルの直下なら宣言だが、
    /// どちらの位置のトークン列かは呼び出し側にも分からないことがある。
    /// </remarks>
    public static bool TryParseSingleUnit(ImmutableArray<HlslSyntaxToken> tokens, out int consumed)
    {
        if (TryParseOne(tokens, asStatement: true, out consumed))
        {
            return true;
        }

        return TryParseOne(tokens, asStatement: false, out consumed);
    }

    /// <summary>1 つ分を読んでみる。</summary>
    /// <param name="tokens">読むトークン列。</param>
    /// <param name="asStatement">文として読むなら <see langword="true"/>、宣言なら <see langword="false"/>。</param>
    /// <param name="consumed">読んだトークンの数。</param>
    /// <returns>誤り無く読めた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>解析は失敗しても例外を投げない。</b>
    /// 誤りは診断として溜まり、読めなかった部分は <see cref="IncompleteDeclarationSyntax"/> になる。
    /// そのため、診断の有無と進んだ量の両方を見る。
    /// </remarks>
    private static bool TryParseOne(
        ImmutableArray<HlslSyntaxToken> tokens,
        bool asStatement,
        out int consumed)
    {
        HlslParser parser = new(tokens);
        HlslSyntaxNode unit = asStatement ? parser.ParseStatement() : parser.ParseDeclaration();

        consumed = parser._index;

        if (parser._diagnostics.Count > 0 || consumed == 0 || unit is IncompleteDeclarationSyntax)
        {
            consumed = 0;
            return false;
        }

        return true;
    }

    private HlslSyntaxToken Current => Peek(0);

    private bool AtEnd => _index >= _tokens.Length || Current.Kind == HlslSyntaxKind.EndOfFileToken;

    /// <summary>
    /// ファイル全体を解析する。
    /// </summary>
    /// <param name="diagnostics">解析中に検出した構文エラー。</param>
    /// <returns>構文木の根。</returns>
    public HlslCompilationUnitSyntax ParseCompilationUnit(out ImmutableArray<Diagnostic> diagnostics)
    {
        ImmutableArray<HlslDeclarationSyntax>.Builder declarations =
            ImmutableArray.CreateBuilder<HlslDeclarationSyntax>();

        while (!AtEnd)
        {
            int positionBefore = _index;
            declarations.Add(ParseDeclaration());

            // 解析が 1 トークンも進まなかった場合は無限ループになる。
            // 文法の想定外に落ちた時の最後の防壁。
            if (_index == positionBefore)
            {
                declarations.Add(new IncompleteDeclarationSyntax([Advance()]));
            }
        }

        diagnostics = _diagnostics.ToImmutable();
        return new HlslCompilationUnitSyntax(declarations.ToImmutable(), Current)
        {
            TypeAliases = _typeAliases.ToFrozenDictionary(StringComparer.Ordinal),
        };
    }

    // --------------------------------------------------------------------
    // 宣言
    // --------------------------------------------------------------------

    /// <summary>
    /// トップレベルまたはブロック内の宣言を 1 つ解析する。
    /// </summary>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// 文法:
    /// <c>Declaration := Attribute* Modifier* (StructDeclaration | ConstantBuffer | Typedef | FunctionOrVariable)</c>
    /// </remarks>
    private HlslDeclarationSyntax ParseDeclaration()
    {
        if (Current.Kind == HlslSyntaxKind.SemicolonToken)
        {
            // 余分なセミコロン。誤りではないので、報告せずに読み飛ばす。
            return new IncompleteDeclarationSyntax([Advance()]);
        }

        ImmutableArray<HlslAttributeSyntax> attributes = ParseAttributes();

        // HLSL 2021 の template と enum は解釈しない。
        // 読めるふりをすると、template は構文の誤りとして、
        // enum は「列挙子がどこにも宣言されていない」として報告することになる。
        if (CurrentTextIs("enum"))
        {
            return ParseUnsupportedTypeDeclaration(endsWithSemicolon: true);
        }

        // template が導く宣言は、関数なら波括弧で、変数ならセミコロンで終わる。
        if (CurrentTextIs("template"))
        {
            return ParseUnsupportedTypeDeclaration(endsWithSemicolon: false);
        }

        if (CurrentTextIs("typedef"))
        {
            return ParseTypedef();
        }

        if (CurrentTextIs("cbuffer") || CurrentTextIs("tbuffer"))
        {
            return ParseConstantBuffer();
        }

        if (CurrentTextIs("namespace"))
        {
            return ParseNamespace();
        }

        ImmutableArray<HlslSyntaxToken> modifiers = ParseModifiers();

        if (CurrentTextIs("class") || CurrentTextIs("interface"))
        {
            return ParseUnsupportedTypeDeclaration(endsWithSemicolon: true);
        }

        if (CurrentTextIs("struct"))
        {
            return ParseStruct();
        }

        return ParseFunctionOrVariable(attributes, modifiers);
    }

    /// <summary>修飾子の並びを読み取る。</summary>
    /// <returns>修飾子のトークン。</returns>
    private ImmutableArray<HlslSyntaxToken> ParseModifiers()
    {
        ImmutableArray<HlslSyntaxToken>.Builder modifiers = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        while (Current.Kind == HlslSyntaxKind.IdentifierToken
            && HlslKeywords.DeclarationModifiers.Contains(Current.Text))
        {
            modifiers.Add(Advance());
        }

        return modifiers.ToImmutable();
    }

    /// <summary>
    /// この解析ツールが解釈しない宣言 (<c>class</c> / <c>interface</c> / <c>enum</c> / <c>template</c>) を読み飛ばす。
    /// </summary>
    /// <param name="endsWithSemicolon">
    /// 宣言がセミコロンで終わるかどうか。型宣言は <c>class Foo { ... } bar;</c> のように
    /// 波括弧の後ろに宣言子が続きうるので真にする。
    /// <c>template</c> が導くのは関数か変数で、関数は波括弧で終わるため偽にする。
    /// 真のまま読み飛ばすと、次のセミコロンまで、つまり後ろの宣言を丸ごと捨ててしまう。
    /// </param>
    /// <returns>読み飛ばしたトークンを保持するノード。</returns>
    /// <remarks>
    /// <para>
    /// <b>何も伝えずに読み飛ばしてはならない。</b>
    /// この経路が無かったとき、<c>interface IBase { ... };</c> は
    /// <see cref="ParseFunctionOrVariable"/> に落ちて
    /// 「<c>interface</c> 型の変数 <c>IBase</c>」として解析され、
    /// 波括弧の中身は <see cref="ParseDeclaratorRest"/> の
    /// <c>sampler_state { ... }</c> を読み飛ばす経路が捨てていた。
    /// 診断は 1 件も出ず、中のメソッドもフィールドも無かったことになる。
    /// </para>
    /// <para>
    /// 報告は <c>HL0003</c> で行う。これはブロックの診断として残るため、
    /// <c>ShaderCompilation.HasCompleteDependencies</c> が偽になり、
    /// 「無いこと」を根拠とするルールはこのブロックで報告しない。
    /// </para>
    /// <para>
    /// <b>読み飛ばしは宣言の終わりまで行う。</b>
    /// 中身を変数宣言として取り込むと、存在しない uniform を一覧に載せることになる。
    /// <c>class Foo { ... } bar;</c> の <c>bar</c> も取り込まない。
    /// このブロックでは「宣言が見つからない」を根拠にする検査が働かないため、
    /// 取りこぼしても誤検出にはならない。
    /// </para>
    /// </remarks>
    private IncompleteDeclarationSyntax ParseUnsupportedTypeDeclaration(bool endsWithSemicolon)
    {
        HlslSyntaxToken keyword = Current;

        string description = keyword.Text == "operator"
            ? "operator による演算子の多重定義"
            : $"{keyword.Text} 宣言";

        _diagnostics.Add(Diagnostic.Create(
            HlslDescriptors.UnsupportedDeclaration, keyword.GetLocation(), description));

        ImmutableArray<HlslSyntaxToken>.Builder skipped = ImmutableArray.CreateBuilder<HlslSyntaxToken>();
        skipped.Add(Advance());

        // 波括弧に入るまで (名前と継承指定) を読む。
        while (!AtEnd
               && Current.Kind != HlslSyntaxKind.OpenBraceToken
               && Current.Kind != HlslSyntaxKind.SemicolonToken)
        {
            skipped.Add(Advance());
        }

        if (Current.Kind == HlslSyntaxKind.OpenBraceToken)
        {
            int depth = 0;

            do
            {
                if (Current.Kind == HlslSyntaxKind.OpenBraceToken)
                {
                    depth++;
                }
                else if (Current.Kind == HlslSyntaxKind.CloseBraceToken)
                {
                    depth--;
                }

                skipped.Add(Advance());
            }
            while (!AtEnd && depth > 0);
        }

        // 波括弧の後ろに続く宣言子と、閉じるセミコロン。
        while (endsWithSemicolon && !AtEnd && Current.Kind != HlslSyntaxKind.SemicolonToken)
        {
            skipped.Add(Advance());
        }

        if (Current.Kind == HlslSyntaxKind.SemicolonToken)
        {
            skipped.Add(Advance());
        }

        return new IncompleteDeclarationSyntax(skipped.ToImmutable());
    }

    /// <summary>
    /// 関数宣言か変数宣言かを判別して解析する。
    /// </summary>
    /// <param name="attributes">先に読み取った属性。</param>
    /// <param name="modifiers">先に読み取った修飾子。</param>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// 型と名前を読んだあとの記号で判別する。
    /// 開き括弧が続けば関数、それ以外なら変数である。
    /// </remarks>
    private HlslDeclarationSyntax ParseFunctionOrVariable(
        ImmutableArray<HlslAttributeSyntax> attributes,
        ImmutableArray<HlslSyntaxToken> modifiers)
    {
        if (Current.Kind != HlslSyntaxKind.IdentifierToken)
        {
            return RecoverDeclaration($"型名が必要ですが '{DescribeCurrent()}' がありました。");
        }

        HlslTypeSyntax type = ParseType();

        // operator による演算子の多重定義 (S operator+(S a, S b) { ... }) は Unity では使えない。
        if (CurrentTextIs("operator"))
        {
            return ParseUnsupportedTypeDeclaration(endsWithSemicolon: false);
        }

        if (Current.Kind != HlslSyntaxKind.IdentifierToken)
        {
            return RecoverDeclaration($"宣言する名前が必要ですが '{DescribeCurrent()}' がありました。");
        }

        HlslSyntaxToken name = Advance();

        // 構造体の外でのメソッドの定義: uint Wave::GetIndex() { ... }
        // 修飾の後ろは関数名で、必ず関数の宣言になる。
        if (Current.Kind == HlslSyntaxKind.ColonColonToken && Peek(1).Kind == HlslSyntaxKind.IdentifierToken)
        {
            ImmutableArray<HlslSyntaxToken>.Builder qualifiers = ImmutableArray.CreateBuilder<HlslSyntaxToken>();
            qualifiers.Add(name);

            while (Current.Kind == HlslSyntaxKind.ColonColonToken && Peek(1).Kind == HlslSyntaxKind.IdentifierToken)
            {
                qualifiers.Add(Advance());
                name = Advance();

                if (Current.Kind == HlslSyntaxKind.ColonColonToken)
                {
                    qualifiers.Add(name);
                }
            }

            if (Current.Kind == HlslSyntaxKind.OpenParenToken)
            {
                return ParseFunctionRest(attributes, modifiers, type, name, qualifiers.ToImmutable());
            }

            return RecoverDeclaration($"'(' が必要ですが '{DescribeCurrent()}' がありました。");
        }

        return Current.Kind == HlslSyntaxKind.OpenParenToken
            ? ParseFunctionRest(attributes, modifiers, type, name)
            : ParseVariableRest(modifiers, type, name);
    }

    /// <summary>関数宣言の残りの部分を解析する。</summary>
    /// <param name="attributes">属性。</param>
    /// <param name="modifiers">修飾子。</param>
    /// <param name="returnType">戻り値の型。</param>
    /// <param name="name">関数名。</param>
    /// <param name="qualifiers">構造体の外でのメソッドの定義なら、関数名の前の修飾 (<c>Wave</c> と <c>::</c>)。</param>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// 文法:
    /// <c>Function := Type (Identifier '::')* Identifier '(' ParameterList ')' Semantic* (Block | ';')</c>
    /// </remarks>
    private FunctionDeclarationSyntax ParseFunctionRest(
        ImmutableArray<HlslAttributeSyntax> attributes,
        ImmutableArray<HlslSyntaxToken> modifiers,
        HlslTypeSyntax returnType,
        HlslSyntaxToken name,
        ImmutableArray<HlslSyntaxToken> qualifiers = default)
    {
        HlslSyntaxToken openParen = Advance();
        ImmutableArray<HlslNodeOrTokenEntry>.Builder parameters =
            ImmutableArray.CreateBuilder<HlslNodeOrTokenEntry>();

        while (!AtEnd && Current.Kind != HlslSyntaxKind.CloseParenToken)
        {
            int positionBefore = _index;
            parameters.Add(HlslNodeOrTokenEntry.FromNode(ParseParameter()));

            if (Current.Kind == HlslSyntaxKind.CommaToken)
            {
                parameters.Add(HlslNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            if (_index == positionBefore)
            {
                parameters.Add(HlslNodeOrTokenEntry.FromToken(Advance()));
            }

            if (Current.Kind != HlslSyntaxKind.CloseParenToken && Current.Kind != HlslSyntaxKind.CommaToken)
            {
                break;
            }
        }

        HlslSyntaxToken closeParen = Expect(HlslSyntaxKind.CloseParenToken, "')'");
        ImmutableArray<SemanticSyntax> semantics = ParseSemantics();

        if (Current.Kind == HlslSyntaxKind.OpenBraceToken)
        {
            BlockStatementSyntax body = IsFromOwnSource(Current) ? ParseBlock() : SkipBlock();
            return new FunctionDeclarationSyntax(
                attributes, modifiers, returnType, name, openParen,
                parameters.ToImmutable(), closeParen, semantics, body, null, qualifiers);
        }

        HlslSyntaxToken semicolon = Expect(HlslSyntaxKind.SemicolonToken, "';'");
        return new FunctionDeclarationSyntax(
            attributes, modifiers, returnType, name, openParen,
            parameters.ToImmutable(), closeParen, semantics, null, semicolon, qualifiers);
    }

    /// <summary>関数の仮引数を 1 つ解析する。</summary>
    /// <returns>解析した仮引数。</returns>
    private ParameterSyntax ParseParameter()
    {
        ImmutableArray<HlslSyntaxToken> modifiers = ParseModifiers();
        HlslTypeSyntax type = ParseType();

        HlslSyntaxToken name = Current.Kind == HlslSyntaxKind.IdentifierToken
            ? Advance()
            : CreateMissingToken(HlslSyntaxKind.IdentifierToken);

        ImmutableArray<HlslSyntaxToken> arrayRanks = ParseArrayRanks();
        ImmutableArray<SemanticSyntax> semantics = ParseSemantics();

        HlslSyntaxToken? equals = null;
        HlslExpressionSyntax? defaultValue = null;

        if (Current.Kind == HlslSyntaxKind.EqualsToken)
        {
            equals = Advance();
            defaultValue = ParseExpression();
        }

        return new ParameterSyntax(modifiers, type, name, arrayRanks, semantics, equals, defaultValue);
    }

    /// <summary>変数宣言の残りの部分を解析する。</summary>
    /// <param name="modifiers">修飾子。</param>
    /// <param name="type">型。</param>
    /// <param name="firstName">最初の変数名。</param>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// 文法: <c>Variable := Type Declarator (',' Declarator)* ';'</c>
    /// </remarks>
    private VariableDeclarationSyntax ParseVariableRest(
        ImmutableArray<HlslSyntaxToken> modifiers,
        HlslTypeSyntax type,
        HlslSyntaxToken firstName)
    {
        ImmutableArray<HlslNodeOrTokenEntry>.Builder declarators =
            ImmutableArray.CreateBuilder<HlslNodeOrTokenEntry>();

        declarators.Add(HlslNodeOrTokenEntry.FromNode(ParseDeclaratorRest(firstName)));

        while (Current.Kind == HlslSyntaxKind.CommaToken)
        {
            declarators.Add(HlslNodeOrTokenEntry.FromToken(Advance()));

            HlslSyntaxToken name = Current.Kind == HlslSyntaxKind.IdentifierToken
                ? Advance()
                : CreateMissingToken(HlslSyntaxKind.IdentifierToken);

            declarators.Add(HlslNodeOrTokenEntry.FromNode(ParseDeclaratorRest(name)));
        }

        HlslSyntaxToken semicolon = Expect(HlslSyntaxKind.SemicolonToken, "';'");
        return new VariableDeclarationSyntax(modifiers, type, declarators.ToImmutable(), semicolon);
    }

    /// <summary>変数名より後の、配列・セマンティクス・初期化子を解析する。</summary>
    /// <param name="name">変数名。</param>
    /// <returns>解析した宣言子。</returns>
    private VariableDeclaratorSyntax ParseDeclaratorRest(HlslSyntaxToken name)
    {
        ImmutableArray<HlslSyntaxToken> arrayRanks = ParseArrayRanks();
        ImmutableArray<SemanticSyntax> semantics = ParseSemantics();

        HlslSyntaxToken? equals = null;
        HlslExpressionSyntax? initializer = null;

        if (Current.Kind == HlslSyntaxKind.EqualsToken)
        {
            equals = Advance();
            initializer = ParseInitializer();
        }

        // sampler_state { ... } のようなサンプラ状態の指定。
        // 中身は Cg 時代の記法であり構文木にする価値が無いため、括弧ごと読み飛ばす。
        if (Current.Kind == HlslSyntaxKind.OpenBraceToken)
        {
            SkipBalancedBraces();
        }

        return new VariableDeclaratorSyntax(name, arrayRanks, semantics, equals, initializer);
    }

    /// <summary>初期化子を解析する。</summary>
    /// <returns>解析した式。</returns>
    private HlslExpressionSyntax ParseInitializer()
        => Current.Kind == HlslSyntaxKind.OpenBraceToken ? ParseInitializerList() : ParseExpression();

    /// <summary>波括弧による初期化子を解析する。</summary>
    /// <returns>解析した式。</returns>
    private InitializerListExpressionSyntax ParseInitializerList()
    {
        HlslSyntaxToken openBrace = Advance();
        ImmutableArray<HlslNodeOrTokenEntry>.Builder elements =
            ImmutableArray.CreateBuilder<HlslNodeOrTokenEntry>();

        while (!AtEnd && Current.Kind != HlslSyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            elements.Add(HlslNodeOrTokenEntry.FromNode(ParseInitializer()));

            if (Current.Kind == HlslSyntaxKind.CommaToken)
            {
                elements.Add(HlslNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            if (_index == positionBefore)
            {
                elements.Add(HlslNodeOrTokenEntry.FromToken(Advance()));
            }
        }

        HlslSyntaxToken closeBrace = Expect(HlslSyntaxKind.CloseBraceToken, "'}'");
        return new InitializerListExpressionSyntax(openBrace, elements.ToImmutable(), closeBrace);
    }

    /// <summary>構造体宣言を解析する。</summary>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// 文法: <c>Struct := 'struct' Identifier? '{' Declaration* '}' Declarator* ';'</c>
    /// 無名構造体と、宣言に続けて変数を宣言する形の両方に対応する。
    /// </remarks>
    private StructDeclarationSyntax ParseStruct()
    {
        HlslSyntaxToken keyword = Advance();

        HlslSyntaxToken? name = Current.Kind == HlslSyntaxKind.IdentifierToken ? Advance() : null;

        // struct の継承指定 (: BaseType) は読み飛ばす。
        if (Current.Kind == HlslSyntaxKind.ColonToken)
        {
            Advance();

            if (Current.Kind == HlslSyntaxKind.IdentifierToken)
            {
                Advance();
            }
        }

        HlslSyntaxToken openBrace = Expect(HlslSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<HlslDeclarationSyntax>.Builder members =
            ImmutableArray.CreateBuilder<HlslDeclarationSyntax>();

        while (!AtEnd && Current.Kind != HlslSyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            members.Add(ParseDeclaration());

            if (_index == positionBefore)
            {
                members.Add(new IncompleteDeclarationSyntax([Advance()]));
            }
        }

        HlslSyntaxToken closeBrace = Expect(HlslSyntaxKind.CloseBraceToken, "'}'");

        ImmutableArray<HlslNodeOrTokenEntry>.Builder declarators =
            ImmutableArray.CreateBuilder<HlslNodeOrTokenEntry>();

        while (Current.Kind == HlslSyntaxKind.IdentifierToken)
        {
            declarators.Add(HlslNodeOrTokenEntry.FromNode(ParseDeclaratorRest(Advance())));

            if (Current.Kind != HlslSyntaxKind.CommaToken)
            {
                break;
            }

            declarators.Add(HlslNodeOrTokenEntry.FromToken(Advance()));
        }

        HlslSyntaxToken semicolon = Expect(HlslSyntaxKind.SemicolonToken, "';'");

        return new StructDeclarationSyntax(
            keyword, name, openBrace, members.ToImmutable(), closeBrace, declarators.ToImmutable(), semicolon);
    }

    /// <summary>定数バッファの宣言を解析する。</summary>
    /// <returns>解析した宣言。</returns>
    private ConstantBufferDeclarationSyntax ParseConstantBuffer()
    {
        HlslSyntaxToken keyword = Advance();
        HlslSyntaxToken? name = Current.Kind == HlslSyntaxKind.IdentifierToken ? Advance() : null;
        ImmutableArray<SemanticSyntax> semantics = ParseSemantics();

        HlslSyntaxToken openBrace = Expect(HlslSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<HlslDeclarationSyntax>.Builder members =
            ImmutableArray.CreateBuilder<HlslDeclarationSyntax>();

        while (!AtEnd && Current.Kind != HlslSyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            members.Add(ParseDeclaration());

            if (_index == positionBefore)
            {
                members.Add(new IncompleteDeclarationSyntax([Advance()]));
            }
        }

        HlslSyntaxToken closeBrace = Expect(HlslSyntaxKind.CloseBraceToken, "'}'");

        // cbuffer のセミコロンは省略できる。
        HlslSyntaxToken? semicolon = Current.Kind == HlslSyntaxKind.SemicolonToken ? Advance() : null;

        return new ConstantBufferDeclarationSyntax(
            keyword, name, semantics, openBrace, members.ToImmutable(), closeBrace, semicolon);
    }

    /// <summary><c>namespace</c> の宣言を解析する。</summary>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// 文法: <c>Namespace := 'namespace' Identifier? '{' Declaration* '}'</c>
    /// 構造体や定数バッファと違い、終端のセミコロンは付かない。
    /// </remarks>
    private NamespaceDeclarationSyntax ParseNamespace()
    {
        HlslSyntaxToken keyword = Advance();
        HlslSyntaxToken? name = Current.Kind == HlslSyntaxKind.IdentifierToken ? Advance() : null;
        HlslSyntaxToken openBrace = Expect(HlslSyntaxKind.OpenBraceToken, "'{'");

        ImmutableArray<HlslDeclarationSyntax>.Builder members =
            ImmutableArray.CreateBuilder<HlslDeclarationSyntax>();

        while (!AtEnd && Current.Kind != HlslSyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            members.Add(ParseDeclaration());

            if (_index == positionBefore)
            {
                members.Add(new IncompleteDeclarationSyntax([Advance()]));
            }
        }

        HlslSyntaxToken closeBrace = Expect(HlslSyntaxKind.CloseBraceToken, "'}'");
        return new NamespaceDeclarationSyntax(keyword, name, openBrace, members.ToImmutable(), closeBrace);
    }

    /// <summary><c>typedef</c> を解析する。</summary>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// <para>
    /// <b>別名を覚え、以降の型の記述では元の型の名前として扱う。</b>
    /// <c>typedef float3 V3;</c> の後の <c>V3 v;</c> は <c>float3 v;</c> と同じ型であり、
    /// 型の分類も成分の数も <c>float3</c> として判定できる。
    /// 覚えるのは <c>typedef 型 別名;</c> の形だけにする。配列 (<c>typedef float A[4];</c>) などは、
    /// 別名を型として扱わないだけで、今までどおり分からない型になる。
    /// </para>
    /// </remarks>
    private TypedefDeclarationSyntax ParseTypedef()
    {
        HlslSyntaxToken keyword = Advance();
        int start = _index;
        ImmutableArray<HlslSyntaxToken>.Builder tokens = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        HlslTypeSyntax? target = Current.Kind == HlslSyntaxKind.IdentifierToken ? ParseType() : null;

        if (target is not null
            && Current.Kind == HlslSyntaxKind.IdentifierToken
            && Peek(1).Kind == HlslSyntaxKind.SemicolonToken
            && !target.NameToken.IsMissing)
        {
            _typeAliases[Current.Text] = target.Name;
        }

        for (int i = start; i < _index; i++)
        {
            tokens.Add(_tokens[i]);
        }

        while (!AtEnd && Current.Kind != HlslSyntaxKind.SemicolonToken)
        {
            tokens.Add(Advance());
        }

        HlslSyntaxToken semicolon = Expect(HlslSyntaxKind.SemicolonToken, "';'");
        return new TypedefDeclarationSyntax(keyword, tokens.ToImmutable(), semicolon);
    }

    /// <summary>組み込み型か、組み込み型への typedef の別名かを判定する。</summary>
    /// <param name="name">判定する名前。</param>
    /// <returns>確実に型なら <see langword="true"/>。</returns>
    private bool IsKnownTypeName(string name)
        => HlslKeywords.IsKnownTypeName(name)
           || (_typeAliases.TryGetValue(name, out string? target) && HlslKeywords.IsKnownTypeName(target));

    // --------------------------------------------------------------------
    // 型・セマンティクス・属性
    // --------------------------------------------------------------------

    /// <summary>型を解析する。</summary>
    /// <returns>解析した型。</returns>
    /// <remarks>
    /// 文法: <c>Type := (Identifier '::')* Identifier ('&lt;' Token* '&gt;')?</c>
    /// 名前空間の修飾に対応するのは、Unity のレイトレーシング関連のコードが
    /// <c>UnifiedRT::InstanceData</c> のような修飾名を使うためである。
    /// </remarks>
    private HlslTypeSyntax ParseType()
    {
        // 型名の前に置かれた修飾子を先に取る。
        // (const int) や (unsigned int) の形で型変換の中にも現れる。
        // 次も識別子であることを条件にするのは、
        // 修飾子と同じ綴りの型名だけが書かれている場合に食べ過ぎないためである。
        ImmutableArray<HlslSyntaxToken>.Builder modifiers = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        while (Current.Kind == HlslSyntaxKind.IdentifierToken
               && HlslKeywords.TypePrefixModifiers.Contains(Current.Text)
               && Peek(1).Kind == HlslSyntaxKind.IdentifierToken)
        {
            modifiers.Add(Advance());
        }

        HlslSyntaxToken name = Current.Kind == HlslSyntaxKind.IdentifierToken
            ? Advance()
            : CreateMissingToken(HlslSyntaxKind.IdentifierToken);

        ImmutableArray<HlslSyntaxToken>.Builder qualifiers = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        while (Current.Kind == HlslSyntaxKind.ColonColonToken)
        {
            qualifiers.Add(name);
            qualifiers.Add(Advance());

            name = Current.Kind == HlslSyntaxKind.IdentifierToken
                ? Advance()
                : CreateMissingToken(HlslSyntaxKind.IdentifierToken);
        }

        HlslSyntaxToken? openAngle = null;
        HlslSyntaxToken? closeAngle = null;
        ImmutableArray<HlslSyntaxToken>.Builder templateArguments =
            ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        if (Current.Kind == HlslSyntaxKind.LessThanToken)
        {
            openAngle = Advance();
            int depth = 0;

            while (!AtEnd)
            {
                if (Current.Kind == HlslSyntaxKind.GreaterThanToken && depth == 0)
                {
                    break;
                }

                if (Current.Kind == HlslSyntaxKind.LessThanToken)
                {
                    depth++;
                }
                else if (Current.Kind == HlslSyntaxKind.GreaterThanToken)
                {
                    depth--;
                }

                // セミコロンや波括弧に達したら、山括弧ではなく比較演算子だったとみなす。
                // ここで止めないと、閉じられない山括弧がファイル全体を飲み込む。
                if (Current.Kind is HlslSyntaxKind.SemicolonToken or HlslSyntaxKind.OpenBraceToken)
                {
                    break;
                }

                templateArguments.Add(Advance());
            }

            closeAngle = Current.Kind == HlslSyntaxKind.GreaterThanToken
                ? Advance()
                : CreateMissingToken(HlslSyntaxKind.GreaterThanToken);
        }

        // typedef の別名は、元の型の名前として扱う。
        string? aliasOf = openAngle is null && qualifiers.Count == 0 && _typeAliases.TryGetValue(name.Text, out string? target)
            ? target
            : null;

        return new HlslTypeSyntax(
            modifiers.ToImmutable(),
            name,
            qualifiers.ToImmutable(),
            openAngle,
            templateArguments.ToImmutable(),
            closeAngle,
            aliasOf);
    }

    /// <summary>配列の次元指定を読み取る。</summary>
    /// <returns>角括弧と大きさの式を含むトークン列。</returns>
    private ImmutableArray<HlslSyntaxToken> ParseArrayRanks()
    {
        ImmutableArray<HlslSyntaxToken>.Builder tokens = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

        while (Current.Kind == HlslSyntaxKind.OpenBracketToken)
        {
            int depth = 0;

            do
            {
                if (Current.Kind == HlslSyntaxKind.OpenBracketToken)
                {
                    depth++;
                }
                else if (Current.Kind == HlslSyntaxKind.CloseBracketToken)
                {
                    depth--;
                }

                tokens.Add(Advance());
            }
            while (!AtEnd && depth > 0);
        }

        return tokens.ToImmutable();
    }

    /// <summary>セマンティクスやレジスタ指定の並びを解析する。</summary>
    /// <returns>解析したセマンティクス。</returns>
    /// <remarks>
    /// コロンの後ろが数値なら、C のビットフィールド (<c>uint a : 8;</c>) を書いたものとして
    /// その数値の位置で報告する。HLSL にビットフィールドは無い。
    /// コロンをそのまま残すと、コロンを「余計なもの」として 1 行に 2 件報告することになり、
    /// しかも本当におかしい数値ではなく、正しい記法であるコロンを指してしまう。
    /// </remarks>
    private ImmutableArray<SemanticSyntax> ParseSemantics()
    {
        ImmutableArray<SemanticSyntax>.Builder semantics = ImmutableArray.CreateBuilder<SemanticSyntax>();

        // 構造体のビットフィールド (uint a : 4;) は Unity では使えない。
        if (Current.Kind == HlslSyntaxKind.ColonToken
            && Peek(1).Kind == HlslSyntaxKind.NumericLiteralToken)
        {
            // Unity も数値を指して拒む ("unexpected integer constant")。コロン自体はセマンティクスの記法として正しい。
            Advance();
            HlslSyntaxToken width = Advance();
            _diagnostics.Add(Diagnostic.Create(
                HlslDescriptors.UnsupportedDeclaration, width.GetLocation(), "ビットフィールド"));
        }

        while (Current.Kind == HlslSyntaxKind.ColonToken
            && Peek(1).Kind == HlslSyntaxKind.IdentifierToken)
        {
            HlslSyntaxToken colon = Advance();
            HlslSyntaxToken name = Advance();

            HlslSyntaxToken? openParen = null;
            HlslSyntaxToken? closeParen = null;
            ImmutableArray<HlslSyntaxToken>.Builder arguments = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

            if (Current.Kind == HlslSyntaxKind.OpenParenToken)
            {
                openParen = Advance();

                // 引数の中にも括弧が来る。
                // [numthreads(GROUP_SIZE, GROUP_SIZE, 1)] の GROUP_SIZE が
                // (TILE_SIZE / 2) のようなマクロだと、展開後は括弧を含む式になる。
                // 深さを数えないと、内側の閉じ括弧で属性が終わったと読んでしまう。
                int depth = 0;

                while (!AtEnd && (depth > 0 || Current.Kind != HlslSyntaxKind.CloseParenToken))
                {
                    if (Current.Kind == HlslSyntaxKind.OpenParenToken)
                    {
                        depth++;
                    }
                    else if (Current.Kind == HlslSyntaxKind.CloseParenToken)
                    {
                        depth--;
                    }

                    arguments.Add(Advance());
                }

                closeParen = Expect(HlslSyntaxKind.CloseParenToken, "')'");
            }

            semantics.Add(new SemanticSyntax(colon, name, openParen, arguments.ToImmutable(), closeParen));
        }

        return semantics.ToImmutable();
    }

    /// <summary>属性の並びを解析する。</summary>
    /// <returns>解析した属性。</returns>
    private ImmutableArray<HlslAttributeSyntax> ParseAttributes()
    {
        ImmutableArray<HlslAttributeSyntax>.Builder attributes =
            ImmutableArray.CreateBuilder<HlslAttributeSyntax>();

        while (Current.Kind == HlslSyntaxKind.OpenBracketToken)
        {
            // [[vk::binding(0, 0)]] のような二重角括弧の注釈は読み飛ばす。
            // SPIR-V 向けの割り当てを指示するもので、この解析ツールが見る意味を持たない。
            if (Peek(1).Kind == HlslSyntaxKind.OpenBracketToken)
            {
                SkipDoubleBracketAnnotation();
                continue;
            }

            HlslSyntaxToken openBracket = Advance();

            if (Current.Kind != HlslSyntaxKind.IdentifierToken)
            {
                // 属性ではなかった。読み過ぎた開き角括弧を戻す。
                _index--;
                break;
            }

            HlslSyntaxToken name = Advance();

            HlslSyntaxToken? openParen = null;
            HlslSyntaxToken? closeParen = null;
            ImmutableArray<HlslSyntaxToken>.Builder arguments = ImmutableArray.CreateBuilder<HlslSyntaxToken>();

            if (Current.Kind == HlslSyntaxKind.OpenParenToken)
            {
                openParen = Advance();

                // 引数の中にも括弧が来る。
                // [numthreads(GROUP_SIZE, GROUP_SIZE, 1)] の GROUP_SIZE が
                // (TILE_SIZE / 2) のようなマクロだと、展開後は括弧を含む式になる。
                // 深さを数えないと、内側の閉じ括弧で属性が終わったと読んでしまう。
                int depth = 0;

                while (!AtEnd && (depth > 0 || Current.Kind != HlslSyntaxKind.CloseParenToken))
                {
                    if (Current.Kind == HlslSyntaxKind.OpenParenToken)
                    {
                        depth++;
                    }
                    else if (Current.Kind == HlslSyntaxKind.CloseParenToken)
                    {
                        depth--;
                    }

                    arguments.Add(Advance());
                }

                closeParen = Expect(HlslSyntaxKind.CloseParenToken, "')'");
            }

            HlslSyntaxToken closeBracket = Expect(HlslSyntaxKind.CloseBracketToken, "']'");
            attributes.Add(new HlslAttributeSyntax(
                openBracket, name, openParen, arguments.ToImmutable(), closeParen, closeBracket));
        }

        return attributes.ToImmutable();
    }

    /// <summary>
    /// <c>[[vk::binding(0, 0)]]</c> のような二重角括弧の注釈を読み飛ばす。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>報告しない。</b> この注釈が指示するのはレジスタやディスクリプタの割り当てで、
    /// この解析ツールはそれを検査しない
    /// (<c>docs/rules/README.md</c> の「報告しないもの」)。
    /// 読み飛ばしても、見送る検査は 1 つも無い。
    /// </para>
    /// <para>
    /// 読み飛ばさないと、後ろの宣言が丸ごと <c>HL0001</c> になる。
    /// <c>[[vk::binding(0, 0)]] Texture2D _Tex;</c> の <c>_Tex</c> が
    /// 宣言として拾われず、使っている場所が「宣言されていない」ことになっていた。
    /// </para>
    /// </remarks>
    private void SkipDoubleBracketAnnotation()
    {
        int depth = 0;

        do
        {
            if (Current.Kind == HlslSyntaxKind.OpenBracketToken)
            {
                depth++;
            }
            else if (Current.Kind == HlslSyntaxKind.CloseBracketToken)
            {
                depth--;
            }

            Advance();
        }
        while (!AtEnd && depth > 0);
    }

    // --------------------------------------------------------------------
    // 文
    // --------------------------------------------------------------------

    /// <summary>波括弧で囲まれた文の並びを解析する。</summary>
    /// <returns>解析したブロック。</returns>
    private BlockStatementSyntax ParseBlock()
    {
        HlslSyntaxToken openBrace = Expect(HlslSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<HlslStatementSyntax>.Builder statements =
            ImmutableArray.CreateBuilder<HlslStatementSyntax>();

        while (!AtEnd && Current.Kind != HlslSyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            statements.Add(ParseStatement());

            if (_index == positionBefore)
            {
                statements.Add(new IncompleteStatementSyntax([Advance()]));
            }
        }

        HlslSyntaxToken closeBrace = Expect(HlslSyntaxKind.CloseBraceToken, "'}'");
        return new BlockStatementSyntax(openBrace, statements.ToImmutable(), closeBrace);
    }

    /// <summary>
    /// そのトークンが、利用者のファイル (解析しているファイルと利用者のヘッダ) のものかを判定する。
    /// </summary>
    /// <param name="token">対象のトークン。</param>
    /// <returns>利用者のものであれば <see langword="true"/>。</returns>
    /// <remarks>
    /// 自身のテキストを渡されていない場合 (ヘッダ単体の解析など) は、
    /// すべてを自身のものとして扱う。読み飛ばさない側へ倒す。
    /// </remarks>
    private bool IsFromOwnSource(HlslSyntaxToken token)
    {
        if (_ownSource is null || ReferenceEquals(token.Source, _ownSource))
        {
            return true;
        }

        if (_isUserInclude is null)
        {
            return false;
        }

        if (!_ownSources.TryGetValue(token.Source, out bool own))
        {
            own = _isUserInclude(token.Source.FilePath);
            _ownSources[token.Source] = own;
        }

        return own;
    }

    /// <summary>
    /// 波括弧の対応だけを取って、中の文を作らずに読み飛ばす。
    /// </summary>
    /// <returns>中身が空のブロック。波括弧は本物である。</returns>
    /// <remarks>
    /// <b>ノードを作らないことが目的である。</b>
    /// 取り込んだヘッダの関数の中身は、どのルールも見ていない。
    /// 作れば構文解析でも親の設定でも走査でも費用がかかる。
    /// </remarks>
    private BlockStatementSyntax SkipBlock()
    {
        HlslSyntaxToken openBrace = Expect(HlslSyntaxKind.OpenBraceToken, "'{'");
        int depth = 1;

        while (!AtEnd && depth > 0)
        {
            if (Current.Kind == HlslSyntaxKind.OpenBraceToken)
            {
                depth++;
            }
            else if (Current.Kind == HlslSyntaxKind.CloseBraceToken)
            {
                depth--;

                if (depth == 0)
                {
                    break;
                }
            }

            Advance();
        }

        HlslSyntaxToken closeBrace = Expect(HlslSyntaxKind.CloseBraceToken, "'}'");
        return new BlockStatementSyntax(openBrace, [], closeBrace);
    }

    /// <summary>文を 1 つ解析する。</summary>
    /// <returns>解析した文。</returns>
    private HlslStatementSyntax ParseStatement()
    {
        ImmutableArray<HlslAttributeSyntax> attributes = ParseAttributes();

        switch (Current.Kind)
        {
            case HlslSyntaxKind.OpenBraceToken:
                return ParseBlock();

            case HlslSyntaxKind.SemicolonToken:
                return new EmptyStatementSyntax(Advance());
        }

        if (Current.Kind == HlslSyntaxKind.IdentifierToken)
        {
            switch (Current.Text)
            {
                case "if": return ParseIf(attributes);
                case "for": return ParseFor(attributes);
                case "while": return ParseWhile(attributes);
                case "do": return ParseDoWhile(attributes);
                case "switch": return ParseSwitch(attributes);
                case "return": return ParseReturn();
                case "break":
                case "continue":
                case "discard":
                    return new JumpStatementSyntax(Advance(), Expect(HlslSyntaxKind.SemicolonToken, "';'"));
                case "case":
                case "default":
                    return ParseSwitchLabel();
            }
        }

        if (LooksLikeDeclaration())
        {
            return new LocalDeclarationStatementSyntax(ParseDeclaration());
        }

        HlslExpressionSyntax expression = ParseExpression();
        HlslSyntaxToken semicolon = Expect(HlslSyntaxKind.SemicolonToken, "';'");
        return new ExpressionStatementSyntax(expression, semicolon);
    }

    /// <summary>
    /// 現在位置が宣言の始まりに見えるかを判定する。
    /// </summary>
    /// <returns>宣言に見える場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <c>float4 x;</c> と <c>foo(x);</c> はどちらも識別子で始まるため、
    /// 先読みで区別する必要がある。判定の根拠は次のとおり。
    /// </para>
    /// <list type="bullet">
    ///   <item><description>修飾子・<c>struct</c> で始まる → 宣言</description></item>
    ///   <item><description>組み込み型名で始まる → 宣言</description></item>
    ///   <item><description><c>識別子 識別子</c> の並び → 宣言 (利用者定義の型)</description></item>
    ///   <item><description><c>識別子 &lt; ... &gt; 識別子</c> の並び → 宣言 (テンプレート型)</description></item>
    /// </list>
    /// <para>
    /// いずれにも当てはまらなければ式として扱う。
    /// 利用者定義型を使った宣言のうち <c>MyType x;</c> の形はここで拾えるが、
    /// より複雑な形は式と誤判定しうる。その場合も構文エラーとして報告されるだけで、
    /// 解析全体は継続する。
    /// </para>
    /// </remarks>
    private bool LooksLikeDeclaration()
    {
        if (Current.Kind != HlslSyntaxKind.IdentifierToken)
        {
            return false;
        }

        if (HlslKeywords.DeclarationModifiers.Contains(Current.Text) || Current.Text == "struct")
        {
            return true;
        }

        if (IsKnownTypeName(Current.Text))
        {
            return true;
        }

        if (Peek(1).Kind == HlslSyntaxKind.IdentifierToken)
        {
            return true;
        }

        // 名前空間で修飾された型 (UnifiedRT::InstanceData x) を探す。
        if (Peek(1).Kind == HlslSyntaxKind.ColonColonToken)
        {
            int offset = 1;

            while (Peek(offset).Kind == HlslSyntaxKind.ColonColonToken
                && Peek(offset + 1).Kind == HlslSyntaxKind.IdentifierToken)
            {
                offset += 2;
            }

            return Peek(offset).Kind == HlslSyntaxKind.IdentifierToken;
        }

        // 識別子 < ... > 識別子 の形 (テンプレート型の宣言) を探す。
        if (Peek(1).Kind == HlslSyntaxKind.LessThanToken)
        {
            int depth = 0;

            for (int offset = 1; offset < 32; offset++)
            {
                HlslSyntaxKind kind = Peek(offset).Kind;

                if (kind == HlslSyntaxKind.LessThanToken)
                {
                    depth++;
                }
                else if (kind == HlslSyntaxKind.GreaterThanToken)
                {
                    depth--;

                    if (depth == 0)
                    {
                        return Peek(offset + 1).Kind == HlslSyntaxKind.IdentifierToken;
                    }
                }
                else if (kind is HlslSyntaxKind.SemicolonToken or HlslSyntaxKind.OpenBraceToken
                    or HlslSyntaxKind.EndOfFileToken)
                {
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary><c>if</c> 文を解析する。</summary>
    /// <param name="attributes">属性。</param>
    /// <returns>解析した文。</returns>
    private IfStatementSyntax ParseIf(ImmutableArray<HlslAttributeSyntax> attributes)
    {
        HlslSyntaxToken keyword = Advance();
        HlslSyntaxToken openParen = Expect(HlslSyntaxKind.OpenParenToken, "'('");
        HlslExpressionSyntax condition = ParseExpression();
        HlslSyntaxToken closeParen = Expect(HlslSyntaxKind.CloseParenToken, "')'");
        HlslStatementSyntax thenStatement = ParseStatement();

        HlslSyntaxToken? elseKeyword = null;
        HlslStatementSyntax? elseStatement = null;

        if (CurrentTextIs("else"))
        {
            elseKeyword = Advance();
            elseStatement = ParseStatement();
        }

        return new IfStatementSyntax(
            attributes, keyword, openParen, condition, closeParen, thenStatement, elseKeyword, elseStatement);
    }

    /// <summary><c>for</c> 文を解析する。</summary>
    /// <param name="attributes">属性。</param>
    /// <returns>解析した文。</returns>
    private ForStatementSyntax ParseFor(ImmutableArray<HlslAttributeSyntax> attributes)
    {
        HlslSyntaxToken keyword = Advance();
        HlslSyntaxToken openParen = Expect(HlslSyntaxKind.OpenParenToken, "'('");

        HlslSyntaxNode? initializer = null;
        HlslSyntaxToken? firstSemicolon = null;

        if (Current.Kind == HlslSyntaxKind.SemicolonToken)
        {
            firstSemicolon = Advance();
        }
        else if (LooksLikeDeclaration())
        {
            // 宣言はセミコロンまでを含んで解析される。
            initializer = ParseDeclaration();
        }
        else
        {
            initializer = ParseExpression();
            firstSemicolon = Expect(HlslSyntaxKind.SemicolonToken, "';'");
        }

        HlslExpressionSyntax? condition = Current.Kind == HlslSyntaxKind.SemicolonToken
            ? null
            : ParseExpression();

        HlslSyntaxToken secondSemicolon = Expect(HlslSyntaxKind.SemicolonToken, "';'");

        ImmutableArray<HlslNodeOrTokenEntry>.Builder incrementors =
            ImmutableArray.CreateBuilder<HlslNodeOrTokenEntry>();

        while (!AtEnd && Current.Kind != HlslSyntaxKind.CloseParenToken)
        {
            int positionBefore = _index;
            incrementors.Add(HlslNodeOrTokenEntry.FromNode(ParseExpression()));

            if (Current.Kind == HlslSyntaxKind.CommaToken)
            {
                incrementors.Add(HlslNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            if (_index == positionBefore)
            {
                incrementors.Add(HlslNodeOrTokenEntry.FromToken(Advance()));
            }
            else
            {
                break;
            }
        }

        HlslSyntaxToken closeParen = Expect(HlslSyntaxKind.CloseParenToken, "')'");
        HlslStatementSyntax body = ParseStatement();

        return new ForStatementSyntax(
            attributes, keyword, openParen, initializer, firstSemicolon,
            condition, secondSemicolon, incrementors.ToImmutable(), closeParen, body);
    }

    /// <summary><c>while</c> 文を解析する。</summary>
    /// <param name="attributes">属性。</param>
    /// <returns>解析した文。</returns>
    private WhileStatementSyntax ParseWhile(ImmutableArray<HlslAttributeSyntax> attributes)
    {
        HlslSyntaxToken keyword = Advance();
        HlslSyntaxToken openParen = Expect(HlslSyntaxKind.OpenParenToken, "'('");
        HlslExpressionSyntax condition = ParseExpression();
        HlslSyntaxToken closeParen = Expect(HlslSyntaxKind.CloseParenToken, "')'");
        HlslStatementSyntax body = ParseStatement();

        return new WhileStatementSyntax(attributes, keyword, openParen, condition, closeParen, body);
    }

    /// <summary><c>do while</c> 文を解析する。</summary>
    /// <param name="attributes">属性。</param>
    /// <returns>解析した文。</returns>
    private DoWhileStatementSyntax ParseDoWhile(ImmutableArray<HlslAttributeSyntax> attributes)
    {
        HlslSyntaxToken doKeyword = Advance();
        HlslStatementSyntax body = ParseStatement();
        HlslSyntaxToken whileKeyword = CurrentTextIs("while")
            ? Advance()
            : CreateMissingToken(HlslSyntaxKind.IdentifierToken);
        HlslSyntaxToken openParen = Expect(HlslSyntaxKind.OpenParenToken, "'('");
        HlslExpressionSyntax condition = ParseExpression();
        HlslSyntaxToken closeParen = Expect(HlslSyntaxKind.CloseParenToken, "')'");
        HlslSyntaxToken semicolon = Expect(HlslSyntaxKind.SemicolonToken, "';'");

        return new DoWhileStatementSyntax(
            attributes, doKeyword, body, whileKeyword, openParen, condition, closeParen, semicolon);
    }

    /// <summary><c>switch</c> 文を解析する。</summary>
    /// <param name="attributes">属性。</param>
    /// <returns>解析した文。</returns>
    private SwitchStatementSyntax ParseSwitch(ImmutableArray<HlslAttributeSyntax> attributes)
    {
        HlslSyntaxToken keyword = Advance();
        HlslSyntaxToken openParen = Expect(HlslSyntaxKind.OpenParenToken, "'('");
        HlslExpressionSyntax expression = ParseExpression();
        HlslSyntaxToken closeParen = Expect(HlslSyntaxKind.CloseParenToken, "')'");
        HlslSyntaxToken openBrace = Expect(HlslSyntaxKind.OpenBraceToken, "'{'");

        ImmutableArray<HlslStatementSyntax>.Builder statements =
            ImmutableArray.CreateBuilder<HlslStatementSyntax>();

        while (!AtEnd && Current.Kind != HlslSyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            statements.Add(ParseStatement());

            if (_index == positionBefore)
            {
                statements.Add(new IncompleteStatementSyntax([Advance()]));
            }
        }

        HlslSyntaxToken closeBrace = Expect(HlslSyntaxKind.CloseBraceToken, "'}'");

        return new SwitchStatementSyntax(
            attributes, keyword, openParen, expression, closeParen, openBrace, statements.ToImmutable(), closeBrace);
    }

    /// <summary><c>case</c> / <c>default</c> ラベルを解析する。</summary>
    /// <returns>解析した文。</returns>
    private SwitchLabelStatementSyntax ParseSwitchLabel()
    {
        HlslSyntaxToken keyword = Advance();
        HlslExpressionSyntax? value = keyword.TextIs("case") ? ParseExpression() : null;
        HlslSyntaxToken colon = Expect(HlslSyntaxKind.ColonToken, "':'");
        return new SwitchLabelStatementSyntax(keyword, value, colon);
    }

    /// <summary><c>return</c> 文を解析する。</summary>
    /// <returns>解析した文。</returns>
    private ReturnStatementSyntax ParseReturn()
    {
        HlslSyntaxToken keyword = Advance();
        HlslExpressionSyntax? expression = Current.Kind == HlslSyntaxKind.SemicolonToken
            ? null
            : ParseExpression();
        HlslSyntaxToken semicolon = Expect(HlslSyntaxKind.SemicolonToken, "';'");
        return new ReturnStatementSyntax(keyword, expression, semicolon);
    }

    // --------------------------------------------------------------------
    // 式
    // --------------------------------------------------------------------

    /// <summary>式を解析する。</summary>
    /// <returns>解析した式。</returns>
    private HlslExpressionSyntax ParseExpression() => ParseAssignment();

    /// <summary>代入式を解析する。</summary>
    /// <returns>解析した式。</returns>
    /// <remarks>代入は右結合であるため、右辺を再帰的に解析する。</remarks>
    private HlslExpressionSyntax ParseAssignment()
    {
        HlslExpressionSyntax left = ParseConditional();

        if (!IsAssignmentOperator(Current.Kind))
        {
            return left;
        }

        HlslSyntaxToken operatorToken = Advance();
        HlslExpressionSyntax right = ParseAssignment();
        return new AssignmentExpressionSyntax(left, operatorToken, right);
    }

    /// <summary>三項条件式を解析する。</summary>
    /// <returns>解析した式。</returns>
    private HlslExpressionSyntax ParseConditional()
    {
        HlslExpressionSyntax condition = ParseBinary(0);

        if (Current.Kind != HlslSyntaxKind.QuestionToken)
        {
            return condition;
        }

        HlslSyntaxToken question = Advance();
        HlslExpressionSyntax whenTrue = ParseAssignment();
        HlslSyntaxToken colon = Expect(HlslSyntaxKind.ColonToken, "':'");
        HlslExpressionSyntax whenFalse = ParseAssignment();

        return new ConditionalExpressionSyntax(condition, question, whenTrue, colon, whenFalse);
    }

    /// <summary>優先順位つきで二項演算式を解析する。</summary>
    /// <param name="minimumPrecedence">この優先順位以上の演算子だけを処理する。</param>
    /// <returns>解析した式。</returns>
    private HlslExpressionSyntax ParseBinary(int minimumPrecedence)
    {
        HlslExpressionSyntax left = ParseUnary();
        int operands = 1;

        while (!AtEnd)
        {
            int precedence = GetBinaryPrecedence(Current.Kind);
            if (precedence < 0 || precedence < minimumPrecedence)
            {
                break;
            }

            // 長さに上限を置く。二項演算の連なりは左へ伸びる木になり、
            // その木を歩く処理は深さだけ再帰する。
            // マクロを重ねれば 1 行から何十万項でも作れるため、
            // 上限が無いとスタックを使い切ってプロセスごと落ちる。
            if (++operands > MaxBinaryOperands)
            {
                ReportError($"1 つの式に演算子を {MaxBinaryOperands} 個以上つなげることはできません。");
                break;
            }

            HlslSyntaxToken operatorToken = Advance();
            HlslExpressionSyntax right = ParseBinary(precedence + 1);
            left = new BinaryExpressionSyntax(left, operatorToken, right);
        }

        return left;
    }

    /// <summary>単項式を解析する。</summary>
    /// <returns>解析した式。</returns>
    private HlslExpressionSyntax ParseUnary()
    {
        if (Current.Kind is HlslSyntaxKind.PlusToken or HlslSyntaxKind.MinusToken
            or HlslSyntaxKind.ExclamationToken or HlslSyntaxKind.TildeToken
            or HlslSyntaxKind.PlusPlusToken or HlslSyntaxKind.MinusMinusToken)
        {
            HlslSyntaxToken operatorToken = Advance();
            return new PrefixUnaryExpressionSyntax(operatorToken, ParseUnary());
        }

        if (Current.Kind == HlslSyntaxKind.OpenParenToken && LooksLikeCast())
        {
            HlslSyntaxToken openParen = Advance();
            HlslTypeSyntax type = ParseType();
            ParseArrayRanks();
            HlslSyntaxToken closeParen = Expect(HlslSyntaxKind.CloseParenToken, "')'");
            return new CastExpressionSyntax(openParen, type, closeParen, ParseUnary());
        }

        return ParsePostfix(ParsePrimary());
    }

    /// <summary>
    /// 開き括弧から始まる式が型変換かどうかを判定する。
    /// </summary>
    /// <returns>型変換に見える場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <c>(float4)x</c> は型変換、<c>(a)*b</c> は括弧つきの式と乗算である。
    /// 構文だけでは区別できず、型の情報が必要になる。
    /// </para>
    /// <para>
    /// 判定は 2 段構えにしている。
    /// 括弧の中が組み込み型なら型変換とみなす。
    /// 利用者定義の型名の場合は、閉じ括弧の次が
    /// <b>識別子・リテラル・開き括弧のいずれか</b>のときだけ型変換とみなす。
    /// <c>-</c> や <c>+</c> を含めていないのは、<c>(a) - b</c> という減算のほうが
    /// <c>(MyType)-x</c> という型変換より圧倒的に多いためである。
    /// </para>
    /// </remarks>
    private bool LooksLikeCast()
    {
        if (Peek(1).Kind != HlslSyntaxKind.IdentifierToken)
        {
            return false;
        }

        // 括弧の中を読み飛ばして閉じ括弧を探す。
        int offset = 1;

        // (const int) や (unsigned int) の形では、型名の前に修飾子が並ぶ。
        // 組み込み型かどうかは、修飾子を越えた先の名前で判定する。
        while (HlslKeywords.TypePrefixModifiers.Contains(Peek(offset).Text)
               && Peek(offset + 1).Kind == HlslSyntaxKind.IdentifierToken)
        {
            offset++;
        }

        bool isKnownType = IsKnownTypeName(Peek(offset).Text);
        int angleDepth = 0;

        while (offset < 32)
        {
            HlslSyntaxKind kind = Peek(offset).Kind;

            if (kind == HlslSyntaxKind.LessThanToken)
            {
                angleDepth++;
            }
            else if (kind == HlslSyntaxKind.GreaterThanToken)
            {
                angleDepth--;
            }
            else if (kind == HlslSyntaxKind.CloseParenToken && angleDepth == 0)
            {
                break;
            }
            else if (kind is HlslSyntaxKind.SemicolonToken or HlslSyntaxKind.EndOfFileToken
                or HlslSyntaxKind.OpenBraceToken or HlslSyntaxKind.CommaToken)
            {
                return false;
            }
            else if (kind is not (HlslSyntaxKind.IdentifierToken or HlslSyntaxKind.OpenBracketToken
                or HlslSyntaxKind.CloseBracketToken or HlslSyntaxKind.NumericLiteralToken))
            {
                // 型の記述に現れない記号があれば型変換ではない。
                return false;
            }

            offset++;
        }

        if (Peek(offset).Kind != HlslSyntaxKind.CloseParenToken)
        {
            return false;
        }

        HlslSyntaxKind following = Peek(offset + 1).Kind;

        if (isKnownType)
        {
            // 組み込み型なら、後ろが式を始めうる形かどうかだけを見る。
            return following is HlslSyntaxKind.IdentifierToken or HlslSyntaxKind.NumericLiteralToken
                or HlslSyntaxKind.OpenParenToken or HlslSyntaxKind.MinusToken
                or HlslSyntaxKind.PlusToken or HlslSyntaxKind.ExclamationToken
                or HlslSyntaxKind.TildeToken;
        }

        return following is HlslSyntaxKind.IdentifierToken or HlslSyntaxKind.NumericLiteralToken
            or HlslSyntaxKind.OpenParenToken;
    }

    /// <summary>後置演算 (呼び出し・添字・メンバーアクセス・増減) を解析する。</summary>
    /// <param name="expression">対象の式。</param>
    /// <returns>解析した式。</returns>
    private HlslExpressionSyntax ParsePostfix(HlslExpressionSyntax expression)
    {
        while (!AtEnd)
        {
            switch (Current.Kind)
            {
                case HlslSyntaxKind.OpenParenToken:
                    expression = ParseInvocation(expression);
                    break;

                case HlslSyntaxKind.OpenBracketToken:
                {
                    HlslSyntaxToken openBracket = Advance();
                    HlslExpressionSyntax index = ParseExpression();
                    HlslSyntaxToken closeBracket = Expect(HlslSyntaxKind.CloseBracketToken, "']'");
                    expression = new ElementAccessExpressionSyntax(expression, openBracket, index, closeBracket);
                    break;
                }

                case HlslSyntaxKind.DotToken:

                // 名前空間で修飾された関数呼び出し (UnifiedRT::TraceRay(...)) に対応する。
                // 構文の形はメンバーアクセスと同じなので同じノードで表す。
                case HlslSyntaxKind.ColonColonToken:
                {
                    HlslSyntaxToken separator = Advance();
                    HlslSyntaxToken name = Current.Kind == HlslSyntaxKind.IdentifierToken
                        ? Advance()
                        : CreateMissingToken(HlslSyntaxKind.IdentifierToken);
                    expression = new MemberAccessExpressionSyntax(expression, separator, name);
                    break;
                }

                case HlslSyntaxKind.PlusPlusToken:
                case HlslSyntaxKind.MinusMinusToken:
                    expression = new PostfixUnaryExpressionSyntax(expression, Advance());
                    break;

                default:
                    return expression;
            }
        }

        return expression;
    }

    /// <summary>関数呼び出しを解析する。</summary>
    /// <param name="target">呼び出す対象。</param>
    /// <returns>解析した式。</returns>
    private InvocationExpressionSyntax ParseInvocation(HlslExpressionSyntax target)
    {
        HlslSyntaxToken openParen = Advance();
        ImmutableArray<HlslNodeOrTokenEntry>.Builder arguments =
            ImmutableArray.CreateBuilder<HlslNodeOrTokenEntry>();

        while (!AtEnd && Current.Kind != HlslSyntaxKind.CloseParenToken)
        {
            int positionBefore = _index;
            arguments.Add(HlslNodeOrTokenEntry.FromNode(ParseAssignment()));

            if (Current.Kind == HlslSyntaxKind.CommaToken)
            {
                arguments.Add(HlslNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            if (_index == positionBefore)
            {
                arguments.Add(HlslNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            break;
        }

        HlslSyntaxToken closeParen = Expect(HlslSyntaxKind.CloseParenToken, "')'");
        return new InvocationExpressionSyntax(target, openParen, arguments.ToImmutable(), closeParen);
    }

    /// <summary>基本要素を解析する。</summary>
    /// <returns>解析した式。</returns>
    private HlslExpressionSyntax ParsePrimary()
    {
        switch (Current.Kind)
        {
            case HlslSyntaxKind.NumericLiteralToken:
            case HlslSyntaxKind.StringLiteralToken:
            case HlslSyntaxKind.CharacterLiteralToken:
                return new LiteralExpressionSyntax(Advance());

            case HlslSyntaxKind.IdentifierToken:
                return new IdentifierExpressionSyntax(Advance());

            case HlslSyntaxKind.OpenParenToken:
            {
                HlslSyntaxToken openParen = Advance();
                HlslExpressionSyntax inner = ParseExpression();
                HlslSyntaxToken closeParen = Expect(HlslSyntaxKind.CloseParenToken, "')'");
                return new ParenthesizedExpressionSyntax(openParen, inner, closeParen);
            }

            case HlslSyntaxKind.OpenBraceToken:
                return ParseInitializerList();

            default:
                ReportError($"式が必要ですが '{DescribeCurrent()}' がありました。");
                return new IncompleteExpressionSyntax([AdvanceIfPossible()]);
        }
    }

    /// <summary>代入演算子かどうかを判定する。</summary>
    /// <param name="kind">判定するトークン種別。</param>
    /// <returns>代入演算子の場合は <see langword="true"/>。</returns>
    private static bool IsAssignmentOperator(HlslSyntaxKind kind) => HlslSyntaxFacts.IsAssignmentOperator(kind);

    /// <summary>二項演算子の優先順位を返す。値が大きいほど強く結合する。</summary>
    /// <param name="kind">演算子の種別。</param>
    /// <returns>優先順位。二項演算子でない場合は負の値。</returns>
    private static int GetBinaryPrecedence(HlslSyntaxKind kind) => kind switch
    {
        HlslSyntaxKind.BarBarToken => 1,
        HlslSyntaxKind.AmpersandAmpersandToken => 2,
        HlslSyntaxKind.BarToken => 3,
        HlslSyntaxKind.CaretToken => 4,
        HlslSyntaxKind.AmpersandToken => 5,
        HlslSyntaxKind.EqualsEqualsToken or HlslSyntaxKind.ExclamationEqualsToken => 6,
        HlslSyntaxKind.LessThanToken or HlslSyntaxKind.GreaterThanToken
            or HlslSyntaxKind.LessThanEqualsToken or HlslSyntaxKind.GreaterThanEqualsToken => 7,
        HlslSyntaxKind.LessThanLessThanToken or HlslSyntaxKind.GreaterThanGreaterThanToken => 8,
        HlslSyntaxKind.PlusToken or HlslSyntaxKind.MinusToken => 9,
        HlslSyntaxKind.AsteriskToken or HlslSyntaxKind.SlashToken or HlslSyntaxKind.PercentToken => 10,
        _ => -1,
    };

    // --------------------------------------------------------------------
    // 補助とエラー回復
    // --------------------------------------------------------------------

    /// <summary>
    /// 宣言として解釈できない区間を、次の宣言が始まりそうな位置まで読み飛ばす。
    /// </summary>
    /// <param name="message">報告するエラーの内容。</param>
    /// <returns>読み飛ばしたトークンを保持するノード。</returns>
    /// <remarks>
    /// 同期点はセミコロンと波括弧である。
    /// 宣言はこのいずれかで終わるため、そこまで捨てれば次の宣言から解析を再開できる。
    /// </remarks>
    private IncompleteDeclarationSyntax RecoverDeclaration(string message)
    {
        ReportError(message);

        ImmutableArray<HlslSyntaxToken>.Builder skipped = ImmutableArray.CreateBuilder<HlslSyntaxToken>();
        int braceDepth = 0;

        while (!AtEnd)
        {
            if (Current.Kind == HlslSyntaxKind.OpenBraceToken)
            {
                braceDepth++;
            }
            else if (Current.Kind == HlslSyntaxKind.CloseBraceToken)
            {
                if (braceDepth == 0)
                {
                    break;
                }

                braceDepth--;
                skipped.Add(Advance());

                if (braceDepth == 0)
                {
                    break;
                }

                continue;
            }
            else if (Current.Kind == HlslSyntaxKind.SemicolonToken && braceDepth == 0)
            {
                skipped.Add(Advance());
                break;
            }

            skipped.Add(Advance());
        }

        if (skipped.Count == 0 && !AtEnd)
        {
            skipped.Add(Advance());
        }

        return new IncompleteDeclarationSyntax(skipped.ToImmutable());
    }

    /// <summary>対応の取れた波括弧の組を読み飛ばす。</summary>
    private void SkipBalancedBraces()
    {
        int depth = 0;

        do
        {
            if (Current.Kind == HlslSyntaxKind.OpenBraceToken)
            {
                depth++;
            }
            else if (Current.Kind == HlslSyntaxKind.CloseBraceToken)
            {
                depth--;
            }

            Advance();
        }
        while (!AtEnd && depth > 0);
    }

    /// <summary>現在のトークンを返して 1 つ進む。</summary>
    /// <returns>進む前のトークン。</returns>
    private HlslSyntaxToken Advance()
    {
        HlslSyntaxToken current = Current;

        if (_index < _tokens.Length)
        {
            _index++;
        }

        return current;
    }

    /// <summary>終端でなければ 1 つ進む。</summary>
    /// <returns>進む前のトークン。</returns>
    private HlslSyntaxToken AdvanceIfPossible() => AtEnd ? Current : Advance();

    /// <summary>指定した相対位置のトークンを取得する。</summary>
    /// <param name="offset">現在位置からの相対オフセット。</param>
    /// <returns>その位置のトークン。範囲外の場合は終端トークン。</returns>
    private HlslSyntaxToken Peek(int offset)
    {
        int index = _index + offset;

        if (index < _tokens.Length)
        {
            return _tokens[index];
        }

        return _tokens.Length > 0
            ? CreateEndOfFileToken(_tokens[^1])
            : CreateEndOfFileToken(null);
    }

    /// <summary>現在のトークンが指定した文字列かどうかを判定する。</summary>
    /// <param name="text">比較する文字列。</param>
    /// <returns>一致する場合は <see langword="true"/>。</returns>
    private bool CurrentTextIs(string text)
        => Current.Kind == HlslSyntaxKind.IdentifierToken && Current.TextIs(text);

    /// <summary>
    /// 期待する種別のトークンを消費する。無い場合は欠落トークンを合成する。
    /// </summary>
    /// <param name="kind">期待するトークン種別。</param>
    /// <param name="expectation">エラーメッセージに出す「何が必要だったか」。</param>
    /// <returns>消費したトークン、または合成した欠落トークン。</returns>
    /// <remarks>
    /// 期待外れの場合にトークンを消費しない。
    /// 消費してしまうと、本来そのトークンで始まるはずだった後続の構文まで巻き添えで壊れる。
    /// </remarks>
    private HlslSyntaxToken Expect(HlslSyntaxKind kind, string expectation)
    {
        if (Current.Kind == kind)
        {
            return Advance();
        }

        ReportError($"{expectation} が必要ですが '{DescribeCurrent()}' がありました。");
        return CreateMissingToken(kind);
    }

    /// <summary>現在のトークンを説明する文字列を返す。</summary>
    /// <returns>エラーメッセージに埋め込む文字列。</returns>
    private string DescribeCurrent()
        => Current.Kind == HlslSyntaxKind.EndOfFileToken ? "ファイルの終わり" : Current.Text;

    /// <summary>欠落トークンを合成する。</summary>
    /// <param name="kind">合成するトークンの種別。</param>
    /// <returns>合成したトークン。</returns>
    private HlslSyntaxToken CreateMissingToken(HlslSyntaxKind kind)
        => new(kind, Current.Source, new TextSpan(Current.Span.Start, 0), string.Empty, isMissing: true);

    /// <summary>終端トークンを合成する。</summary>
    /// <param name="last">直前のトークン。位置の基準に使う。</param>
    /// <returns>合成したトークン。</returns>
    private static HlslSyntaxToken CreateEndOfFileToken(HlslSyntaxToken? last)
        => last is not null
            ? new HlslSyntaxToken(HlslSyntaxKind.EndOfFileToken, last.Source,
                new TextSpan(last.Span.End, 0), string.Empty)
            : new HlslSyntaxToken(HlslSyntaxKind.EndOfFileToken,
                SourceText.From(string.Empty, "<空>"), new TextSpan(0, 0), string.Empty);

    /// <summary>
    /// 構文エラーを記録する。
    /// </summary>
    /// <param name="message">エラーの内容。</param>
    /// <remarks>
    /// 報告数に上限を設けている。マクロが期待どおり展開されないと
    /// 1 ファイルから数千件のエラーが出ることがあり、
    /// そのすべてを報告しても読み手の役に立たないためである。
    /// </remarks>
    private void ReportError(string message)
    {
        if (_reportedErrors >= MaxReportedErrors)
        {
            return;
        }

        _reportedErrors++;
        _diagnostics.Add(Diagnostic.Create(
            HlslDescriptors.SyntaxError, Current.GetLocation(), message));
    }
}
