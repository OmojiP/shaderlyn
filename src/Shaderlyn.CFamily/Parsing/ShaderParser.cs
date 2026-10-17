using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.CFamily.Parsing;

/// <summary>
/// プリプロセス済みの C 系の言語のトークン列を構文木へ組み立てる共通の構文解析器 (再帰下降パーサ)。
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
/// <para>
/// <b>シェーダー言語ごとの違いは、派生した型が答える。</b>
/// 組み込みの型の名前、修飾子、使える構文 (enum・template など)、その言語にだけある宣言 (HLSL の cbuffer など) である。
/// </para>
/// </remarks>
internal abstract class ShaderParser
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

    private readonly ImmutableArray<ShaderSyntaxToken> _tokens;
    private readonly SourceText? _ownSource;
    private readonly Func<string, bool>? _isUserInclude;
    private readonly Dictionary<SourceText, bool> _ownSources = [];
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

    /// <summary>ここまでに読んだ typedef の、別名から元の型の名前への対応。</summary>
    private readonly Dictionary<string, string> _typeAliases = new(StringComparer.Ordinal);
    private int _index;
    private int _reportedErrors;

    /// <summary>最後に構文エラーを報告した位置のトークン。同じ位置で重ねて報告しないために使う。</summary>
    private ShaderSyntaxToken? _lastErrorToken;

    /// <summary>
    /// パーサを生成する。
    /// </summary>
    /// <param name="tokens">プリプロセス済みのトークン列。</param>
    protected ShaderParser(ImmutableArray<ShaderSyntaxToken> tokens)
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
    protected ShaderParser(ImmutableArray<ShaderSyntaxToken> tokens, SourceText? ownSource, Func<string, bool>? isUserInclude = null)
        : this(tokens)
    {
        _ownSource = ownSource;
        _isUserInclude = isUserInclude;
    }

    // --------------------------------------------------------------------
    // 言語ごとの違い
    // --------------------------------------------------------------------

    /// <summary>使えない構文の報告 (HL0003) に入れる、どこで使えないかを伝える語 (<c>FXC</c> / <c>Unity</c>)。</summary>
    protected abstract string UnavailableSubject { get; }

    /// <summary><c>enum</c> を読むか。読まなければ、報告して読み飛ばす。</summary>
    protected abstract bool AcceptsEnums { get; }

    /// <summary><c>template</c> を読むか。読まなければ、報告して読み飛ばす。</summary>
    protected abstract bool AcceptsTemplates { get; }

    /// <summary>演算子の多重定義を読むか。</summary>
    protected abstract bool AcceptsOperatorOverloading { get; }

    /// <summary>ビットフィールドを読むか。</summary>
    protected abstract bool AcceptsBitfields { get; }

    /// <summary>
    /// 型の側の配列 (GLSL の <c>vec4[3] positions</c>) と、配列のコンストラクタ (<c>float[](0.1, 0.2)</c>) を読むか。既定は読まない。
    /// </summary>
    protected virtual bool AcceptsArrayTypes => false;

    /// <summary>組み込みの型の名前かを判定する。</summary>
    /// <param name="name">判定する名前。</param>
    /// <returns>組み込みの型なら <see langword="true"/>。</returns>
    protected abstract bool IsBuiltInTypeName(string name);

    /// <summary>宣言の前に置ける修飾子 (<c>static</c>・<c>uniform</c> など) かを判定する。</summary>
    /// <param name="text">判定する語。</param>
    /// <returns>修飾子なら <see langword="true"/>。</returns>
    protected abstract bool IsDeclarationModifier(string text);

    /// <summary>型名の前に置ける修飾子 (<c>const</c>・<c>unsigned</c> など) かを判定する。</summary>
    /// <param name="text">判定する語。</param>
    /// <returns>修飾子なら <see langword="true"/>。</returns>
    protected abstract bool IsTypePrefixModifier(string text);

    /// <summary>仮引数の前にだけ置ける修飾子 (HLSL のメッシュシェーダーの <c>vertices</c> など) かを判定する。</summary>
    /// <param name="text">判定する語。</param>
    /// <returns>修飾子なら <see langword="true"/>。</returns>
    protected abstract bool IsParameterModifier(string text);

    /// <summary>
    /// 同じ型の別の書き方を、短い名前にする (<see cref="TypeSyntax.CanonicalName"/>)。
    /// </summary>
    /// <param name="name">型名のトークン。</param>
    /// <param name="openAngle">テンプレート引数の開き山括弧。無い場合は <see langword="null"/>。</param>
    /// <param name="templateArguments">テンプレート引数のトークン。カンマを含む。</param>
    /// <returns>短い名前。当てはまらなければ <see langword="null"/>。</returns>
    protected virtual string? GetCanonicalTypeName(
        ShaderSyntaxToken name,
        ShaderSyntaxToken? openAngle,
        ImmutableArray<ShaderSyntaxToken> templateArguments)
        => null;

    /// <summary>
    /// 1 語ではない修飾 (GLSL の <c>layout(location = 0)</c> など) を、今の位置から読む。
    /// </summary>
    /// <param name="modifiers">読んだトークンを足す先。</param>
    /// <returns>読んだら <see langword="true"/>。今の位置がそうでなければ <see langword="false"/> (何も読まない)。</returns>
    /// <remarks>宣言の前の修飾子の並びの中で、1 語の修飾子 (<see cref="IsDeclarationModifier"/>) と交互に尋ねる。</remarks>
    protected virtual bool TryParseModifierGroup(ImmutableArray<ShaderSyntaxToken>.Builder modifiers) => false;

    /// <summary>
    /// その言語にだけある宣言 (HLSL の <c>cbuffer</c> など) を読む。
    /// </summary>
    /// <returns>読んだ宣言。今の位置がそうでなければ <see langword="null"/> (何も読まない)。</returns>
    /// <remarks>属性・<c>enum</c>・<c>template</c>・<c>typedef</c>・<c>static_assert</c> の後、修飾子の前に尋ねる。</remarks>
    protected virtual DeclarationSyntax? TryParseLanguageDeclaration() => null;

    /// <summary>
    /// 構文木にせずに読み飛ばした宣言に現れた識別子。<see cref="ParseCompilationUnit"/> のあとで揃う。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>報告せずに読み飛ばした宣言のためにある。</b>
    /// HLSL 2021 の <c>class</c> や <c>template</c> の明示的な特殊化は構文木にしないので、その名前はどこにも宣言されていないことになる。
    /// 呼び出しを「宣言されていない関数」と報告すると、正しいコードを誤りとして指摘することになる。
    /// </para>
    /// <para>
    /// 読み飛ばした <c>#if</c> の分岐と同じく、解析していない場所に現れた名前として扱う。
    /// 報告して読み飛ばした宣言 (どのコンパイラと HLSL のバージョンでも使えない構文) では、ブロックの解析そのものが不完全になるので集めない。
    /// </para>
    /// </remarks>
    public ImmutableArray<ShaderSyntaxToken> UnanalyzedIdentifiers => [.. _unanalyzedIdentifiers];

    private readonly List<ShaderSyntaxToken> _unanalyzedIdentifiers = [];

    /// <summary>
    /// そのトークン列が、文・宣言として過不足なく閉じているかを判定する。
    /// </summary>
    /// <param name="tokens">判定するトークン列。</param>
    /// <param name="create">トークン列を読むパーサを作る (シェーダー言語ごとの派生した型)。</param>
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
    protected static bool IsCompleteUnits(
        ImmutableArray<ShaderSyntaxToken> tokens,
        Func<ImmutableArray<ShaderSyntaxToken>, ShaderParser> create)
    {
        if (tokens.IsDefaultOrEmpty)
        {
            return true;
        }

        int consumed = 0;

        while (consumed < tokens.Length)
        {
            if (!TryParseSingleUnit(tokens[consumed..], create, out int length))
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
    /// <param name="create">トークン列を読むパーサを作る (シェーダー言語ごとの派生した型)。</param>
    /// <param name="consumed">読んだトークンの数。読めなかった場合は 0。</param>
    /// <returns>誤り無く読めた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>文として読めなければ宣言として読み直す。</b>
    /// 関数の中なら文、ファイルの直下なら宣言だが、
    /// どちらの位置のトークン列かは呼び出し側にも分からないことがある。
    /// </remarks>
    protected static bool TryParseSingleUnit(
        ImmutableArray<ShaderSyntaxToken> tokens,
        Func<ImmutableArray<ShaderSyntaxToken>, ShaderParser> create,
        out int consumed)
    {
        if (TryParseOne(tokens, create, asStatement: true, out consumed))
        {
            return true;
        }

        return TryParseOne(tokens, create, asStatement: false, out consumed);
    }

    /// <summary>1 つ分を読んでみる。</summary>
    /// <param name="tokens">読むトークン列。</param>
    /// <param name="create">トークン列を読むパーサを作る (シェーダー言語ごとの派生した型)。</param>
    /// <param name="asStatement">文として読むなら <see langword="true"/>、宣言なら <see langword="false"/>。</param>
    /// <param name="consumed">読んだトークンの数。</param>
    /// <returns>誤り無く読めた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>解析は失敗しても例外を投げない。</b>
    /// 誤りは診断として溜まり、読めなかった部分は <see cref="IncompleteDeclarationSyntax"/> になる。
    /// そのため、診断の有無と進んだ量の両方を見る。
    /// </remarks>
    private static bool TryParseOne(
        ImmutableArray<ShaderSyntaxToken> tokens,
        Func<ImmutableArray<ShaderSyntaxToken>, ShaderParser> create,
        bool asStatement,
        out int consumed)
    {
        ShaderParser parser = create(tokens);
        ShaderSyntaxNode unit = asStatement ? parser.ParseStatement() : parser.ParseDeclaration();

        consumed = parser._index;

        if (parser._diagnostics.Count > 0 || consumed == 0 || unit is IncompleteDeclarationSyntax)
        {
            consumed = 0;
            return false;
        }

        return true;
    }

    protected ShaderSyntaxToken Current => Peek(0);

    protected bool AtEnd => _index >= _tokens.Length || Current.Kind == ShaderSyntaxKind.EndOfFileToken;

    /// <summary>
    /// ファイル全体を解析する。
    /// </summary>
    /// <param name="diagnostics">解析中に検出した構文エラー。</param>
    /// <returns>構文木の根。</returns>
    public CompilationUnitSyntax ParseCompilationUnit(out ImmutableArray<Diagnostic> diagnostics)
    {
        ImmutableArray<DeclarationSyntax>.Builder declarations =
            ImmutableArray.CreateBuilder<DeclarationSyntax>();

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
        return new CompilationUnitSyntax(declarations.ToImmutable(), Current)
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
    /// <c>Declaration := Attribute* Modifier* (StructDeclaration | LanguageDeclaration | Typedef | FunctionOrVariable)</c>
    /// </remarks>
    private DeclarationSyntax ParseDeclaration()
    {
        if (Current.Kind == ShaderSyntaxKind.SemicolonToken)
        {
            // 余分なセミコロン。誤りではないので、報告せずに読み飛ばす。
            return new IncompleteDeclarationSyntax([Advance()]);
        }

        ImmutableArray<AttributeSyntax> attributes = ParseAttributes();

        // enum は DXC 2017 から使える。どのコンパイラと HLSL のバージョンでも使えなければ、報告して読み飛ばす (FXC / DXC 2016)。
        if (CurrentTextIs("enum"))
        {
            return AcceptsEnums
                ? ParseEnum()
                : ParseUnsupportedTypeDeclaration(endsWithSemicolon: true, report: true);
        }

        // template は DXC 2021 から使える。どのコンパイラと HLSL のバージョンでも使えなければ、報告して読み飛ばす。
        // 使えるのに型引数の並びを読めない形 (既定値の中の >>) は、報告せずに読み飛ばす。
        if (CurrentTextIs("template"))
        {
            return AcceptsTemplates && TryParseTemplate() is { } template
                ? template
                : ParseUnsupportedTypeDeclaration(endsWithSemicolon: false, report: !AcceptsTemplates);
        }

        if (CurrentTextIs("typedef"))
        {
            return ParseTypedef();
        }

        if (IsStaticAssert())
        {
            return ParseStaticAssert();
        }

        if (TryParseLanguageDeclaration() is { } languageDeclaration)
        {
            return languageDeclaration;
        }

        // using namespace dx; (シェーダーモデル 6.9 の dx::HitObject など)。名前の引き方には関わらないので読み飛ばす。
        // 修飾した名前 (dx::HitObject) は今までどおり読める。
        if (CurrentTextIs("using"))
        {
            ImmutableArray<ShaderSyntaxToken>.Builder skipped = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

            while (!AtEnd && Current.Kind != ShaderSyntaxKind.SemicolonToken)
            {
                skipped.Add(Advance());
            }

            if (Current.Kind == ShaderSyntaxKind.SemicolonToken)
            {
                skipped.Add(Advance());
            }

            return new IncompleteDeclarationSyntax(skipped.ToImmutable());
        }

        ImmutableArray<ShaderSyntaxToken> modifiers = ParseModifiers();

        // 前方宣言 (struct Light; / class ObjModelLoader;)。名前を知らせるだけで中身は無い。
        if ((CurrentTextIs("struct") || CurrentTextIs("class") || CurrentTextIs("interface"))
            && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken
            && Peek(2).Kind == ShaderSyntaxKind.SemicolonToken)
        {
            return new IncompleteDeclarationSyntax([Advance(), Advance(), Advance()]);
        }

        // class と interface は、構造体と同じ形で構文木にする。FXC でも DXC のどの版でも使える (Unity でも使える)。
        if (CurrentTextIs("class") || CurrentTextIs("interface"))
        {
            return ParseStruct();
        }

        if (CurrentTextIs("struct"))
        {
            return ParseStruct();
        }

        return ParseFunctionOrVariable(attributes, modifiers);
    }

    /// <summary>今の位置が <c>_Static_assert(...)</c> / <c>static_assert(...)</c> かを判定する。</summary>
    /// <returns>そうなら <see langword="true"/>。</returns>
    private bool IsStaticAssert()
        => (CurrentTextIs("_Static_assert") || CurrentTextIs("static_assert"))
           && Peek(1).Kind == ShaderSyntaxKind.OpenParenToken;

    /// <summary>
    /// コンパイル時の表明 (<c>_Static_assert(式, "文")</c>) を読み飛ばす。
    /// </summary>
    /// <returns>読み飛ばした宣言。</returns>
    /// <remarks>
    /// <para>
    /// DXC は C11 の <c>_Static_assert</c> を受け付け、HLSL 2021 では <c>static_assert</c> も使える。
    /// Unreal Engine のシェーダーは <c>HLSL_STATIC_ASSERT</c> マクロを通して、DXC のときにこれを書く
    /// (<c>Shared/HLSLStaticAssert.h</c>)。読めずにいると、関数の外の表明が構文エラー (HL0001) になっていた。
    /// </para>
    /// <para>
    /// 式は評価しない。何も宣言しないので、名前の引き方にも関わらない。
    /// </para>
    /// </remarks>
    private IncompleteDeclarationSyntax ParseStaticAssert()
    {
        ImmutableArray<ShaderSyntaxToken>.Builder skipped = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();
        skipped.Add(Advance());

        int depth = 0;

        while (!AtEnd)
        {
            ShaderSyntaxToken token = Advance();
            skipped.Add(token);

            if (token.Kind == ShaderSyntaxKind.OpenParenToken)
            {
                depth++;
            }
            else if (token.Kind == ShaderSyntaxKind.CloseParenToken && --depth == 0)
            {
                break;
            }
        }

        skipped.Add(Expect(ShaderSyntaxKind.SemicolonToken, "';'"));
        return new IncompleteDeclarationSyntax(skipped.ToImmutable());
    }

    /// <summary>修飾子の並びを読み取る。</summary>
    /// <returns>修飾子のトークン。</returns>
    protected ImmutableArray<ShaderSyntaxToken> ParseModifiers()
    {
        ImmutableArray<ShaderSyntaxToken>.Builder modifiers = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        while (true)
        {
            if (Current.Kind == ShaderSyntaxKind.IdentifierToken && IsDeclarationModifier(Current.Text))
            {
                modifiers.Add(Advance());
            }
            else if (!TryParseModifierGroup(modifiers))
            {
                break;
            }
        }

        return modifiers.ToImmutable();
    }

    /// <summary>
    /// この解析ツールが解釈しない宣言 (<c>class</c> / <c>interface</c>、HLSL 2018 の <c>enum</c> / <c>template</c> / <c>operator</c>、
    /// HLSL 2021 の <c>template</c> の明示的な特殊化) を読み飛ばす。
    /// </summary>
    /// <param name="endsWithSemicolon">
    /// 宣言がセミコロンで終わるかどうか。型宣言は <c>class Foo { ... } bar;</c> のように
    /// 波括弧の後ろに宣言子が続きうるので真にする。
    /// <c>template</c> が導くのは関数か変数で、関数は波括弧で終わるため偽にする。
    /// 真のまま読み飛ばすと、次のセミコロンまで、つまり後ろの宣言を丸ごと捨ててしまう。
    /// </param>
    /// <param name="report">
    /// HL0003 として報告するか。どのコンパイラと HLSL のバージョンでも使えない構文なら報告する。
    /// 使えるが読めない形 (型引数の並びを読めない template) なら報告せず、名前を解析していない場所の名前として残す。
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
    private IncompleteDeclarationSyntax ParseUnsupportedTypeDeclaration(bool endsWithSemicolon, bool report)
    {
        ShaderSyntaxToken keyword = Current;
        bool reported = report;

        if (reported)
        {
            string description = keyword.Text == "operator"
                ? "operator による演算子の多重定義"
                : $"{keyword.Text} 宣言";

            _diagnostics.Add(Diagnostic.Create(
                ShaderDescriptors.UnsupportedDeclaration, keyword.GetLocation(), description, UnavailableSubject));
        }

        ImmutableArray<ShaderSyntaxToken>.Builder skipped = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();
        skipped.Add(Advance());

        // 波括弧に入るまで (名前と継承指定) を読む。
        while (!AtEnd
               && Current.Kind != ShaderSyntaxKind.OpenBraceToken
               && Current.Kind != ShaderSyntaxKind.SemicolonToken)
        {
            skipped.Add(Advance());
        }

        if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
        {
            int depth = 0;

            do
            {
                if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
                {
                    depth++;
                }
                else if (Current.Kind == ShaderSyntaxKind.CloseBraceToken)
                {
                    depth--;
                }

                skipped.Add(Advance());
            }
            while (!AtEnd && depth > 0);
        }

        // 波括弧の後ろに続く宣言子と、閉じるセミコロン。
        while (endsWithSemicolon && !AtEnd && Current.Kind != ShaderSyntaxKind.SemicolonToken)
        {
            skipped.Add(Advance());
        }

        if (Current.Kind == ShaderSyntaxKind.SemicolonToken)
        {
            skipped.Add(Advance());
        }

        // 報告しない (受け付ける構文だが、まだ構文木にしない) なら、名前を解析していない場所の名前として残す。
        if (!reported)
        {
            _unanalyzedIdentifiers.AddRange(skipped.Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken));
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
    private DeclarationSyntax ParseFunctionOrVariable(
        ImmutableArray<AttributeSyntax> attributes,
        ImmutableArray<ShaderSyntaxToken> modifiers)
    {
        if (Current.Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return RecoverDeclaration(DescribeExpected("型名"));
        }

        TypeSyntax type = ParseType();

        // operator による演算子の多重定義 (S operator+(S other) { ... }) は DXC 2021 から使える。
        if (CurrentTextIs("operator"))
        {
            return AcceptsOperatorOverloading
                ? ParseOperatorFunction(attributes, modifiers, type)
                : ParseUnsupportedTypeDeclaration(endsWithSemicolon: false, report: true);
        }

        if (Current.Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return RecoverDeclaration(DescribeExpected("宣言する名前"));
        }

        ShaderSyntaxToken name = Advance();

        // 構造体の外でのメソッドの定義: uint Wave::GetIndex() { ... }
        // 修飾の後ろは関数名で、必ず関数の宣言になる。
        if (Current.Kind == ShaderSyntaxKind.ColonColonToken && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            ImmutableArray<ShaderSyntaxToken>.Builder qualifiers = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();
            qualifiers.Add(name);

            while (Current.Kind == ShaderSyntaxKind.ColonColonToken && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken)
            {
                qualifiers.Add(Advance());
                name = Advance();

                if (Current.Kind == ShaderSyntaxKind.ColonColonToken)
                {
                    qualifiers.Add(name);
                }
            }

            if (Current.Kind == ShaderSyntaxKind.OpenParenToken)
            {
                return ParseFunctionRest(attributes, modifiers, type, name, qualifiers.ToImmutable());
            }

            return RecoverDeclaration(DescribeExpected("'('"));
        }

        // 明示的な特殊化の関数名に付く型引数 (template<> float Twice<float>(float x))。
        if (AcceptsTemplates
            && Current.Kind == ShaderSyntaxKind.LessThanToken
            && FindTypeArgumentsEnd() is int close)
        {
            ImmutableArray<ShaderSyntaxToken>.Builder specialization = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

            for (int i = 0; i <= close; i++)
            {
                specialization.Add(Advance());
            }

            return ParseFunctionRest(attributes, modifiers, type, name, specializationTokens: specialization.ToImmutable());
        }

        return Current.Kind == ShaderSyntaxKind.OpenParenToken
            ? ParseFunctionRest(attributes, modifiers, type, name)
            : ParseVariableRest(modifiers, type, name);
    }

    /// <summary>関数宣言の残りの部分を解析する。</summary>
    /// <param name="attributes">属性。</param>
    /// <param name="modifiers">修飾子。</param>
    /// <param name="returnType">戻り値の型。</param>
    /// <param name="name">関数名。</param>
    /// <param name="qualifiers">構造体の外でのメソッドの定義なら、関数名の前の修飾 (<c>Wave</c> と <c>::</c>)。</param>
    /// <param name="operatorTokens">演算子の多重定義なら、<c>operator</c> に続く記号。</param>
    /// <param name="specializationTokens">明示的な特殊化なら、関数名に付く型引数 (山括弧を含む)。</param>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// 文法:
    /// <c>Function := Type (Identifier '::')* Identifier '(' ParameterList ')' Semantic* (Block | ';')</c>
    /// </remarks>
    private FunctionDeclarationSyntax ParseFunctionRest(
        ImmutableArray<AttributeSyntax> attributes,
        ImmutableArray<ShaderSyntaxToken> modifiers,
        TypeSyntax returnType,
        ShaderSyntaxToken name,
        ImmutableArray<ShaderSyntaxToken> qualifiers = default,
        ImmutableArray<ShaderSyntaxToken> operatorTokens = default,
        ImmutableArray<ShaderSyntaxToken> specializationTokens = default)
    {
        ShaderSyntaxToken openParen = Advance();
        ImmutableArray<ShaderNodeOrTokenEntry>.Builder parameters =
            ImmutableArray.CreateBuilder<ShaderNodeOrTokenEntry>();

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.CloseParenToken)
        {
            int positionBefore = _index;
            parameters.Add(ShaderNodeOrTokenEntry.FromNode(ParseParameter()));

            if (Current.Kind == ShaderSyntaxKind.CommaToken)
            {
                parameters.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            if (_index == positionBefore)
            {
                parameters.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
            }

            if (Current.Kind != ShaderSyntaxKind.CloseParenToken && Current.Kind != ShaderSyntaxKind.CommaToken)
            {
                break;
            }
        }

        ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
        ImmutableArray<SemanticSyntax> semantics = ParseSemantics();

        if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
        {
            BlockStatementSyntax body = IsFromOwnSource(Current) ? ParseBlock() : SkipBlock();
            return new FunctionDeclarationSyntax(
                attributes, modifiers, returnType, name, openParen,
                parameters.ToImmutable(), closeParen, semantics, body, null, qualifiers, operatorTokens, specializationTokens);
        }

        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");
        return new FunctionDeclarationSyntax(
            attributes, modifiers, returnType, name, openParen,
            parameters.ToImmutable(), closeParen, semantics, null, semicolon, qualifiers, operatorTokens, specializationTokens);
    }

    /// <summary>
    /// HLSL 2021 の演算子の多重定義 (<c>Complex operator+(Complex other) { ... }</c>) を関数として解析する。
    /// </summary>
    /// <param name="attributes">属性。</param>
    /// <param name="modifiers">修飾子。</param>
    /// <param name="returnType">戻り値の型。</param>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// 記号は 1 トークンが基本で、<c>operator[]</c> と <c>operator()</c> だけが 2 トークンになる。
    /// <c>operator()</c> の後ろにはもう 1 組の括弧 (仮引数) が続く。
    /// </remarks>
    private DeclarationSyntax ParseOperatorFunction(
        ImmutableArray<AttributeSyntax> attributes,
        ImmutableArray<ShaderSyntaxToken> modifiers,
        TypeSyntax returnType)
    {
        ShaderSyntaxToken keyword = Advance();
        ImmutableArray<ShaderSyntaxToken>.Builder symbols = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        if ((Current.Kind == ShaderSyntaxKind.OpenBracketToken && Peek(1).Kind == ShaderSyntaxKind.CloseBracketToken)
            || (Current.Kind == ShaderSyntaxKind.OpenParenToken && Peek(1).Kind == ShaderSyntaxKind.CloseParenToken
                && Peek(2).Kind == ShaderSyntaxKind.OpenParenToken))
        {
            symbols.Add(Advance());
            symbols.Add(Advance());
        }
        else if (!AtEnd && Current.Kind is not (ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.IdentifierToken))
        {
            symbols.Add(Advance());
        }

        if (symbols.Count == 0 || Current.Kind != ShaderSyntaxKind.OpenParenToken)
        {
            return RecoverDeclaration(DescribeExpected("演算子と '('"));
        }

        return ParseFunctionRest(attributes, modifiers, returnType, keyword, operatorTokens: symbols.ToImmutable());
    }

    /// <summary>関数の仮引数を 1 つ解析する。</summary>
    /// <returns>解析した仮引数。</returns>
    private ParameterSyntax ParseParameter()
    {
        // 引数に付ける属性 (ワークグラフの [MaxRecords(2)] NodeOutput<T> output)。型の判定には関わらないので読み飛ばす。
        if (Current.Kind == ShaderSyntaxKind.OpenBracketToken)
        {
            _ = ParseAttributes();
        }

        ImmutableArray<ShaderSyntaxToken> modifiers = ParseModifiers();

        // メッシュシェーダーの出力・増幅シェーダーの受け渡し (out vertices Vertex verts[64])。
        // 変数名としてもよく使う語なので、引数の中で、後ろに型と名前が続くときだけ修飾子として読む。
        if (Current.Kind == ShaderSyntaxKind.IdentifierToken
            && IsParameterModifier(Current.Text)
            && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken
            && Peek(2).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            modifiers = modifiers.Add(Advance());
        }

        TypeSyntax type = ParseType();

        ShaderSyntaxToken name = Current.Kind == ShaderSyntaxKind.IdentifierToken
            ? Advance()
            : CreateMissingToken(ShaderSyntaxKind.IdentifierToken);

        ImmutableArray<ShaderSyntaxToken> arrayRanks = ParseArrayRanks();
        ImmutableArray<SemanticSyntax> semantics = ParseSemantics();

        ShaderSyntaxToken? equals = null;
        ExpressionSyntax? defaultValue = null;

        if (Current.Kind == ShaderSyntaxKind.EqualsToken)
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
        ImmutableArray<ShaderSyntaxToken> modifiers,
        TypeSyntax type,
        ShaderSyntaxToken firstName)
    {
        ImmutableArray<ShaderNodeOrTokenEntry>.Builder declarators =
            ImmutableArray.CreateBuilder<ShaderNodeOrTokenEntry>();

        declarators.Add(ShaderNodeOrTokenEntry.FromNode(ParseDeclaratorRest(firstName)));

        while (Current.Kind == ShaderSyntaxKind.CommaToken)
        {
            declarators.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));

            ShaderSyntaxToken name = Current.Kind == ShaderSyntaxKind.IdentifierToken
                ? Advance()
                : CreateMissingToken(ShaderSyntaxKind.IdentifierToken);

            declarators.Add(ShaderNodeOrTokenEntry.FromNode(ParseDeclaratorRest(name)));
        }

        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");
        return new VariableDeclarationSyntax(modifiers, type, declarators.ToImmutable(), semicolon);
    }

    /// <summary>変数名より後の、配列・セマンティクス・初期化子を解析する。</summary>
    /// <param name="name">変数名。</param>
    /// <returns>解析した宣言子。</returns>
    private VariableDeclaratorSyntax ParseDeclaratorRest(ShaderSyntaxToken name)
    {
        ImmutableArray<ShaderSyntaxToken> arrayRanks = ParseArrayRanks();
        ImmutableArray<SemanticSyntax> semantics = ParseSemantics();

        ShaderSyntaxToken? equals = null;
        ExpressionSyntax? initializer = null;

        if (Current.Kind == ShaderSyntaxKind.EqualsToken)
        {
            equals = Advance();

            // sampler_state { ... } は値ではなくサンプラ状態の指定である。名前として読むと、宣言されていない名前 (HL0310) になる。
            if (Current.Text == "sampler_state" && Peek(1).Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                Advance();
            }
            else
            {
                initializer = ParseInitializer();
            }
        }

        // sampler_state { ... } のようなサンプラ状態の指定。
        // 中身は Cg 時代の記法であり構文木にする価値が無いため、括弧ごと読み飛ばす。
        if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
        {
            SkipBalancedBraces();
        }

        return new VariableDeclaratorSyntax(name, arrayRanks, semantics, equals, initializer);
    }

    /// <summary>初期化子を解析する。</summary>
    /// <returns>解析した式。</returns>
    private ExpressionSyntax ParseInitializer()
        => Current.Kind == ShaderSyntaxKind.OpenBraceToken ? ParseInitializerList() : ParseExpression();

    /// <summary>波括弧による初期化子を解析する。</summary>
    /// <returns>解析した式。</returns>
    private InitializerListExpressionSyntax ParseInitializerList()
    {
        ShaderSyntaxToken openBrace = Advance();
        ImmutableArray<ShaderNodeOrTokenEntry>.Builder elements =
            ImmutableArray.CreateBuilder<ShaderNodeOrTokenEntry>();

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            elements.Add(ShaderNodeOrTokenEntry.FromNode(ParseInitializer()));

            if (Current.Kind == ShaderSyntaxKind.CommaToken)
            {
                elements.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            if (_index == positionBefore)
            {
                elements.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
            }
        }

        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");
        return new InitializerListExpressionSyntax(openBrace, elements.ToImmutable(), closeBrace);
    }

    /// <summary><c>template</c> の宣言を読む (HLSL 2021)。</summary>
    /// <returns>読んだ宣言。この形では読めない場合は <see langword="null"/> で、何も読み進めない。</returns>
    /// <remarks>
    /// <para>
    /// 文法: <c>template '&lt;' TemplateParameter (',' TemplateParameter)* '&gt;' Declaration</c>。
    /// 型引数の並びは名前だけを読み、続く関数か構造体はふつうの宣言として読む。
    /// </para>
    /// <para>
    /// 明示的な特殊化 (<c>template&lt;&gt; float Twice&lt;float&gt;(float x)</c>) は読まない。
    /// 関数名の後ろに型引数が付く形を関数の宣言が持たないためで、今までどおり読み飛ばす。
    /// </para>
    /// </remarks>
    private TemplateDeclarationSyntax? TryParseTemplate()
    {
        if (Peek(1).Kind != ShaderSyntaxKind.LessThanToken)
        {
            return null;
        }

        // 閉じ山括弧の位置を先に確かめる。閉じられなければ読み進めない。
        int depth = 0;
        int close = -1;

        for (int offset = 1; close < 0; offset++)
        {
            switch (Peek(offset).Kind)
            {
                case ShaderSyntaxKind.LessThanToken:
                    depth++;
                    break;

                case ShaderSyntaxKind.GreaterThanToken:
                    if (--depth == 0)
                    {
                        close = offset;
                    }

                    break;

                case ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken
                    or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.EndOfFileToken:
                    return null;

                default:
                    break;
            }
        }

        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken openAngle = Advance();
        ImmutableArray<ShaderSyntaxToken>.Builder parameters = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        for (int i = 2; i < close; i++)
        {
            parameters.Add(Advance());
        }

        ShaderSyntaxToken closeAngle = Advance();
        DeclarationSyntax declaration = ParseDeclaration();

        switch (declaration)
        {
            case FunctionDeclarationSyntax function:
                _templateNames.Add(function.Name);
                break;

            case StructDeclarationSyntax structure:
                _templateNames.Add(structure.Name);
                break;
        }

        return new TemplateDeclarationSyntax(keyword, openAngle, parameters.ToImmutable(), closeAngle, declaration);
    }

    /// <summary>ここまでに template として宣言した関数と構造体の名前。</summary>
    /// <remarks>
    /// <b>名前の後ろの <c>&lt;</c> を型引数の始まりと読むのは、template の名前のときだけにする。</b>
    /// <c>a &lt; b, c &gt; (d)</c> は、<c>a</c> が template なら呼び出し、そうでなければ比較の並びである。
    /// C++ と同じく、名前の宣言を知らないと決められない。
    /// Unreal Engine の VirtualShadowMapDebug.usf の <c>or(OutputPixel &lt; BorderWidth, OutputPixel &gt;(…))</c> を呼び出しと読み、
    /// 引数の個数を誤って報告していた (HL0341)。
    /// </remarks>
    private readonly HashSet<string> _templateNames = new(StringComparer.Ordinal);

    /// <summary><c>enum</c> の宣言を読む (HLSL 2021)。</summary>
    /// <returns>読んだ宣言。</returns>
    /// <remarks>
    /// 文法: <c>enum [class|struct] Name [: Type] { A [= 式], ... } ;</c>。
    /// 列挙子の値の式は定数の式で、カンマで区切るので代入の式 (カンマ演算子を含まない) として読む。
    /// </remarks>
    private EnumDeclarationSyntax ParseEnum()
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken? scope = CurrentTextIs("class") || CurrentTextIs("struct") ? Advance() : null;
        ShaderSyntaxToken? name = Current.Kind == ShaderSyntaxKind.IdentifierToken ? Advance() : null;

        ImmutableArray<ShaderSyntaxToken>.Builder baseType = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        if (Current.Kind == ShaderSyntaxKind.ColonToken)
        {
            while (!AtEnd && Current.Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
            {
                baseType.Add(Advance());
            }
        }

        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<EnumMemberSyntax>.Builder members = ImmutableArray.CreateBuilder<EnumMemberSyntax>();

        while (!AtEnd && Current.Kind == ShaderSyntaxKind.IdentifierToken)
        {
            ShaderSyntaxToken memberName = Advance();
            ShaderSyntaxToken? equals = null;
            ExpressionSyntax? value = null;

            if (Current.Kind == ShaderSyntaxKind.EqualsToken)
            {
                equals = Advance();
                value = ParseExpression();
            }

            ShaderSyntaxToken? comma = Current.Kind == ShaderSyntaxKind.CommaToken ? Advance() : null;
            members.Add(new EnumMemberSyntax(memberName, equals, value, comma));

            if (comma is null)
            {
                break;
            }
        }

        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");
        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");

        return new EnumDeclarationSyntax(keyword, scope, name, baseType.ToImmutable(), openBrace, members.ToImmutable(), closeBrace, semicolon);
    }

    /// <summary>構造体宣言を解析する。</summary>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// 文法: <c>Struct := 'struct' Identifier? '{' Declaration* '}' Declarator* ';'</c>
    /// 無名構造体と、宣言に続けて変数を宣言する形の両方に対応する。
    /// </remarks>
    private StructDeclarationSyntax ParseStruct()
    {
        ShaderSyntaxToken keyword = Advance();

        // 構造体に付ける属性 (struct [raypayload] RayPayload)。読み飛ばす。
        if (Current.Kind == ShaderSyntaxKind.OpenBracketToken)
        {
            _ = ParseAttributes();
        }

        ShaderSyntaxToken? name = Current.Kind == ShaderSyntaxKind.IdentifierToken ? Advance() : null;

        // 明示的な特殊化の構造体名に付く型引数 (template<> struct Box<float> { ... })。
        ImmutableArray<ShaderSyntaxToken>.Builder specialization = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        if (name is not null && AcceptsTemplates && Current.Kind == ShaderSyntaxKind.LessThanToken)
        {
            int depth = 0;

            do
            {
                depth += Current.Kind switch
                {
                    ShaderSyntaxKind.LessThanToken => 1,
                    ShaderSyntaxKind.GreaterThanToken => -1,
                    _ => 0,
                };
                specialization.Add(Advance());
            }
            while (!AtEnd && depth > 0 && Current.Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken));
        }

        // struct の継承 (HLSL 2021 の struct D : B)。基底のメンバーも使えるので、名前を残す。
        // 基底は型引数を持ちうる (struct Derived : Box<T>)。
        ShaderSyntaxToken? colon = null;
        TypeSyntax? baseType = null;

        if (Current.Kind == ShaderSyntaxKind.ColonToken)
        {
            colon = Advance();

            if (Current.Kind == ShaderSyntaxKind.IdentifierToken)
            {
                baseType = ParseType();
            }
        }

        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<DeclarationSyntax>.Builder members =
            ImmutableArray.CreateBuilder<DeclarationSyntax>();

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            members.Add(ParseDeclaration());

            if (_index == positionBefore)
            {
                members.Add(new IncompleteDeclarationSyntax([Advance()]));
            }
        }

        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");

        ImmutableArray<ShaderNodeOrTokenEntry>.Builder declarators =
            ImmutableArray.CreateBuilder<ShaderNodeOrTokenEntry>();

        while (Current.Kind == ShaderSyntaxKind.IdentifierToken)
        {
            declarators.Add(ShaderNodeOrTokenEntry.FromNode(ParseDeclaratorRest(Advance())));

            if (Current.Kind != ShaderSyntaxKind.CommaToken)
            {
                break;
            }

            declarators.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
        }

        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");

        return new StructDeclarationSyntax(
            keyword, name, openBrace, members.ToImmutable(), closeBrace, declarators.ToImmutable(), semicolon, colon, baseType,
            specialization.ToImmutable());
    }

    /// <summary>
    /// 波括弧の中の宣言を、閉じ波括弧 (含まない) まで読む。
    /// </summary>
    /// <returns>読んだ宣言。</returns>
    /// <remarks>言語にだけある宣言 (HLSL の <c>cbuffer</c> / <c>namespace</c>) の中身を読むためにある。</remarks>
    protected ImmutableArray<DeclarationSyntax> ParseMemberDeclarations()
    {
        ImmutableArray<DeclarationSyntax>.Builder members =
            ImmutableArray.CreateBuilder<DeclarationSyntax>();

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            members.Add(ParseDeclaration());

            if (_index == positionBefore)
            {
                members.Add(new IncompleteDeclarationSyntax([Advance()]));
            }
        }

        return members.ToImmutable();
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
        ShaderSyntaxToken keyword = Advance();
        int start = _index;
        ImmutableArray<ShaderSyntaxToken>.Builder tokens = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        TypeSyntax? target = Current.Kind == ShaderSyntaxKind.IdentifierToken ? ParseType() : null;

        if (target is not null
            && Current.Kind == ShaderSyntaxKind.IdentifierToken
            && Peek(1).Kind == ShaderSyntaxKind.SemicolonToken
            && !target.NameToken.IsMissing)
        {
            _typeAliases[Current.Text] = target.Name;
        }

        for (int i = start; i < _index; i++)
        {
            tokens.Add(_tokens[i]);
        }

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.SemicolonToken)
        {
            tokens.Add(Advance());
        }

        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");
        return new TypedefDeclarationSyntax(keyword, tokens.ToImmutable(), semicolon);
    }

    /// <summary>組み込み型か、組み込み型への typedef の別名かを判定する。</summary>
    /// <param name="name">判定する名前。</param>
    /// <returns>確実に型なら <see langword="true"/>。</returns>
    private bool IsKnownTypeName(string name)
        => IsBuiltInTypeName(name)
           || (_typeAliases.TryGetValue(name, out string? target) && IsBuiltInTypeName(target));

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
    private TypeSyntax ParseType()
    {
        // 型名の前に置かれた修飾子を先に取る。
        // (const int) や (unsigned int) の形で型変換の中にも現れる。
        // 次も識別子であることを条件にするのは、
        // 修飾子と同じ綴りの型名だけが書かれている場合に食べ過ぎないためである。
        ImmutableArray<ShaderSyntaxToken>.Builder modifiers = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        while (Current.Kind == ShaderSyntaxKind.IdentifierToken
               && IsTypePrefixModifier(Current.Text)
               && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            modifiers.Add(Advance());
        }

        ShaderSyntaxToken name = Current.Kind == ShaderSyntaxKind.IdentifierToken
            ? Advance()
            : CreateMissingToken(ShaderSyntaxKind.IdentifierToken);

        ImmutableArray<ShaderSyntaxToken>.Builder qualifiers = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        while (Current.Kind == ShaderSyntaxKind.ColonColonToken)
        {
            qualifiers.Add(name);
            qualifiers.Add(Advance());

            name = Current.Kind == ShaderSyntaxKind.IdentifierToken
                ? Advance()
                : CreateMissingToken(ShaderSyntaxKind.IdentifierToken);
        }

        ShaderSyntaxToken? openAngle = null;
        ShaderSyntaxToken? closeAngle = null;
        ImmutableArray<ShaderSyntaxToken>.Builder templateArguments =
            ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        if (Current.Kind == ShaderSyntaxKind.LessThanToken)
        {
            openAngle = Advance();
            int depth = 0;

            while (!AtEnd)
            {
                if (Current.Kind == ShaderSyntaxKind.GreaterThanToken && depth == 0)
                {
                    break;
                }

                if (Current.Kind == ShaderSyntaxKind.LessThanToken)
                {
                    depth++;
                }
                else if (Current.Kind == ShaderSyntaxKind.GreaterThanToken)
                {
                    depth--;
                }

                // セミコロンや波括弧に達したら、山括弧ではなく比較演算子だったとみなす。
                // ここで止めないと、閉じられない山括弧がファイル全体を飲み込む。
                if (Current.Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken)
                {
                    break;
                }

                templateArguments.Add(Advance());
            }

            closeAngle = Current.Kind == ShaderSyntaxKind.GreaterThanToken
                ? Advance()
                : CreateMissingToken(ShaderSyntaxKind.GreaterThanToken);
        }

        // typedef の別名は、元の型の名前として扱う。
        string? aliasOf = openAngle is null && qualifiers.Count == 0 && _typeAliases.TryGetValue(name.Text, out string? target)
            ? target
            : null;

        ImmutableArray<ShaderSyntaxToken> arguments = templateArguments.ToImmutable();

        // 型の側の配列 (GLSL の vec4[3] positions)。
        ImmutableArray<ShaderSyntaxToken> arrayRanks = AcceptsArrayTypes && Current.Kind == ShaderSyntaxKind.OpenBracketToken
            ? ParseArrayRanks()
            : [];

        return new TypeSyntax(
            modifiers.ToImmutable(),
            name,
            qualifiers.ToImmutable(),
            openAngle,
            arguments,
            closeAngle,
            aliasOf,
            GetCanonicalTypeName(name, openAngle, arguments))
        {
            ArrayRanks = arrayRanks,
        };
    }

    /// <summary>配列の次元指定を読み取る。</summary>
    /// <returns>角括弧と大きさの式を含むトークン列。</returns>
    protected ImmutableArray<ShaderSyntaxToken> ParseArrayRanks()
    {
        ImmutableArray<ShaderSyntaxToken>.Builder tokens = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        while (Current.Kind == ShaderSyntaxKind.OpenBracketToken)
        {
            int depth = 0;

            do
            {
                if (Current.Kind == ShaderSyntaxKind.OpenBracketToken)
                {
                    depth++;
                }
                else if (Current.Kind == ShaderSyntaxKind.CloseBracketToken)
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
    protected ImmutableArray<SemanticSyntax> ParseSemantics()
    {
        ImmutableArray<SemanticSyntax>.Builder semantics = ImmutableArray.CreateBuilder<SemanticSyntax>();

        // 構造体のビットフィールド (uint a : 4;) は Unity では使えない。
        if (Current.Kind == ShaderSyntaxKind.ColonToken
            && Peek(1).Kind == ShaderSyntaxKind.NumericLiteralToken)
        {
            // Unity も数値を指して拒む ("unexpected integer constant")。コロン自体はセマンティクスの記法として正しい。
            Advance();
            ShaderSyntaxToken width = Advance();

            // DXC 2021 では正しい書き方である。幅は型の判定に関わらないので、読み飛ばすだけでよい。
            if (!AcceptsBitfields)
            {
                _diagnostics.Add(Diagnostic.Create(
                    ShaderDescriptors.UnsupportedDeclaration, width.GetLocation(), "ビットフィールド", UnavailableSubject));
            }
        }

        while (Current.Kind == ShaderSyntaxKind.ColonToken
            && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            ShaderSyntaxToken colon = Advance();
            ShaderSyntaxToken name = Advance();

            ShaderSyntaxToken? openParen = null;
            ShaderSyntaxToken? closeParen = null;
            ImmutableArray<ShaderSyntaxToken>.Builder arguments = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

            if (Current.Kind == ShaderSyntaxKind.OpenParenToken)
            {
                openParen = Advance();

                // 引数の中にも括弧が来る。
                // [numthreads(GROUP_SIZE, GROUP_SIZE, 1)] の GROUP_SIZE が
                // (TILE_SIZE / 2) のようなマクロだと、展開後は括弧を含む式になる。
                // 深さを数えないと、内側の閉じ括弧で属性が終わったと読んでしまう。
                int depth = 0;

                while (!AtEnd && (depth > 0 || Current.Kind != ShaderSyntaxKind.CloseParenToken))
                {
                    if (Current.Kind == ShaderSyntaxKind.OpenParenToken)
                    {
                        depth++;
                    }
                    else if (Current.Kind == ShaderSyntaxKind.CloseParenToken)
                    {
                        depth--;
                    }

                    arguments.Add(Advance());
                }

                closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
            }

            semantics.Add(new SemanticSyntax(colon, name, openParen, arguments.ToImmutable(), closeParen));
        }

        return semantics.ToImmutable();
    }

    /// <summary>属性の並びを解析する。</summary>
    /// <returns>解析した属性。</returns>
    private ImmutableArray<AttributeSyntax> ParseAttributes()
    {
        ImmutableArray<AttributeSyntax>.Builder attributes =
            ImmutableArray.CreateBuilder<AttributeSyntax>();

        while (Current.Kind == ShaderSyntaxKind.OpenBracketToken)
        {
            // [[vk::binding(0, 0)]] のような二重角括弧の注釈は読み飛ばす。
            // SPIR-V 向けの割り当てを指示するもので、この解析ツールが見る意味を持たない。
            if (Peek(1).Kind == ShaderSyntaxKind.OpenBracketToken)
            {
                SkipDoubleBracketAnnotation();
                continue;
            }

            ShaderSyntaxToken openBracket = Advance();

            if (Current.Kind != ShaderSyntaxKind.IdentifierToken)
            {
                // 属性ではなかった。読み過ぎた開き角括弧を戻す。
                _index--;
                break;
            }

            ShaderSyntaxToken name = Advance();

            ShaderSyntaxToken? openParen = null;
            ShaderSyntaxToken? closeParen = null;
            ImmutableArray<ShaderSyntaxToken>.Builder arguments = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

            if (Current.Kind == ShaderSyntaxKind.OpenParenToken)
            {
                openParen = Advance();

                // 引数の中にも括弧が来る。
                // [numthreads(GROUP_SIZE, GROUP_SIZE, 1)] の GROUP_SIZE が
                // (TILE_SIZE / 2) のようなマクロだと、展開後は括弧を含む式になる。
                // 深さを数えないと、内側の閉じ括弧で属性が終わったと読んでしまう。
                int depth = 0;

                while (!AtEnd && (depth > 0 || Current.Kind != ShaderSyntaxKind.CloseParenToken))
                {
                    if (Current.Kind == ShaderSyntaxKind.OpenParenToken)
                    {
                        depth++;
                    }
                    else if (Current.Kind == ShaderSyntaxKind.CloseParenToken)
                    {
                        depth--;
                    }

                    arguments.Add(Advance());
                }

                closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
            }

            ShaderSyntaxToken closeBracket = Expect(ShaderSyntaxKind.CloseBracketToken, "']'");
            attributes.Add(new AttributeSyntax(
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
            if (Current.Kind == ShaderSyntaxKind.OpenBracketToken)
            {
                depth++;
            }
            else if (Current.Kind == ShaderSyntaxKind.CloseBracketToken)
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
        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<StatementSyntax>.Builder statements =
            ImmutableArray.CreateBuilder<StatementSyntax>();

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            statements.Add(ParseStatement());

            if (_index == positionBefore)
            {
                statements.Add(new IncompleteStatementSyntax([Advance()]));
            }
        }

        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");
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
    private bool IsFromOwnSource(ShaderSyntaxToken token)
    {
        // 同じファイルを別にマスクしたテキスト (Unity の HLSLINCLUDE を先に読むもの) も、このファイルのものである。
        if (_ownSource is null
            || ReferenceEquals(token.Source, _ownSource)
            || string.Equals(token.Source.FilePath, _ownSource.FilePath, StringComparison.Ordinal))
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
        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        int depth = 1;

        while (!AtEnd && depth > 0)
        {
            if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                depth++;
            }
            else if (Current.Kind == ShaderSyntaxKind.CloseBraceToken)
            {
                depth--;

                if (depth == 0)
                {
                    break;
                }
            }

            Advance();
        }

        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");
        return new BlockStatementSyntax(openBrace, [], closeBrace);
    }

    /// <summary>文を 1 つ解析する。</summary>
    /// <returns>解析した文。</returns>
    private StatementSyntax ParseStatement()
    {
        ImmutableArray<AttributeSyntax> attributes = ParseAttributes();

        switch (Current.Kind)
        {
            case ShaderSyntaxKind.OpenBraceToken:
                return ParseBlock();

            case ShaderSyntaxKind.SemicolonToken:
                return new EmptyStatementSyntax(Advance());
        }

        if (Current.Kind == ShaderSyntaxKind.IdentifierToken)
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
                    return new JumpStatementSyntax(Advance(), Expect(ShaderSyntaxKind.SemicolonToken, "';'"));
                case "case":
                case "default":
                    return ParseSwitchLabel();
            }
        }

        if (LooksLikeDeclaration() || IsStaticAssert())
        {
            return new LocalDeclarationStatementSyntax(ParseDeclaration());
        }

        ExpressionSyntax expression = ParseExpression();
        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");
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
        if (Current.Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return false;
        }

        if (IsDeclarationModifier(Current.Text) || Current.Text == "struct")
        {
            return true;
        }

        if (IsKnownTypeName(Current.Text))
        {
            return true;
        }

        if (Peek(1).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            return true;
        }

        // 名前空間で修飾された型 (UnifiedRT::InstanceData x) を探す。
        if (Peek(1).Kind == ShaderSyntaxKind.ColonColonToken)
        {
            int offset = 1;

            while (Peek(offset).Kind == ShaderSyntaxKind.ColonColonToken
                && Peek(offset + 1).Kind == ShaderSyntaxKind.IdentifierToken)
            {
                offset += 2;
            }

            return Peek(offset).Kind == ShaderSyntaxKind.IdentifierToken;
        }

        // 識別子 < ... > 識別子 の形 (テンプレート型の宣言) を探す。
        if (Peek(1).Kind == ShaderSyntaxKind.LessThanToken)
        {
            int depth = 0;

            for (int offset = 1; offset < 32; offset++)
            {
                ShaderSyntaxKind kind = Peek(offset).Kind;

                if (kind == ShaderSyntaxKind.LessThanToken)
                {
                    depth++;
                }
                else if (kind == ShaderSyntaxKind.GreaterThanToken)
                {
                    depth--;

                    if (depth == 0)
                    {
                        return Peek(offset + 1).Kind == ShaderSyntaxKind.IdentifierToken;
                    }
                }
                else if (kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken
                    or ShaderSyntaxKind.EndOfFileToken)
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
    private IfStatementSyntax ParseIf(ImmutableArray<AttributeSyntax> attributes)
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken openParen = Expect(ShaderSyntaxKind.OpenParenToken, "'('");
        ExpressionSyntax condition = ParseExpression();
        ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
        StatementSyntax thenStatement = ParseStatement();

        ShaderSyntaxToken? elseKeyword = null;
        StatementSyntax? elseStatement = null;

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
    private ForStatementSyntax ParseFor(ImmutableArray<AttributeSyntax> attributes)
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken openParen = Expect(ShaderSyntaxKind.OpenParenToken, "'('");

        ShaderSyntaxNode? initializer = null;
        ShaderSyntaxToken? firstSemicolon = null;

        if (Current.Kind == ShaderSyntaxKind.SemicolonToken)
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
            firstSemicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");
        }

        ExpressionSyntax? condition = Current.Kind == ShaderSyntaxKind.SemicolonToken
            ? null
            : ParseExpression();

        ShaderSyntaxToken secondSemicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");

        ImmutableArray<ShaderNodeOrTokenEntry>.Builder incrementors =
            ImmutableArray.CreateBuilder<ShaderNodeOrTokenEntry>();

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.CloseParenToken)
        {
            int positionBefore = _index;
            incrementors.Add(ShaderNodeOrTokenEntry.FromNode(ParseExpression()));

            if (Current.Kind == ShaderSyntaxKind.CommaToken)
            {
                incrementors.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            if (_index == positionBefore)
            {
                incrementors.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
            }
            else
            {
                break;
            }
        }

        ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
        StatementSyntax body = ParseStatement();

        return new ForStatementSyntax(
            attributes, keyword, openParen, initializer, firstSemicolon,
            condition, secondSemicolon, incrementors.ToImmutable(), closeParen, body);
    }

    /// <summary><c>while</c> 文を解析する。</summary>
    /// <param name="attributes">属性。</param>
    /// <returns>解析した文。</returns>
    private WhileStatementSyntax ParseWhile(ImmutableArray<AttributeSyntax> attributes)
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken openParen = Expect(ShaderSyntaxKind.OpenParenToken, "'('");
        ExpressionSyntax condition = ParseExpression();
        ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
        StatementSyntax body = ParseStatement();

        return new WhileStatementSyntax(attributes, keyword, openParen, condition, closeParen, body);
    }

    /// <summary><c>do while</c> 文を解析する。</summary>
    /// <param name="attributes">属性。</param>
    /// <returns>解析した文。</returns>
    private DoWhileStatementSyntax ParseDoWhile(ImmutableArray<AttributeSyntax> attributes)
    {
        ShaderSyntaxToken doKeyword = Advance();
        StatementSyntax body = ParseStatement();
        ShaderSyntaxToken whileKeyword = CurrentTextIs("while")
            ? Advance()
            : CreateMissingToken(ShaderSyntaxKind.IdentifierToken);
        ShaderSyntaxToken openParen = Expect(ShaderSyntaxKind.OpenParenToken, "'('");
        ExpressionSyntax condition = ParseExpression();
        ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");

        return new DoWhileStatementSyntax(
            attributes, doKeyword, body, whileKeyword, openParen, condition, closeParen, semicolon);
    }

    /// <summary><c>switch</c> 文を解析する。</summary>
    /// <param name="attributes">属性。</param>
    /// <returns>解析した文。</returns>
    private SwitchStatementSyntax ParseSwitch(ImmutableArray<AttributeSyntax> attributes)
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken openParen = Expect(ShaderSyntaxKind.OpenParenToken, "'('");
        ExpressionSyntax expression = ParseExpression();
        ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");

        ImmutableArray<StatementSyntax>.Builder statements =
            ImmutableArray.CreateBuilder<StatementSyntax>();

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            statements.Add(ParseStatement());

            if (_index == positionBefore)
            {
                statements.Add(new IncompleteStatementSyntax([Advance()]));
            }
        }

        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");

        return new SwitchStatementSyntax(
            attributes, keyword, openParen, expression, closeParen, openBrace, statements.ToImmutable(), closeBrace);
    }

    /// <summary><c>case</c> / <c>default</c> ラベルを解析する。</summary>
    /// <returns>解析した文。</returns>
    private SwitchLabelStatementSyntax ParseSwitchLabel()
    {
        ShaderSyntaxToken keyword = Advance();
        ExpressionSyntax? value = keyword.TextIs("case") ? ParseExpression() : null;
        ShaderSyntaxToken colon = Expect(ShaderSyntaxKind.ColonToken, "':'");
        return new SwitchLabelStatementSyntax(keyword, value, colon);
    }

    /// <summary><c>return</c> 文を解析する。</summary>
    /// <returns>解析した文。</returns>
    private ReturnStatementSyntax ParseReturn()
    {
        ShaderSyntaxToken keyword = Advance();
        ExpressionSyntax? expression = Current.Kind == ShaderSyntaxKind.SemicolonToken
            ? null
            : ParseExpression();
        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");
        return new ReturnStatementSyntax(keyword, expression, semicolon);
    }

    // --------------------------------------------------------------------
    // 式
    // --------------------------------------------------------------------

    /// <summary>式を解析する。</summary>
    /// <returns>解析した式。</returns>
    private ExpressionSyntax ParseExpression() => ParseAssignment();

    /// <summary>代入式を解析する。</summary>
    /// <returns>解析した式。</returns>
    /// <remarks>代入は右結合であるため、右辺を再帰的に解析する。</remarks>
    private ExpressionSyntax ParseAssignment()
    {
        ExpressionSyntax left = ParseConditional();

        if (!IsAssignmentOperator(Current.Kind))
        {
            return left;
        }

        ShaderSyntaxToken operatorToken = Advance();
        ExpressionSyntax right = ParseAssignment();
        return new AssignmentExpressionSyntax(left, operatorToken, right);
    }

    /// <summary>三項条件式を解析する。</summary>
    /// <returns>解析した式。</returns>
    private ExpressionSyntax ParseConditional()
    {
        ExpressionSyntax condition = ParseBinary(0);

        if (Current.Kind != ShaderSyntaxKind.QuestionToken)
        {
            return condition;
        }

        ShaderSyntaxToken question = Advance();
        ExpressionSyntax whenTrue = ParseAssignment();
        ShaderSyntaxToken colon = Expect(ShaderSyntaxKind.ColonToken, "':'");
        ExpressionSyntax whenFalse = ParseAssignment();

        return new ConditionalExpressionSyntax(condition, question, whenTrue, colon, whenFalse);
    }

    /// <summary>優先順位つきで二項演算式を解析する。</summary>
    /// <param name="minimumPrecedence">この優先順位以上の演算子だけを処理する。</param>
    /// <returns>解析した式。</returns>
    private ExpressionSyntax ParseBinary(int minimumPrecedence)
    {
        ExpressionSyntax left = ParseUnary();
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

            ShaderSyntaxToken operatorToken = Advance();
            ExpressionSyntax right = ParseBinary(precedence + 1);
            left = new BinaryExpressionSyntax(left, operatorToken, right);
        }

        return left;
    }

    /// <summary>単項式を解析する。</summary>
    /// <returns>解析した式。</returns>
    private ExpressionSyntax ParseUnary()
    {
        if (Current.Kind is ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken
            or ShaderSyntaxKind.ExclamationToken or ShaderSyntaxKind.TildeToken
            or ShaderSyntaxKind.PlusPlusToken or ShaderSyntaxKind.MinusMinusToken)
        {
            ShaderSyntaxToken operatorToken = Advance();
            return new PrefixUnaryExpressionSyntax(operatorToken, ParseUnary());
        }

        if (Current.Kind == ShaderSyntaxKind.OpenParenToken && LooksLikeCast())
        {
            ShaderSyntaxToken openParen = Advance();
            TypeSyntax type = ParseType();
            ParseArrayRanks();
            ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
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
        if (Peek(1).Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return false;
        }

        // 括弧の中を読み飛ばして閉じ括弧を探す。
        int offset = 1;

        // (const int) や (unsigned int) の形では、型名の前に修飾子が並ぶ。
        // 組み込み型かどうかは、修飾子を越えた先の名前で判定する。
        while (IsTypePrefixModifier(Peek(offset).Text)
               && Peek(offset + 1).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            offset++;
        }

        bool isKnownType = IsKnownTypeName(Peek(offset).Text);
        int angleDepth = 0;

        while (offset < 32)
        {
            ShaderSyntaxKind kind = Peek(offset).Kind;

            if (kind == ShaderSyntaxKind.LessThanToken)
            {
                angleDepth++;
            }
            else if (kind == ShaderSyntaxKind.GreaterThanToken)
            {
                angleDepth--;
            }
            else if (kind == ShaderSyntaxKind.CloseParenToken && angleDepth == 0)
            {
                break;
            }
            else if (kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.EndOfFileToken
                or ShaderSyntaxKind.OpenBraceToken)
            {
                return false;
            }
            else if (kind == ShaderSyntaxKind.CommaToken)
            {
                // 山括弧の中のカンマは型引数の区切りである ((vector<float, 3>) x)。外のカンマは型の記述に現れない。
                if (angleDepth == 0)
                {
                    return false;
                }
            }
            else if (kind is not (ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.OpenBracketToken
                or ShaderSyntaxKind.CloseBracketToken or ShaderSyntaxKind.NumericLiteralToken

                // 名前空間で修飾した型 ((AnalyticPrimitive::Enum) x)。
                or ShaderSyntaxKind.ColonColonToken))
            {
                // 型の記述に現れない記号があれば型変換ではない。
                return false;
            }

            offset++;
        }

        if (Peek(offset).Kind != ShaderSyntaxKind.CloseParenToken)
        {
            return false;
        }

        ShaderSyntaxKind following = Peek(offset + 1).Kind;

        if (isKnownType)
        {
            // 組み込み型なら、後ろが式を始めうる形かどうかだけを見る。
            return following is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.NumericLiteralToken
                or ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.MinusToken
                or ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.ExclamationToken
                or ShaderSyntaxKind.TildeToken;
        }

        return following is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.NumericLiteralToken
            or ShaderSyntaxKind.OpenParenToken;
    }

    /// <summary>今の位置の <c>[..]</c> (続く <c>[..]</c> も含む) の直後が <c>(</c> かを、読み進めずに判定する。</summary>
    /// <returns>配列のコンストラクタの形なら <see langword="true"/>。</returns>
    private bool IsArrayTypeFollowedByCall()
    {
        int depth = 0;

        for (int offset = 0; ; offset++)
        {
            ShaderSyntaxKind kind = Peek(offset).Kind;

            if (kind == ShaderSyntaxKind.OpenBracketToken)
            {
                depth++;
            }
            else if (kind == ShaderSyntaxKind.CloseBracketToken && --depth == 0)
            {
                // 多次元の配列 (float[2][3](...)) は、続く [..] も読む。
                if (Peek(offset + 1).Kind == ShaderSyntaxKind.OpenBracketToken)
                {
                    continue;
                }

                return Peek(offset + 1).Kind == ShaderSyntaxKind.OpenParenToken;
            }
            else if (kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken
                or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.EndOfFileToken)
            {
                return false;
            }
        }
    }

    /// <summary>後置演算 (呼び出し・添字・メンバーアクセス・増減) を解析する。</summary>
    /// <param name="expression">対象の式。</param>
    /// <returns>解析した式。</returns>
    private ExpressionSyntax ParsePostfix(ExpressionSyntax expression)
    {
        while (!AtEnd)
        {
            switch (Current.Kind)
            {
                case ShaderSyntaxKind.OpenParenToken:
                    expression = ParseInvocation(expression);
                    break;

                // 配列のコンストラクタ (GLSL の float[](0.1, 0.2))。名前の後ろの [..] の直後が ( なら、配列の型として読む。
                case ShaderSyntaxKind.OpenBracketToken
                    when AcceptsArrayTypes
                         && expression is IdentifierExpressionSyntax elementType
                         && IsArrayTypeFollowedByCall():
                    expression = new ArrayTypeExpressionSyntax(elementType, ParseArrayRanks());
                    break;

                case ShaderSyntaxKind.OpenBracketToken:
                {
                    ShaderSyntaxToken openBracket = Advance();
                    ExpressionSyntax index = ParseExpression();
                    ShaderSyntaxToken closeBracket = Expect(ShaderSyntaxKind.CloseBracketToken, "']'");
                    expression = new ElementAccessExpressionSyntax(expression, openBracket, index, closeBracket);
                    break;
                }

                case ShaderSyntaxKind.DotToken:

                // 名前空間で修飾された関数呼び出し (UnifiedRT::TraceRay(...)) に対応する。
                // 構文の形はメンバーアクセスと同じなので同じノードで表す。
                case ShaderSyntaxKind.ColonColonToken:
                {
                    ShaderSyntaxToken separator = Advance();
                    ShaderSyntaxToken name = Current.Kind == ShaderSyntaxKind.IdentifierToken
                        ? Advance()
                        : CreateMissingToken(ShaderSyntaxKind.IdentifierToken);
                    expression = new MemberAccessExpressionSyntax(expression, separator, name);
                    break;
                }

                case ShaderSyntaxKind.PlusPlusToken:
                case ShaderSyntaxKind.MinusMinusToken:
                    expression = new PostfixUnaryExpressionSyntax(expression, Advance());
                    break;

                // 型引数を書いた型の静的メンバー (HLSL 2021 の TLaneVector2D<float, 2>::Const(1.0))。
                // 比較の連なりとして読むと、:: から先が式にならない。
                case ShaderSyntaxKind.LessThanToken
                    when AcceptsTemplates
                         && expression is IdentifierExpressionSyntax identifier
                         && _templateNames.Contains(identifier.Name)
                         && FindTypeArgumentsEnd(ShaderSyntaxKind.ColonColonToken) is int qualifiedClose:
                {
                    ShaderSyntaxToken openAngle = Advance();
                    ImmutableArray<ShaderSyntaxToken>.Builder typeArguments = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

                    for (int i = 1; i < qualifiedClose; i++)
                    {
                        typeArguments.Add(Advance());
                    }

                    expression = new GenericNameExpressionSyntax(identifier.Identifier, openAngle, typeArguments.ToImmutable(), Advance());
                    break;
                }

                // 型引数を書いた呼び出し (HLSL 2021 の Twice<float4>(x)、buffer.Load<float4>(i))。
                // 名前だけのときは、template として宣言した名前に限る (_templateNames)。メソッド (buffer.Load<float4>) は問わない。
                case ShaderSyntaxKind.LessThanToken
                    when AcceptsTemplates
                         && (expression is MemberAccessExpressionSyntax
                             || (expression is IdentifierExpressionSyntax { Name: var calledName } && _templateNames.Contains(calledName)))
                         && FindTypeArgumentsEnd() is int close:
                {
                    ShaderSyntaxToken openAngle = Advance();
                    ImmutableArray<ShaderSyntaxToken>.Builder typeArguments = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

                    for (int i = 1; i < close; i++)
                    {
                        typeArguments.Add(Advance());
                    }

                    ShaderSyntaxToken closeAngle = Advance();
                    expression = ParseInvocation(expression, openAngle, typeArguments.ToImmutable(), closeAngle);
                    break;
                }

                default:
                    return expression;
            }
        }

        return expression;
    }

    /// <summary>
    /// 現在の <c>&lt;</c> から型引数の並びが始まり、閉じ山括弧の直後に開き括弧 (または <paramref name="following"/>) が続くかを調べる。
    /// </summary>
    /// <param name="following">閉じ山括弧の直後に続く記号。</param>
    /// <returns>閉じ山括弧の位置 (現在からの距離)。型引数の並びでなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <c>a &lt; b &gt; (c)</c> という比較の連なりと形が同じになる。
    /// 比較の結果をさらに比べる書き方はまず無いので、型の記述に現れるトークンだけで閉じていれば呼び出しとみなす。
    /// </remarks>
    private int? FindTypeArgumentsEnd(ShaderSyntaxKind following = ShaderSyntaxKind.OpenParenToken)
    {
        int depth = 0;

        for (int offset = 0; offset < 32; offset++)
        {
            switch (Peek(offset).Kind)
            {
                case ShaderSyntaxKind.LessThanToken:
                    depth++;
                    break;

                case ShaderSyntaxKind.GreaterThanToken:
                    if (--depth == 0)
                    {
                        return offset > 1 && Peek(offset + 1).Kind == following ? offset : null;
                    }

                    break;

                case ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.NumericLiteralToken
                    or ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.ColonColonToken:
                    break;

                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>関数呼び出しを解析する。</summary>
    /// <param name="target">呼び出す対象。</param>
    /// <param name="openAngle">型引数の開き山括弧。書いていなければ <see langword="null"/>。</param>
    /// <param name="typeArguments">型引数のトークン。</param>
    /// <param name="closeAngle">型引数の閉じ山括弧。書いていなければ <see langword="null"/>。</param>
    /// <returns>解析した式。</returns>
    private InvocationExpressionSyntax ParseInvocation(
        ExpressionSyntax target,
        ShaderSyntaxToken? openAngle = null,
        ImmutableArray<ShaderSyntaxToken> typeArguments = default,
        ShaderSyntaxToken? closeAngle = null)
    {
        ShaderSyntaxToken openParen = Advance();
        ImmutableArray<ShaderNodeOrTokenEntry>.Builder arguments =
            ImmutableArray.CreateBuilder<ShaderNodeOrTokenEntry>();

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.CloseParenToken)
        {
            int positionBefore = _index;
            arguments.Add(ShaderNodeOrTokenEntry.FromNode(ParseAssignment()));

            if (Current.Kind == ShaderSyntaxKind.CommaToken)
            {
                arguments.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            if (_index == positionBefore)
            {
                arguments.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            break;
        }

        ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
        return new InvocationExpressionSyntax(
            target, openParen, arguments.ToImmutable(), closeParen, openAngle, typeArguments, closeAngle);
    }

    /// <summary>基本要素を解析する。</summary>
    /// <returns>解析した式。</returns>
    private ExpressionSyntax ParsePrimary()
    {
        switch (Current.Kind)
        {
            case ShaderSyntaxKind.NumericLiteralToken:
            case ShaderSyntaxKind.StringLiteralToken:
            case ShaderSyntaxKind.CharacterLiteralToken:
                return new LiteralExpressionSyntax(Advance());

            case ShaderSyntaxKind.IdentifierToken:
                return new IdentifierExpressionSyntax(Advance());

            // 大域の名前 (::AddLobeWithMIS(...))。構造体のメソッドから、同じ名前の大域の関数を呼ぶのに使う (HLSL 2021)。
            // メソッドは大域の関数の候補に入らないので、名前の参照として読めば大域の関数を引く。
            // 先頭の :: は木に残さない (名前の引き方に関わらない)。
            case ShaderSyntaxKind.ColonColonToken when Peek(1).Kind == ShaderSyntaxKind.IdentifierToken:
                Advance();
                return new IdentifierExpressionSyntax(Advance());

            case ShaderSyntaxKind.OpenParenToken:
            {
                ShaderSyntaxToken openParen = Advance();
                ExpressionSyntax inner = ParseExpression();

                // 括弧の中のカンマ演算子 ((0.1, 0.1, 0.1, 1) は 1 になる)。左から順に二項演算として読む。値の型は決めない。
                while (Current.Kind == ShaderSyntaxKind.CommaToken)
                {
                    ShaderSyntaxToken comma = Advance();
                    inner = new BinaryExpressionSyntax(inner, comma, ParseExpression());
                }

                ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
                return new ParenthesizedExpressionSyntax(openParen, inner, closeParen);
            }

            case ShaderSyntaxKind.OpenBraceToken:
                return ParseInitializerList();

            default:
                ReportError(DescribeExpected("式"));
                return new IncompleteExpressionSyntax([AdvanceIfPossible()]);
        }
    }

    /// <summary>代入演算子かどうかを判定する。</summary>
    /// <param name="kind">判定するトークン種別。</param>
    /// <returns>代入演算子の場合は <see langword="true"/>。</returns>
    private static bool IsAssignmentOperator(ShaderSyntaxKind kind) => ShaderSyntaxFacts.IsAssignmentOperator(kind);

    /// <summary>二項演算子の優先順位を返す。値が大きいほど強く結合する。</summary>
    /// <param name="kind">演算子の種別。</param>
    /// <returns>優先順位。二項演算子でない場合は負の値。</returns>
    private static int GetBinaryPrecedence(ShaderSyntaxKind kind) => kind switch
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

        ImmutableArray<ShaderSyntaxToken>.Builder skipped = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();
        int braceDepth = 0;

        while (!AtEnd)
        {
            if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                braceDepth++;
            }
            else if (Current.Kind == ShaderSyntaxKind.CloseBraceToken)
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
            else if (Current.Kind == ShaderSyntaxKind.SemicolonToken && braceDepth == 0)
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
            if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
            {
                depth++;
            }
            else if (Current.Kind == ShaderSyntaxKind.CloseBraceToken)
            {
                depth--;
            }

            Advance();
        }
        while (!AtEnd && depth > 0);
    }

    /// <summary>現在のトークンを返して 1 つ進む。</summary>
    /// <returns>進む前のトークン。</returns>
    protected ShaderSyntaxToken Advance()
    {
        ShaderSyntaxToken current = Current;

        if (_index < _tokens.Length)
        {
            _index++;
        }

        return current;
    }

    /// <summary>終端でなければ 1 つ進む。</summary>
    /// <returns>進む前のトークン。</returns>
    private ShaderSyntaxToken AdvanceIfPossible() => AtEnd ? Current : Advance();

    /// <summary>指定した相対位置のトークンを取得する。</summary>
    /// <param name="offset">現在位置からの相対オフセット。</param>
    /// <returns>その位置のトークン。範囲外の場合は終端トークン。</returns>
    protected ShaderSyntaxToken Peek(int offset)
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
    protected bool CurrentTextIs(string text)
        => Current.Kind == ShaderSyntaxKind.IdentifierToken && Current.TextIs(text);

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
    protected ShaderSyntaxToken Expect(ShaderSyntaxKind kind, string expectation)
    {
        if (Current.Kind == kind)
        {
            return Advance();
        }

        ReportError(DescribeExpected(expectation));
        return CreateMissingToken(kind);
    }

    /// <summary>現在のトークンを説明する文字列を返す。</summary>
    /// <returns>エラーメッセージに埋め込む文字列。</returns>
    private string DescribeCurrent()
        => Current.Kind == ShaderSyntaxKind.EndOfFileToken ? "ファイルの終わり" : Current.Text;

    /// <summary>今の位置で何かが要ることを伝える文を作る。</summary>
    /// <param name="expectation">要るもの (<c>';'</c> / <c>式</c> など)。</param>
    /// <returns>文。ファイルの終わりなら「ファイルの末尾には … が必要です。」</returns>
    private string DescribeExpected(string expectation)
        => Current.Kind == ShaderSyntaxKind.EndOfFileToken
            ? $"ファイルの末尾には {expectation}{Space(expectation)}が必要です。"
            : $"{expectation}{Space(expectation)}が必要ですが '{Current.Text}' がありました。";

    /// <summary>
    /// 今の位置の <c>(</c> が、定義されていない関数形式のマクロの呼び出しに見えるなら、そう伝える文を作る。
    /// </summary>
    /// <returns>文。<c>名前(</c> の形でなければ <see langword="null"/>。</returns>
    /// <remarks>
    /// 宣言の位置 (ファイルの直下・構造体のメンバー・引数の並び) で <c>NAME(...)</c> が構文エラーになるのは、
    /// ほとんどが <c>NAME</c> がマクロとして展開されなかったときである
    /// (定義がこの位置より後にある、使われない #if の分岐にある、定義を含むヘッダを取り込めていない)。
    /// 「宣言する名前が必要ですが '(' がありました」では、マクロのことだと気付きにくい。
    /// </remarks>
    private string? DescribeUndefinedMacroCall()
    {
        if (Current.Kind != ShaderSyntaxKind.OpenParenToken || _index == 0)
        {
            return null;
        }

        ShaderSyntaxToken name = Peek(-1);

        if (name.Kind != ShaderSyntaxKind.IdentifierToken || name.IsMissing || IsBuiltInTypeName(name.Text))
        {
            return null;
        }

        return $"'{name.Text}(' を宣言として読めません。'{name.Text}' はここではマクロとして定義されていません。"
            + $"'{name.Text}' を定義する #define (またはそれを含む #include) がこの位置より前にあり、この構成で有効か確かめてください。";
    }

    /// <summary>
    /// 要るものの名前と「が」の間の空白。記号や英字 ('}' / HLSLPROGRAM) なら空け、日本語 (式 / 型名) なら詰める。
    /// </summary>
    /// <param name="expectation">要るもの。</param>
    /// <returns>空白か空。</returns>
    private static string Space(string expectation)
        => expectation.Length > 0 && char.IsAscii(expectation[^1]) ? " " : string.Empty;

    /// <summary>欠落トークンを合成する。</summary>
    /// <param name="kind">合成するトークンの種別。</param>
    /// <returns>合成したトークン。</returns>
    private ShaderSyntaxToken CreateMissingToken(ShaderSyntaxKind kind)
        => new(kind, Current.Source, new TextSpan(Current.Span.Start, 0), string.Empty, isMissing: true);

    /// <summary>終端トークンを合成する。</summary>
    /// <param name="last">直前のトークン。位置の基準に使う。</param>
    /// <returns>合成したトークン。</returns>
    private static ShaderSyntaxToken CreateEndOfFileToken(ShaderSyntaxToken? last)
        => last is not null
            ? new ShaderSyntaxToken(ShaderSyntaxKind.EndOfFileToken, last.Source,
                new TextSpan(last.Span.End, 0), string.Empty)
            : new ShaderSyntaxToken(ShaderSyntaxKind.EndOfFileToken,
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

        // 立て直しのために同じ位置で何度もつまずくことがある。読み手に要るのは最初の 1 件だけである。
        if (_lastErrorToken is not null
            && ReferenceEquals(_lastErrorToken.Source, Current.Source)
            && _lastErrorToken.Span.Start == Current.Span.Start)
        {
            return;
        }

        _lastErrorToken = Current;

        if (DescribeUndefinedMacroCall() is { } undefinedMacro)
        {
            message = undefinedMacro;
        }

        _reportedErrors++;
        _diagnostics.Add(Diagnostic.Create(
            ShaderDescriptors.SyntaxError, Current.GetLocation(), message));
    }
}
