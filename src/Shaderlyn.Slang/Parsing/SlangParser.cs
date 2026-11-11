using System.Collections.Immutable;
using Shaderlyn.CFamily.Parsing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Slang.Parsing;

/// <summary>
/// Slang の構文解析。HLSL (2021) の構文に、Slang に固有の構文を足す。
/// </summary>
/// <remarks>
/// <para>
/// 構文木にするもの: <c>func</c> の関数、<c>let</c> / <c>var</c> の変数、<c>__init</c>、ジェネリックの関数と構造体 (仮引数は読み飛ばす)、
/// ポインターの型、<c>;</c> を省いた構造体と列挙型、<c>cbuffer</c>、<c>namespace</c>。
/// </para>
/// <para>
/// 読み飛ばすもの (中の名前は、名前を解析していない場所の名前として残す): <c>import</c> / <c>module</c> / <c>implementing</c> / <c>__include</c>、
/// <c>extension</c>、<c>typealias</c>、<c>associatedtype</c>、<c>property</c>、<c>__subscript</c>、<c>where</c> の制約、
/// <c>is</c> / <c>as</c> の式、ラムダ式、<c>__target_switch</c>、<c>spirv_asm</c>。
/// 読み飛ばした所では、「宣言が無い」を根拠にする検査がその名前を報告しない。
/// </para>
/// </remarks>
internal sealed class SlangParser : ShaderParser
{
    /// <summary>トークン列から解析器を作る。</summary>
    /// <param name="tokens">プリプロセス済みのトークン列。</param>
    public SlangParser(ImmutableArray<ShaderSyntaxToken> tokens)
        : base(tokens)
    {
    }

    /// <summary>トークン列から解析器を作る。</summary>
    /// <param name="tokens">プリプロセス済みのトークン列。</param>
    /// <param name="ownSource">解析対象のファイル。</param>
    /// <param name="isUserInclude">利用者の取り込みか。</param>
    public SlangParser(ImmutableArray<ShaderSyntaxToken> tokens, SourceText? ownSource, Func<string, bool>? isUserInclude = null)
        : base(tokens, ownSource, isUserInclude)
    {
    }

    /// <summary>トークン列が、宣言の区切りで終わっているか。</summary>
    /// <param name="tokens">トークン列。</param>
    /// <returns>区切りで終わっていれば <see langword="true"/>。</returns>
    public static bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens)
        => IsCompleteUnits(tokens, static t => new SlangParser(t));

    /// <inheritdoc/>
    protected override string UnavailableSubject => "Slang";

    /// <inheritdoc/>
    protected override bool AcceptsEnums => true;

    /// <inheritdoc/>
    protected override bool ReportsEndOfFileAtTextEnd => true;

    /// <inheritdoc/>
    protected override bool AcceptsTemplates => true;

    /// <inheritdoc/>
    protected override bool AcceptsOperatorOverloading => true;

    /// <inheritdoc/>
    protected override bool AcceptsBitfields => true;

    /// <inheritdoc/>
    protected override bool AcceptsPointerTypes => true;

    /// <inheritdoc/>
    protected override bool AllowsOmittedTypeSemicolon => true;

    /// <inheritdoc/>
    protected override bool ModifierWordsCanBeNames => true;

    /// <inheritdoc/>
    protected override bool AcceptsDottedTypeNames => true;

    /// <inheritdoc/>
    protected override bool AcceptsArrayTypes => true;

    /// <inheritdoc/>
    /// <remarks>型の並びを扱う組み込み (__first(T)・__last(T)・__trimFirst(T)・__trimLast(T)・__packBranch(…)) は、型の位置に書ける (__first(T).Element value;)。</remarks>
    protected override bool TakesParenthesizedTypeArguments(string name) => name is "__first" or "__last" or "__trimFirst" or "__trimLast" or "__packBranch" or "functype";

    /// <inheritdoc/>
    protected override bool TypeArgumentsTakeReturnType(string name) => name == "functype";

    /// <inheritdoc/>
    /// <remarks>slangc は typedef int; の ; を「unexpected token」(E20001) にする。</remarks>
    protected override bool RequiresTypedefName => true;

    /// <inheritdoc/>
    /// <remarks>Slang の列挙子は、[UnscopedEnum] を付けなければ型の名前で修飾して書く (enum Fruit { Apple } の Apple は Fruit.Apple)。</remarks>
    protected override bool EnumMembersNeedQualificationByDefault => !UnscopedEnums;

    /// <summary>どの列挙型の列挙子も修飾せずに書けるか (<see cref="SlangLanguageOptions.UnscopedEnums"/>)。</summary>
    public bool UnscopedEnums { get; init; }

    /// <inheritdoc/>
    /// <remarks>
    /// slangc 2026.19 に読ませて確かめた形。浮動小数は f・h・l をそれぞれ 1 つまで (h と l は組めない)、大文字と小文字をそろえる (1.0hf・1.0LF・1.0fl)。
    /// 整数は u を 1 つまでと、l・ll・z のどれか 1 つ (ll は大文字と小文字をそろえる。1ull・1zu・1Lu)。d (1.0d) は誤り。
    /// </remarks>
    protected override bool IsNumericSuffixAccepted(string suffix, bool floating)
    {
        if (floating)
        {
            return suffix.Length <= 2
                   && suffix.Count(c => c is 'f' or 'F') <= 1
                   && suffix.Count(c => c is 'h' or 'H' or 'l' or 'L') <= 1
                   && suffix.All(c => c is 'f' or 'F' or 'h' or 'H' or 'l' or 'L')
                   && (suffix.All(char.IsLower) || suffix.All(char.IsUpper));
        }

        if (suffix.All(c => c is 'u' or 'U' or 'l' or 'L' or 'z' or 'Z'))
        {
            return System.Text.RegularExpressions.Regex.IsMatch(suffix, "^(?:[uU]?(?:l|L|ll|LL|z|Z)?|(?:l|L|ll|LL|z|Z)[uU])$");
        }

        return suffix.All(c => c is 'f' or 'F' or 'h' or 'H' or 'u' or 'U' or 'l' or 'L');
    }

    /// <inheritdoc/>
    protected override bool IsBuiltInTypeName(string name) => SlangKeywords.IsKnownTypeName(name);

    /// <inheritdoc/>
    /// <remarks>
    /// <c>vector&lt;int, 4&gt;</c> を <c>int4</c>、<c>matrix&lt;float, 2, 3&gt;</c> を <c>float2x3</c> にする (HLSL と同じ)。
    /// 大きさを数で書いたものだけ。型引数を書かない <c>vector</c> は Slang には無いので、そろえない。
    /// </remarks>
    protected override string? GetCanonicalTypeName(ShaderSyntaxToken name, ShaderSyntaxToken? openAngle, ImmutableArray<ShaderSyntaxToken> templateArguments)
    {
        bool isVector = name.Text == "vector";

        if ((!isVector && name.Text != "matrix") || openAngle is null)
        {
            return null;
        }

        int expected = isVector ? 3 : 5;

        if (templateArguments.Length != expected || !SlangKeywords.IsKnownTypeName(templateArguments[0].Text + "4"))
        {
            return null;
        }

        for (int i = 1; i < expected; i += 2)
        {
            if (templateArguments[i].Kind != ShaderSyntaxKind.CommaToken || templateArguments[i + 1].Text is not { Length: 1 } digit || digit[0] is < '1' or > '4')
            {
                return null;
            }
        }

        return isVector
            ? templateArguments[0].Text + templateArguments[2].Text
            : templateArguments[0].Text + templateArguments[2].Text + "x" + templateArguments[4].Text;
    }

    /// <inheritdoc/>
    protected override bool IsDeclarationModifier(string text) => SlangKeywords.IsDeclarationModifier(text);

    /// <inheritdoc/>
    protected override bool IsTypePrefixModifier(string text) => HlslKeywords.TypePrefixModifiers.Contains(text);

    /// <inheritdoc/>
    protected override bool IsParameterModifier(string text) => HlslKeywords.MeshParameterModifiers.Contains(text);

    /// <inheritdoc/>
    protected override bool IsBuiltInTemplateTypeName(string name) => name is "vector" or "matrix";

    /// <inheritdoc/>
    protected override bool LooksLikeLanguageDeclaration()
    {
        if (Current.Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return false;
        }

        // let x = …; / var x : T; / typealias T = …;
        if (Current.Text is "let" or "var" or "typealias" or "func" or "__generic" && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            return true;
        }

        // A.B x; / A<T>.Handle h; / Foo<Bar<T>> x;
        if (Peek(1).Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.LessThanToken or ShaderSyntaxKind.ColonColonToken && ScanTypeEnd(0) is int typeEnd)
        {
            return Peek(typeEnd).Kind == ShaderSyntaxKind.IdentifierToken
                   && Peek(typeEnd + 1).Kind is ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CommaToken
                       or ShaderSyntaxKind.OpenBracketToken or ShaderSyntaxKind.ColonToken;
        }

        // float4* p = …; / Foo* p;
        if (Peek(1).Kind == ShaderSyntaxKind.AsteriskToken)
        {
            int offset = 1;

            while (Peek(offset).Kind == ShaderSyntaxKind.AsteriskToken)
            {
                offset++;
            }

            return Peek(offset).Kind == ShaderSyntaxKind.IdentifierToken
                   && Peek(offset + 1).Kind is ShaderSyntaxKind.EqualsToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CommaToken;
        }

        return false;
    }

    /// <inheritdoc/>
    /// <remarks>GLSL の書き方の <c>layout(…)</c> を、括弧ごと修飾子として読む (Slang は GLSL の書き方も読む)。</remarks>
    protected override bool TryParseModifierGroup(ImmutableArray<ShaderSyntaxToken>.Builder modifiers)
    {
        // 可変長の仮引数 (expand each T values)。後ろに名前が続くときだけ修飾子として読む。
        if (Current.Text is "expand" or "each" && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            modifiers.Add(Advance());
            return true;
        }

        // 引数を取ることのある修飾子は、引数が無くても読む。
        if (Current.Kind == ShaderSyntaxKind.IdentifierToken
            && SlangKeywords.ModifiersWithArguments.Contains(Current.Text)
            && Peek(1).Kind != ShaderSyntaxKind.OpenParenToken)
        {
            modifiers.Add(Advance());
            return true;
        }

        if (!(CurrentTextIs("layout") || SlangKeywords.ModifiersWithArguments.Contains(Current.Text)) || Peek(1).Kind != ShaderSyntaxKind.OpenParenToken)
        {
            return false;
        }

        modifiers.Add(Advance());
        modifiers.AddRange(SkipBalanced(ShaderSyntaxKind.OpenParenToken, ShaderSyntaxKind.CloseParenToken));
        return true;
    }

    /// <inheritdoc/>
    protected override DeclarationSyntax? TryParseLanguageDeclaration(ImmutableArray<AttributeSyntax> attributes)
    {
        int start = Position;

        // __generic<let N : int> の仮引数の並びは、続く宣言の前に読み飛ばす。
        if (CurrentTextIs("__generic") && Peek(1).Kind == ShaderSyntaxKind.LessThanToken)
        {
            Advance();
            _ = TryParseGenericParameters();
            _ = ParseAttributes();
            start = Position;
        }

        ImmutableArray<ShaderSyntaxToken> modifiers = ParseModifiers();

        // 修飾子の後ろの属性 (__target_intrinsic(cpp, "…") [__readNone] int f();)。属性を読んだら、そこから先を宣言として読む (前の修飾子は残らない)。
        while (Current.Kind == ShaderSyntaxKind.OpenBracketToken && Peek(1).Kind is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.OpenBracketToken && !modifiers.IsEmpty)
        {
            _ = ParseAttributes();
            modifiers = [.. modifiers, .. ParseModifiers()];
            start = Position;
        }

        // 修飾子だけの宣言 (GLSL の書き方の layout(local_size_x = 1) in;)。
        if (!modifiers.IsEmpty && Current.Kind == ShaderSyntaxKind.SemicolonToken)
        {
            return new IncompleteDeclarationSyntax([.. modifiers, Advance()]);
        }

        if (Current.Kind != ShaderSyntaxKind.IdentifierToken)
        {
            Position = start;
            return null;
        }

        // GLSL の書き方のブロック (uniform UBO { … } ubo; / buffer Data { … };)。
        if (!modifiers.IsEmpty
            && Peek(1).Kind == ShaderSyntaxKind.OpenBraceToken
            && !SlangKeywords.DeclarationKeywords.Contains(Current.Text)
            && Current.Text is not ("struct" or "class" or "interface" or "enum" or "cbuffer" or "tbuffer" or "namespace"))
        {
            List<ShaderSyntaxToken> skipped = [.. modifiers, Advance()];
            skipped.AddRange(SkipBalanced(ShaderSyntaxKind.OpenBraceToken, ShaderSyntaxKind.CloseBraceToken));

            if (Current.Kind == ShaderSyntaxKind.IdentifierToken && Peek(1).Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBracketToken)
            {
                skipped.Add(Advance());

                while (!AtEnd && Current.Kind is ShaderSyntaxKind.OpenBracketToken or ShaderSyntaxKind.CloseBracketToken or ShaderSyntaxKind.NumericLiteralToken or ShaderSyntaxKind.IdentifierToken)
                {
                    skipped.Add(Advance());
                }
            }

            // Slang では、ブロックの後ろの ; を省ける。
            if (Current.Kind == ShaderSyntaxKind.SemicolonToken)
            {
                skipped.Add(Advance());
            }
            MarkUnanalyzed(skipped);
            return new IncompleteDeclarationSyntax([.. skipped]);
        }

        // 修飾子の後ろの typedef (public typedef float InputType;)。
        if (Current.Text == "typedef" && !modifiers.IsEmpty)
        {
            return ParseTypedef();
        }

        // struct X : I = Impl; (リンクのときに決まる型) と、本体の無い宣言 (extern struct X : I;)。
        if (Current.Text is "struct" or "class" && LinksToType())
        {
            return SkipToSemicolon(markNames: true);
        }

        switch (Current.Text)
        {
            // モジュールの名前は、名前の修飾 (mylib.f()) にも使えるので、名前を解析していない場所の名前にする。
            case "import" or "__import" or "module" or "implementing" or "__include":
                return SkipToSemicolon(markNames: true);

            case "cbuffer" or "tbuffer":
                return ParseConstantBuffer();

            case "namespace":
                return ParseNamespace();

            case "func":
                return ParseFunc(attributes, modifiers);

            case "let" or "var":
                return ParseLetOrVar(modifiers);

            case "__init":
                return ParseInitializerFunction(attributes, modifiers);

            // type_param T : IBase; (古い書き方の、ファイル全体のジェネリックの仮引数)
            case "typealias" or "associatedtype" or "__constraint" or "precision" or "type_param":
                return SkipToSemicolon(markNames: true);

            // __func_extension fwd_diff(f)(…) -> T { … }
            case "__func_extension":
                return SkipTypeLikeDeclaration();

            case "extension" or "__extension":
                return SkipTypeLikeDeclaration();

            case "property" or "__subscript":
                return SkipPropertyOrSubscript();
        }

        Position = start;
        return null;
    }

    /// <inheritdoc/>
    protected override bool TryParseGenericParameters()
    {
        if (Current.Kind != ShaderSyntaxKind.LessThanToken)
        {
            return false;
        }

        // < … > を読み飛ばす。仮引数の名前 (T、N) は宣言として残らないので、名前を解析していない場所の名前にする。
        // 宣言の名前は、型引数を書いて呼べる名前として覚える (f<float>(x)、Box<int>.make())。
        ShaderSyntaxToken declared = Peek(-1);
        int start = Position;
        List<ShaderSyntaxToken> skipped = [];
        int depth = 0;

        // 仮引数の名前の位置に名前でないもの (<1>) と、制約の位置の struct などのキーワード (<T : struct>) の位置と、何が要るか。
        (int Position, string Expected)? problem = null;
        bool expectsName = false;

        do
        {
            if (problem is null && depth == 1)
            {
                if (expectsName && Current.Kind != ShaderSyntaxKind.GreaterThanToken)
                {
                    // キーワードの名前 (<struct>) は、slangc が警告だけにするので通す。
                    if (Current.Kind == ShaderSyntaxKind.IdentifierToken)
                    {
                        expectsName = Current.Text is "let" or "each" or "typename";
                    }
                    else
                    {
                        problem = (Position, "ジェネリックの仮引数の名前");
                    }
                }
                else if (Peek(-1).Kind == ShaderSyntaxKind.ColonToken && Current.Text is "struct" or "class" or "enum")
                {
                    problem = (Position, "制約のインターフェイス");
                }
            }

            ShaderSyntaxToken token = Advance();
            expectsName = depth == 0 && token.Kind == ShaderSyntaxKind.LessThanToken
                          || depth == 1 && token.Kind == ShaderSyntaxKind.CommaToken
                          || (expectsName && token.Text is "let" or "each" or "typename");
            skipped.Add(token);
            depth += token.Kind switch
            {
                ShaderSyntaxKind.LessThanToken => 1,
                ShaderSyntaxKind.GreaterThanToken => -1,
                ShaderSyntaxKind.GreaterThanGreaterThanToken => -2,
                _ => 0,
            };
        }
        while (!AtEnd && depth > 0 && Current.Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken));

        if (depth > 0)
        {
            Position = start;
            return false;
        }

        if (problem is { } found)
        {
            int end = Position;
            Position = found.Position;
            ReportError(DescribeExpected(found.Expected));
            Position = end;
        }

        MarkUnanalyzed(skipped);
        RememberGenericParameters([.. skipped]);

        if (declared.Kind == ShaderSyntaxKind.IdentifierToken)
        {
            RegisterTemplateName(declared.Text);
        }

        return true;
    }

    /// <inheritdoc/>
    protected override void SkipDeclarationSuffix()
    {
        List<ShaderSyntaxToken> skipped = [];

        while (!AtEnd)
        {
            // 2 つ目からの基底 (struct S : IFoo, IBar、struct S : IFoo & IBar)。
            if (Current.Kind is ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.AmpersandToken)
            {
                skipped.Add(Advance());
                skipped.AddRange(SkipTypeTokens());
                continue;
            }

            // 戻り値の型 (-> T) と、投げる型 (throws E)。
            if (IsArrow())
            {
                skipped.Add(Advance());
                skipped.Add(Advance());
                skipped.AddRange(SkipTypeTokens());
                continue;
            }

            if (CurrentTextIs("throws"))
            {
                skipped.Add(Advance());
                skipped.AddRange(SkipTypeTokens());
                continue;
            }

            // where T : IFoo, U == V (本体か ; まで)。
            if (CurrentTextIs("where"))
            {
                int angle = 0;

                while (!AtEnd && !(angle == 0 && Current.Kind is ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
                {
                    angle += Current.Kind switch
                    {
                        ShaderSyntaxKind.LessThanToken => 1,
                        ShaderSyntaxKind.GreaterThanToken => -1,
                        _ => 0,
                    };
                    skipped.Add(Advance());
                }

                continue;
            }

            break;
        }

        MarkUnanalyzed(skipped);
    }

    /// <inheritdoc/>
    protected override StatementSyntax? TryParseLanguageStatement()
    {
        // $for(i in Range(0, 5)) { … } (コンパイル時に展開する繰り返し)。( ) の中は読み飛ばし、本体は文として読む。
        if (Current.Kind == ShaderSyntaxKind.BadToken && Current.Text == "$"
            && Peek(1) is { Kind: ShaderSyntaxKind.IdentifierToken, Text: "for" }
            && Peek(2).Kind == ShaderSyntaxKind.OpenParenToken)
        {
            List<ShaderSyntaxToken> header = [Advance(), Advance()];
            header.AddRange(SkipBalanced(ShaderSyntaxKind.OpenParenToken, ShaderSyntaxKind.CloseParenToken));
            MarkUnanalyzed(header);
            return ParseStatement();
        }

        if (Current.Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return null;
        }

        // ループの名前 (outer: for (…))。case と default は除く。
        if (Peek(1).Kind == ShaderSyntaxKind.ColonToken
            && Current.Text is not ("case" or "default")
            && Peek(2).Kind == ShaderSyntaxKind.IdentifierToken
            && Peek(2).Text is "for" or "while" or "do" or "switch" or "if" or "{")
        {
            Advance();
            Advance();
            return ParseStatement();
        }

        switch (Current.Text)
        {
            // __target_switch { case hlsl: … } / spirv_asm { … }; / __intrinsic_asm "…";
            case "__target_switch" or "__stage_switch" when Peek(1).Kind == ShaderSyntaxKind.OpenBraceToken:
            case "spirv_asm" when Peek(1).Kind == ShaderSyntaxKind.OpenBraceToken:
            {
                List<ShaderSyntaxToken> skipped = [Advance()];
                skipped.AddRange(SkipBalanced(ShaderSyntaxKind.OpenBraceToken, ShaderSyntaxKind.CloseBraceToken));

                if (Current.Kind == ShaderSyntaxKind.SemicolonToken)
                {
                    skipped.Add(Advance());
                }

                MarkUnanalyzed(skipped);
                return new IncompleteStatementSyntax([.. skipped]);
            }

            case "__intrinsic_asm":
            {
                IncompleteDeclarationSyntax skipped = SkipToSemicolon(markNames: true);
                return new IncompleteStatementSyntax(skipped.Tokens);
            }

            // do { … } catch (e : E) { … } (while の無い do は、catch の付いたブロック)。
            case "do" when Peek(1).Kind == ShaderSyntaxKind.OpenBraceToken:
            {
                int start = Position;
                Advance();
                _ = SkipBalanced(ShaderSyntaxKind.OpenBraceToken, ShaderSyntaxKind.CloseBraceToken);
                bool caught = CurrentTextIs("catch");
                Position = start;

                if (!caught)
                {
                    return null;
                }

                Advance();
                return ParseBlock();
            }

            // break outer; / continue outer; (名前を付けたループを抜ける)
            case "break" or "continue" when Peek(1).Kind == ShaderSyntaxKind.IdentifierToken && Peek(2).Kind == ShaderSyntaxKind.SemicolonToken:
            {
                ShaderSyntaxToken keyword = Advance();
                Advance();
                return new JumpStatementSyntax(keyword, Advance());
            }

            // expand f(each xs); (可変長の値の並びの、要素ごとの式)。no_diff f(x); (微分しない呼び出しの式。no_diff float x; は宣言)
            case "expand" or "each":
            case "no_diff" when Peek(1).Kind == ShaderSyntaxKind.IdentifierToken && Peek(2).Kind == ShaderSyntaxKind.OpenParenToken:
            {
                ExpressionSyntax expression = ParseExpression();
                return new ExpressionStatementSyntax(expression, Expect(ShaderSyntaxKind.SemicolonToken, "';'"));
            }

            // try { … } catch (e : E) { … }
            case "try" when Peek(1).Kind == ShaderSyntaxKind.OpenBraceToken:
                Advance();
                return ParseBlock();

            // try f(); (戻り値を使わない、投げる関数の呼び出し)。宣言 (型 try の変数) ではなく、式の文として読む。
            case "try" when Peek(1).Kind == ShaderSyntaxKind.IdentifierToken:
            {
                ExpressionSyntax expression = ParseExpression();
                return new ExpressionStatementSyntax(expression, Expect(ShaderSyntaxKind.SemicolonToken, "';'"));
            }

            case "catch":
            {
                List<ShaderSyntaxToken> skipped = [Advance()];

                if (Current.Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    skipped.AddRange(SkipBalanced(ShaderSyntaxKind.OpenParenToken, ShaderSyntaxKind.CloseParenToken));
                }

                MarkUnanalyzed(skipped);
                return Current.Kind == ShaderSyntaxKind.OpenBraceToken ? ParseBlock() : new IncompleteStatementSyntax([.. skipped]);
            }

            // throw E.Fail;
            case "throw":
            {
                // throw の後ろは 1 つの式と ;。; が無ければ (throw E.Fail の次の行の return) 構文の誤りにする。
                ShaderSyntaxToken keyword = Advance();
                ExpressionSyntax thrown = ParseExpression();
                List<ShaderSyntaxToken> skipped = [keyword, .. thrown.DescendantTokens(), Expect(ShaderSyntaxKind.SemicolonToken, "';'")];
                MarkUnanalyzed(skipped);
                return new IncompleteStatementSyntax([.. skipped]);
            }

            // defer <文>
            case "defer":
            {
                Advance();
                return ParseStatement();
            }
        }

        return null;
    }

    /// <inheritdoc/>
    /// <remarks><c>if (let x = optional)</c>: 値があるときだけ x を宣言して通る。</remarks>
    protected override ExpressionSyntax? TryParseLanguageCondition()
    {
        if (!(CurrentTextIs("let") || CurrentTextIs("var")) || Peek(1).Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return null;
        }

        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken name = Advance();
        TypeSyntax type = Current.Kind == ShaderSyntaxKind.ColonToken && Advance() is not null ? ParseType() : MakeType(keyword);
        ShaderSyntaxToken? equals = Current.Kind == ShaderSyntaxKind.EqualsToken ? Advance() : null;
        ExpressionSyntax? value = equals is null ? null : ParseExpression();
        VariableDeclaratorSyntax declarator = new(name, [], [], equals, value);

        return new ConditionDeclarationExpressionSyntax(new VariableDeclarationSyntax(
            [keyword], type, [ShaderNodeOrTokenEntry.FromNode(declarator)], CreateMissingToken(ShaderSyntaxKind.SemicolonToken)));
    }

    /// <inheritdoc/>
    protected override ExpressionSyntax? TryParseLanguagePostfix(ExpressionSyntax expression)
    {
        // 引数の無い添字 (x[]。__subscript() -> T を呼ぶ)。何を返すかは決めないので、読み飛ばした式にする。
        if (Current.Kind == ShaderSyntaxKind.OpenBracketToken && Peek(1).Kind == ShaderSyntaxKind.CloseBracketToken)
        {
            return new IncompleteExpressionSyntax([.. expression.DescendantTokens(), Advance(), Advance()]);
        }

        // x is T / x as T
        if (Current.Kind == ShaderSyntaxKind.IdentifierToken
            && Current.Text is "is" or "as"
            && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            List<ShaderSyntaxToken> tokens = [.. expression.DescendantTokens(), Advance()];
            tokens.AddRange(SkipTypeTokens());
            MarkUnanalyzed(tokens);
            return new IncompleteExpressionSyntax([.. tokens]);
        }

        // 型引数を書いた名前 (coopVecLoad<4, float>(x)、MLVec<4>.fromArray(…)、apply<Foo>)。
        // 比較の連なりと取り違えないよう、型引数に書ける形 (名前・数・, ・ . ・ :: ・括弧の中の式) だけで閉じていて、
        // 後ろに ( ・ ) ・ , ・ . ・ ; ・ ] が続くときに限る。
        if (Current.Kind == ShaderSyntaxKind.LessThanToken
            && expression is IdentifierExpressionSyntax or MemberAccessExpressionSyntax
            && ScanTypeArgumentsEnd(0) is int close
            && Peek(close + 1).Kind is ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.CommaToken
                or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBracketToken or ShaderSyntaxKind.ColonColonToken)
        {
            ShaderSyntaxToken openAngle = Advance();
            ImmutableArray<ShaderSyntaxToken>.Builder arguments = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

            for (int i = 1; i < close; i++)
            {
                arguments.Add(Advance());
            }

            ShaderSyntaxToken closeAngle = Advance();
            MarkUnanalyzed(arguments);

            return expression is IdentifierExpressionSyntax identifier
                ? new GenericNameExpressionSyntax(identifier.Identifier, openAngle, arguments.ToImmutable(), closeAngle)
                : new IncompleteExpressionSyntax([.. expression.DescendantTokens(), openAngle, .. arguments, closeAngle]);
        }

        // p->x (ポインターのメンバー)
        if (IsArrow() && Peek(2).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            ShaderSyntaxToken minus = Advance();
            Advance();
            ShaderSyntaxToken name = Advance();
            return new MemberAccessExpressionSyntax(expression, minus, name);
        }

        return null;
    }

    /// <inheritdoc/>
    protected override ExpressionSyntax? TryParseLanguagePrimary()
    {
        // 1.0d・0ZZ・0x1p0g (数のすぐ後ろに続けた名前)。字句解析は数と名前に分けるが、slangc は数の接尾辞として読み、使えない接尾辞の誤りにする。
        // 0.xx (数のスウィズル) は、数が . で終わるので除く。
        if (Current.Kind == ShaderSyntaxKind.NumericLiteralToken
            && !Current.Text.EndsWith('.')
            && Peek(1) is { Kind: ShaderSyntaxKind.IdentifierToken } glued
            && ReferenceEquals(glued.Source, Current.Source)
            && glued.Span.Start == Current.Span.End)
        {
            ShaderSyntaxToken literal = Current;
            string combined = ShaderLiteral.GetSuffix(literal) + glued.Text;

            // 1zU (z の後ろの u を字句解析が名前として分けたもの) は、続けて 1 つの正しい接尾辞になる。
            if (!(combined.All(c => c is 'u' or 'U' or 'l' or 'L' or 'z' or 'Z') && ShaderLiteral.GetKind(literal) != ShaderLiteralKind.Floating
                  && IsNumericSuffixAccepted(combined, floating: false)))
            {
                ReportError($"数値リテラルの接尾辞 '{combined}' は Slang では使えません。");
            }

            Advance();
            Advance();
            return new LiteralExpressionSyntax(literal);
        }

        // 2.4#INFf (無限大のリテラル)。#INF の F は接尾辞ではないので、その後ろだけを見る。
        if (Current.Kind == ShaderSyntaxKind.NumericLiteralToken && Current.Text.IndexOf('#', StringComparison.Ordinal) is var hash and >= 0)
        {
            string rest = Current.Text[(hash + 1)..];
            string word = new([.. rest.TakeWhile(char.IsAsciiLetterUpper)]);
            string infinitySuffix = rest[word.Length..];

            if (infinitySuffix.Length > 0 && !IsNumericSuffixAccepted(infinitySuffix, floating: true))
            {
                ReportError($"数値リテラルの接尾辞 '{infinitySuffix}' は Slang では使えません。");
            }

            return new LiteralExpressionSyntax(Advance());
        }

        // 0LZ・1.0z (z で終わる接尾辞)。共通の接尾辞の読み方 (ShaderLiteral) は z を数えないので、ここで見る。
        if (Current.Kind == ShaderSyntaxKind.NumericLiteralToken && Current.Text.Length > 0 && Current.Text[^1] is 'z' or 'Z')
        {
            string text = Current.Text;
            bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            bool floating = text.Contains('.', StringComparison.Ordinal)
                            || (hex ? text.IndexOfAny(['p', 'P']) >= 0 : text.IndexOfAny(['e', 'E']) >= 0);
            int start = text.Length;

            while (start > 0 && text[start - 1] is 'u' or 'U' or 'l' or 'L' or 'z' or 'Z' || (start > 0 && !hex && text[start - 1] is 'f' or 'F' or 'h' or 'H'))
            {
                start--;
            }

            string suffix = text[start..];

            if (!IsNumericSuffixAccepted(suffix, floating))
            {
                ReportError($"数値リテラルの接尾辞 '{suffix}' は Slang では使えません。");
                return new LiteralExpressionSyntax(Advance());
            }
        }

        // ポインターの参照 (*p) とアドレス (&x)。
        if (Current.Kind is ShaderSyntaxKind.AsteriskToken or ShaderSyntaxKind.AmpersandToken)
        {
            ShaderSyntaxToken op = Advance();
            return new PrefixUnaryExpressionSyntax(op, ParseUnary());
        }

        // spirv_asm { … } (値を返す SPIR-V のアセンブリ)。
        if (CurrentTextIs("spirv_asm") && Peek(1).Kind == ShaderSyntaxKind.OpenBraceToken)
        {
            List<ShaderSyntaxToken> skipped = [Advance(), .. SkipBalanced(ShaderSyntaxKind.OpenBraceToken, ShaderSyntaxKind.CloseBraceToken)];
            MarkUnanalyzed(skipped);
            return new IncompleteExpressionSyntax([.. skipped]);
        }

        // ポインターの型への変換 ((float4*)p、(TestStruct*)(x))。
        if (Current.Kind == ShaderSyntaxKind.OpenParenToken
            && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken
            && ScanTypeEnd(1) is int typeEnd
            && Peek(typeEnd - 1).Kind == ShaderSyntaxKind.AsteriskToken
            && Peek(typeEnd).Kind == ShaderSyntaxKind.CloseParenToken)
        {
            ShaderSyntaxToken openParen = Advance();
            TypeSyntax type = ParseType();
            ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
            return new CastExpressionSyntax(openParen, type, closeParen, ParseUnary());
        }

        // expand f(each xs) / each xs は、値の並び (型の並び) を広げる式。型も個数も決められないので、語を残して読み飛ばした式にする
        // (呼び出しの実引数の数と型を数えないため)。
        if (Current.Kind == ShaderSyntaxKind.IdentifierToken && Current.Text is "expand" or "each"
            && Peek(1).Kind is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.OpenParenToken)
        {
            ShaderSyntaxToken pack = Advance();
            ExpressionSyntax operand = ParseUnary();
            return new IncompleteExpressionSyntax([pack, .. operand.DescendantTokens()]);
        }

        // new T(…)
        if (Current.Kind == ShaderSyntaxKind.IdentifierToken && Current.Text is "new"
            && Peek(1).Kind is ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.OpenParenToken)
        {
            Advance();
            return ParseUnary();
        }

        // no_diff f(x) / detach(x) は値をそのまま返す (微分を止める)。
        if (CurrentTextIs("no_diff"))
        {
            Advance();
            return ParseUnary();
        }

        // try f(…)
        if (CurrentTextIs("try") && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            Advance();
            return ParseExpression();
        }

        // ラムダ式 (int x) => x + 1 / (x) { … }
        if (Current.Kind == ShaderSyntaxKind.OpenParenToken)
        {
            int start = Position;
            List<ShaderSyntaxToken> tokens = [.. SkipBalanced(ShaderSyntaxKind.OpenParenToken, ShaderSyntaxKind.CloseParenToken)];

            if (IsFatArrow())
            {
                tokens.Add(Advance());
                tokens.Add(Advance());

                if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
                {
                    tokens.AddRange(SkipBalanced(ShaderSyntaxKind.OpenBraceToken, ShaderSyntaxKind.CloseBraceToken));
                }
                else
                {
                    tokens.AddRange(SkipExpressionTokens());
                }

                MarkUnanalyzed(tokens);
                return new IncompleteExpressionSyntax([.. tokens]);
            }

            Position = start;
        }

        return null;
    }

    /// <summary><c>func name&lt;T&gt;(a : T, b : U) -&gt; R where … { … }</c> を読む。</summary>
    private DeclarationSyntax ParseFunc(ImmutableArray<AttributeSyntax> attributes, ImmutableArray<ShaderSyntaxToken> modifiers)
    {
        ShaderSyntaxToken keyword = Advance();

        if (Current.Kind != ShaderSyntaxKind.IdentifierToken)
        {
            return RecoverDeclaration(DescribeExpected("関数の名前"));
        }

        ShaderSyntaxToken name = Advance();
        _ = TryParseGenericParameters();

        if (Current.Kind != ShaderSyntaxKind.OpenParenToken)
        {
            return RecoverDeclaration(DescribeExpected("'('"));
        }

        (ShaderSyntaxToken openParen, ImmutableArray<ShaderNodeOrTokenEntry> parameters, ShaderSyntaxToken closeParen) = ParseParameterList(typeAfterName: true);

        // -> R。書かなければ void。
        TypeSyntax returnType = MakeType(SynthesizeVoid(keyword));

        if (IsArrow())
        {
            Advance();
            Advance();
            returnType = ParseType();
        }

        SkipDeclarationSuffix();
        return FinishFunction(attributes, [.. modifiers, keyword], returnType, name, openParen, parameters, closeParen);
    }

    /// <summary><c>__init(…) { … }</c> (構造体の作り方) を読む。</summary>
    private DeclarationSyntax ParseInitializerFunction(ImmutableArray<AttributeSyntax> attributes, ImmutableArray<ShaderSyntaxToken> modifiers)
    {
        ShaderSyntaxToken name = Advance();
        _ = TryParseGenericParameters();

        if (Current.Kind != ShaderSyntaxKind.OpenParenToken)
        {
            return RecoverDeclaration(DescribeExpected("'('"));
        }

        (ShaderSyntaxToken openParen, ImmutableArray<ShaderNodeOrTokenEntry> parameters, ShaderSyntaxToken closeParen) = ParseParameterList(typeAfterName: false);
        SkipDeclarationSuffix();
        return FinishFunction(attributes, modifiers, MakeType(SynthesizeVoid(name)), name, openParen, parameters, closeParen);
    }

    /// <summary>関数の本体か <c>;</c> を読んで、関数の宣言を作る。</summary>
    private FunctionDeclarationSyntax FinishFunction(
        ImmutableArray<AttributeSyntax> attributes,
        ImmutableArray<ShaderSyntaxToken> modifiers,
        TypeSyntax returnType,
        ShaderSyntaxToken name,
        ShaderSyntaxToken openParen,
        ImmutableArray<ShaderNodeOrTokenEntry> parameters,
        ShaderSyntaxToken closeParen)
    {
        ImmutableArray<ShaderSyntaxToken> genericParameters = TakeGenericParameters();

        if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
        {
            BlockStatementSyntax body = IsFromOwnSource(Current) ? ParseBlock() : SkipBlock();
            return new FunctionDeclarationSyntax(attributes, modifiers, returnType, name, openParen, parameters, closeParen, [], body, null, default, default, default)
            {
                GenericParameterTokens = genericParameters,
            };
        }

        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");
        return new FunctionDeclarationSyntax(attributes, modifiers, returnType, name, openParen, parameters, closeParen, [], null, semicolon, default, default, default)
        {
            GenericParameterTokens = genericParameters,
        };
    }

    /// <summary>仮引数の並び <c>( … )</c> を読む。</summary>
    /// <param name="typeAfterName"><c>func</c> の形 (<c>a : T</c>) か。</param>
    private (ShaderSyntaxToken OpenParen, ImmutableArray<ShaderNodeOrTokenEntry> Parameters, ShaderSyntaxToken CloseParen) ParseParameterList(bool typeAfterName)
    {
        ShaderSyntaxToken openParen = Advance();
        ImmutableArray<ShaderNodeOrTokenEntry>.Builder parameters = ImmutableArray.CreateBuilder<ShaderNodeOrTokenEntry>();

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.CloseParenToken)
        {
            int before = Position;
            // func でも、型を先に書く仮引数 (func f(Buf* buf)) を書ける。
            bool nameColonType = typeAfterName && LooksLikeNameColonTypeParameter();
            parameters.Add(ShaderNodeOrTokenEntry.FromNode(nameColonType ? ParseNameColonTypeParameter() : ParseParameter()));

            if (Current.Kind == ShaderSyntaxKind.CommaToken)
            {
                parameters.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
                continue;
            }

            if (Position == before)
            {
                parameters.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
            }

            if (Current.Kind is not (ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.CommaToken))
            {
                break;
            }
        }

        ShaderSyntaxToken closeParen = Expect(ShaderSyntaxKind.CloseParenToken, "')'");
        return (openParen, parameters.ToImmutable(), closeParen);
    }

    /// <summary>今の位置の仮引数が <c>名前 : 型</c> の形か (修飾子を越えて見る)。</summary>
    private bool LooksLikeNameColonTypeParameter()
    {
        int i = 0;

        while (Peek(i).Kind == ShaderSyntaxKind.IdentifierToken && SlangKeywords.IsDeclarationModifier(Peek(i).Text))
        {
            i++;
        }

        return Peek(i).Kind == ShaderSyntaxKind.IdentifierToken && Peek(i + 1).Kind == ShaderSyntaxKind.ColonToken;
    }

    /// <summary><c>func</c> の仮引数 (<c>inout a : T = 1</c>) を読む。</summary>
    private ParameterSyntax ParseNameColonTypeParameter()
    {
        ImmutableArray<ShaderSyntaxToken> modifiers = ParseModifiers();
        ShaderSyntaxToken name = Current.Kind == ShaderSyntaxKind.IdentifierToken ? Advance() : CreateMissingToken(ShaderSyntaxKind.IdentifierToken);
        TypeSyntax type = Current.Kind == ShaderSyntaxKind.ColonToken && Advance() is not null
            ? ParseTypeOrFunctionType()
            : MakeType(CreateMissingToken(ShaderSyntaxKind.IdentifierToken));
        ImmutableArray<ShaderSyntaxToken> ranks = ParseArrayRanks();
        ImmutableArray<SemanticSyntax> semantics = ParseSemantics();

        ShaderSyntaxToken? equals = null;
        ExpressionSyntax? defaultValue = null;

        if (Current.Kind == ShaderSyntaxKind.EqualsToken)
        {
            equals = Advance();
            defaultValue = ParseExpression();
        }

        return new ParameterSyntax(modifiers, type, name, ranks, semantics, equals, defaultValue);
    }

    /// <summary>型か、関数の型 (<c>functype(int, int) -&gt; float</c>) を読む。関数の型は、名前だけの分からない型にする。</summary>
    private TypeSyntax ParseTypeOrFunctionType()
    {
        if (!CurrentTextIs("functype") || Peek(1).Kind != ShaderSyntaxKind.OpenParenToken)
        {
            return ParseType();
        }

        ShaderSyntaxToken keyword = Advance();
        List<ShaderSyntaxToken> skipped = SkipBalanced(ShaderSyntaxKind.OpenParenToken, ShaderSyntaxKind.CloseParenToken);

        if (IsArrow())
        {
            skipped.Add(Advance());
            skipped.Add(Advance());
            skipped.AddRange(SkipTypeTokens());
        }

        MarkUnanalyzed(skipped);
        return MakeType(keyword);
    }

    /// <summary><c>let x = 1;</c> / <c>var y : float = 2.0;</c> / <c>let a = 1, b = 2;</c> を読む。</summary>
    /// <remarks>型を書かない <c>let</c> / <c>var</c> は、型の名前をキーワードのトークンにする (型は推論されるが、今は分からないものとして扱う)。</remarks>
    private DeclarationSyntax ParseLetOrVar(ImmutableArray<ShaderSyntaxToken> modifiers)
    {
        ShaderSyntaxToken keyword = Advance();
        ImmutableArray<ShaderNodeOrTokenEntry>.Builder declarators = ImmutableArray.CreateBuilder<ShaderNodeOrTokenEntry>();
        TypeSyntax? type = null;

        while (!AtEnd)
        {
            ShaderSyntaxToken name = Current.Kind == ShaderSyntaxKind.IdentifierToken ? Advance() : CreateMissingToken(ShaderSyntaxKind.IdentifierToken);

            if (Current.Kind == ShaderSyntaxKind.ColonToken)
            {
                Advance();
                type ??= ParseType();
            }

            ImmutableArray<ShaderSyntaxToken> ranks = ParseArrayRanks();
            ShaderSyntaxToken? equals = null;
            ExpressionSyntax? initializer = null;

            if (Current.Kind == ShaderSyntaxKind.EqualsToken)
            {
                equals = Advance();
                initializer = ParseInitializer();
            }

            declarators.Add(ShaderNodeOrTokenEntry.FromNode(new VariableDeclaratorSyntax(name, ranks, [], equals, initializer)));

            if (Current.Kind != ShaderSyntaxKind.CommaToken)
            {
                break;
            }

            declarators.Add(ShaderNodeOrTokenEntry.FromToken(Advance()));
        }

        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");

        // let は書き換えられない (const と同じ)。
        ImmutableArray<ShaderSyntaxToken> all = [.. modifiers, keyword];
        return new VariableDeclarationSyntax(all, type ?? MakeType(keyword), declarators.ToImmutable(), semicolon);
    }

    /// <summary><c>cbuffer</c> / <c>tbuffer</c> を読む (HLSL と同じ)。</summary>
    private ConstantBufferDeclarationSyntax ParseConstantBuffer()
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken? name = Current.Kind == ShaderSyntaxKind.IdentifierToken ? Advance() : null;
        ImmutableArray<SemanticSyntax> semantics = ParseSemantics();

        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<DeclarationSyntax> members = ParseMemberDeclarations();
        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");

        // cbuffer の後ろの名前 (cbuffer CBuffer { … } cb;)。
        if (Current.Kind == ShaderSyntaxKind.IdentifierToken && Peek(1).Kind == ShaderSyntaxKind.SemicolonToken)
        {
            MarkUnanalyzed([Advance()]);
        }

        ShaderSyntaxToken? semicolon = Current.Kind == ShaderSyntaxKind.SemicolonToken ? Advance() : null;

        return new ConstantBufferDeclarationSyntax(keyword, name, semantics, openBrace, members, closeBrace, semicolon);
    }

    /// <summary><c>namespace a.b { … }</c> を読む。名前は <c>.</c> と <c>::</c> で区切ってよい。</summary>
    private DeclarationSyntax ParseNamespace()
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken? name = Current.Kind == ShaderSyntaxKind.IdentifierToken ? Advance() : null;
        List<ShaderSyntaxToken> names = name is null ? [] : [name];

        while (Current.Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken)
        {
            Advance();
            name = Advance();
            names.Add(name);
        }

        // 名前空間の名前は、名前の修飾 (ns1.ns2.g()) に使う。宣言として残らないので、名前を解析していない場所の名前にする。
        MarkUnanalyzed(names);

        // namespace a; (ファイル全体の名前空間)
        if (Current.Kind == ShaderSyntaxKind.SemicolonToken)
        {
            return new IncompleteDeclarationSyntax([keyword, .. name is null ? [] : new[] { name }, Advance()]);
        }

        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<DeclarationSyntax> members = ParseMemberDeclarations();
        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");

        return new NamespaceDeclarationSyntax(keyword, name, openBrace, members, closeBrace);
    }

    /// <summary><c>extension T : IFoo { … }</c> を読み飛ばす。</summary>
    private IncompleteDeclarationSyntax SkipTypeLikeDeclaration()
    {
        List<ShaderSyntaxToken> skipped = [];

        while (!AtEnd && Current.Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
        {
            skipped.Add(Advance());
        }

        if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
        {
            skipped.AddRange(SkipBalanced(ShaderSyntaxKind.OpenBraceToken, ShaderSyntaxKind.CloseBraceToken));
        }

        if (Current.Kind == ShaderSyntaxKind.SemicolonToken)
        {
            skipped.Add(Advance());
        }

        MarkUnanalyzed(skipped);
        return new IncompleteDeclarationSyntax([.. skipped]);
    }

    /// <summary><c>property x : T { get { … } set { … } }</c> と <c>__subscript(…) -&gt; T { … }</c> を読み飛ばす。</summary>
    private IncompleteDeclarationSyntax SkipPropertyOrSubscript()
    {
        List<ShaderSyntaxToken> skipped = [];

        while (!AtEnd && Current.Kind is not (ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken))
        {
            if (Current.Kind == ShaderSyntaxKind.OpenParenToken)
            {
                skipped.AddRange(SkipBalanced(ShaderSyntaxKind.OpenParenToken, ShaderSyntaxKind.CloseParenToken));
                continue;
            }

            skipped.Add(Advance());
        }

        if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
        {
            ReadAccessors(skipped);
        }
        else if (Current.Kind == ShaderSyntaxKind.SemicolonToken)
        {
            skipped.Add(Advance());
        }

        MarkUnanalyzed(skipped);
        return new IncompleteDeclarationSyntax([.. skipped]);
    }

    /// <summary>
    /// property と __subscript の <c>{ get { … } set; }</c> を読む。accessor の本体は文として読み (構文の誤りを報告する)、木には残さない。
    /// </summary>
    /// <remarks>accessor の前には、属性 (<c>[mutating]</c>) と修飾子 (<c>constexpr</c>) を書ける。</remarks>
    private void ReadAccessors(List<ShaderSyntaxToken> skipped)
    {
        skipped.Add(Advance());

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.CloseBraceToken)
        {
            if (Current.Kind == ShaderSyntaxKind.OpenBracketToken)
            {
                skipped.AddRange(SkipBalanced(ShaderSyntaxKind.OpenBracketToken, ShaderSyntaxKind.CloseBracketToken));
                continue;
            }

            if (Current.Kind == ShaderSyntaxKind.IdentifierToken && Current.Text is "get" or "set" or "ref" or "modify")
            {
                skipped.Add(Advance());

                if (Current.Kind == ShaderSyntaxKind.OpenParenToken)
                {
                    skipped.AddRange(SkipBalanced(ShaderSyntaxKind.OpenParenToken, ShaderSyntaxKind.CloseParenToken));
                }

                if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
                {
                    skipped.AddRange(ParseBlock().DescendantTokens());
                }
                else
                {
                    skipped.Add(Expect(ShaderSyntaxKind.SemicolonToken, "';'"));
                }

                continue;
            }

            // constexpr get の修飾子
            if (Current.Kind == ShaderSyntaxKind.IdentifierToken && Peek(1).Kind == ShaderSyntaxKind.IdentifierToken)
            {
                skipped.Add(Advance());
                continue;
            }

            // accessor でないもの (書きかけの se など)。報告して、閉じ括弧まで読み飛ばす。
            ReportError(DescribeExpected("accessor (get・set・ref・modify)"));
            int depth = 1;

            while (!AtEnd)
            {
                if (Current.Kind == ShaderSyntaxKind.OpenBraceToken)
                {
                    depth++;
                }
                else if (Current.Kind == ShaderSyntaxKind.CloseBraceToken && --depth == 0)
                {
                    break;
                }

                skipped.Add(Advance());
            }
        }

        skipped.Add(Expect(ShaderSyntaxKind.CloseBraceToken, "'}'"));
    }

    /// <summary><c>;</c> まで読み飛ばす。</summary>
    private IncompleteDeclarationSyntax SkipToSemicolon(bool markNames)
    {
        List<ShaderSyntaxToken> skipped = [];

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.SemicolonToken)
        {
            skipped.Add(Advance());
        }

        skipped.Add(Expect(ShaderSyntaxKind.SemicolonToken, "';'"));

        if (markNames)
        {
            MarkUnanalyzed(skipped);
        }

        return new IncompleteDeclarationSyntax([.. skipped]);
    }

    /// <summary>対応する閉じ括弧まで読み飛ばす (今の位置が開き括弧)。</summary>
    private List<ShaderSyntaxToken> SkipBalanced(ShaderSyntaxKind open, ShaderSyntaxKind close)
    {
        List<ShaderSyntaxToken> skipped = [];
        int depth = 0;

        while (!AtEnd)
        {
            ShaderSyntaxToken token = Advance();
            skipped.Add(token);

            if (token.Kind == open)
            {
                depth++;
            }
            else if (token.Kind == close && --depth == 0)
            {
                break;
            }
        }

        return skipped;
    }

    /// <summary>型を読み飛ばす (名前、<c>.</c> / <c>::</c> の修飾、<c>&lt;…&gt;</c>、<c>*</c>、<c>[…]</c>)。</summary>
    private List<ShaderSyntaxToken> SkipTypeTokens()
    {
        int start = Position;
        _ = ParseType();
        List<ShaderSyntaxToken> skipped = [];

        for (int i = start; i < Position; i++)
        {
            skipped.Add(Peek(i - Position));
        }

        return skipped;
    }

    /// <summary>式を読み飛ばす (括弧の外の <c>,</c> <c>)</c> <c>;</c> <c>}</c> まで)。</summary>
    private List<ShaderSyntaxToken> SkipExpressionTokens()
    {
        List<ShaderSyntaxToken> skipped = [];
        int depth = 0;

        while (!AtEnd)
        {
            if (depth == 0 && Current.Kind is ShaderSyntaxKind.CommaToken or ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.CloseBracketToken)
            {
                break;
            }

            depth += Current.Kind switch
            {
                ShaderSyntaxKind.OpenParenToken or ShaderSyntaxKind.OpenBracketToken or ShaderSyntaxKind.OpenBraceToken => 1,
                ShaderSyntaxKind.CloseParenToken or ShaderSyntaxKind.CloseBracketToken or ShaderSyntaxKind.CloseBraceToken => -1,
                _ => 0,
            };
            skipped.Add(Advance());
        }

        return skipped;
    }

    /// <summary>
    /// 今の位置から <paramref name="offset"/> 先の <c>&lt;</c> から、型引数の並びの閉じ (<c>&gt;</c> か <c>&gt;&gt;</c>) までを調べる。
    /// </summary>
    /// <param name="offset">開き山括弧の位置 (今の位置からの差)。</param>
    /// <returns>閉じの位置 (今の位置からの差)。型引数の並びでなければ <see langword="null"/>。</returns>
    private int? ScanTypeArgumentsEnd(int offset)
    {
        int depth = 0;
        int parentheses = 0;

        for (int i = offset; i < offset + 128; i++)
        {
            ShaderSyntaxKind kind = Peek(i).Kind;

            if (parentheses > 0)
            {
                if (kind == ShaderSyntaxKind.OpenParenToken)
                {
                    parentheses++;
                }
                else if (kind == ShaderSyntaxKind.CloseParenToken)
                {
                    parentheses--;
                }
                else if (kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.CloseBraceToken or ShaderSyntaxKind.EndOfFileToken)
                {
                    return null;
                }

                continue;
            }

            switch (kind)
            {
                case ShaderSyntaxKind.LessThanToken:
                    depth++;
                    break;

                case ShaderSyntaxKind.GreaterThanToken:
                    if (--depth == 0)
                    {
                        return i;
                    }

                    break;

                case ShaderSyntaxKind.GreaterThanGreaterThanToken:
                    depth -= 2;
                    if (depth <= 0)
                    {
                        return depth == 0 ? i : null;
                    }

                    break;

                case ShaderSyntaxKind.OpenParenToken:
                    parentheses++;
                    break;

                case ShaderSyntaxKind.IdentifierToken or ShaderSyntaxKind.NumericLiteralToken or ShaderSyntaxKind.CommaToken
                    or ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken or ShaderSyntaxKind.AsteriskToken
                    or ShaderSyntaxKind.OpenBracketToken or ShaderSyntaxKind.CloseBracketToken or ShaderSyntaxKind.MinusToken
                    or ShaderSyntaxKind.BarToken or ShaderSyntaxKind.AmpersandToken or ShaderSyntaxKind.PlusToken:
                    break;

                default:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 今の位置から <paramref name="offset"/> 先の名前で始まる型 (<c>A.B</c>、<c>A&lt;T&gt;.B</c>、<c>A*</c>) の、後ろの位置を調べる。
    /// </summary>
    /// <param name="offset">名前の位置 (今の位置からの差)。</param>
    /// <returns>型の後ろの位置 (今の位置からの差)。型として読めなければ <see langword="null"/>。</returns>
    private int? ScanTypeEnd(int offset)
    {
        int i = offset + 1;

        while (true)
        {
            if (Peek(i).Kind == ShaderSyntaxKind.LessThanToken)
            {
                if (ScanTypeArgumentsEnd(i) is not int close)
                {
                    return null;
                }

                i = close + 1;
                continue;
            }

            if (Peek(i).Kind is ShaderSyntaxKind.DotToken or ShaderSyntaxKind.ColonColonToken && Peek(i + 1).Kind == ShaderSyntaxKind.IdentifierToken)
            {
                i += 2;
                continue;
            }

            break;
        }

        while (Peek(i).Kind == ShaderSyntaxKind.AsteriskToken)
        {
            i++;
        }

        return i;
    }

    /// <summary>今の位置の <c>struct</c> が、本体の代わりに <c>= 型;</c> を書いたものか。</summary>
    private bool LinksToType()
    {
        int angle = 0;

        for (int i = 1; i < 64; i++)
        {
            switch (Peek(i).Kind)
            {
                case ShaderSyntaxKind.LessThanToken:
                    angle++;
                    break;

                case ShaderSyntaxKind.GreaterThanToken:
                    angle--;
                    break;

                case ShaderSyntaxKind.GreaterThanGreaterThanToken:
                    angle -= 2;
                    break;

                // 型の仮引数の既定値 (struct A<let N : int = 2>) の = は数えない。
                case ShaderSyntaxKind.EqualsToken when angle <= 0:
                    return true;

                // 本体の無い宣言 (extern struct External : IHasElement;)。
                case ShaderSyntaxKind.SemicolonToken when angle <= 0 && i > 2:
                    return true;

                case ShaderSyntaxKind.OpenBraceToken or ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.EndOfFileToken:
                    return false;
            }
        }

        return false;
    }

    /// <summary>今の位置が <c>-&gt;</c> (隣り合う <c>-</c> と <c>&gt;</c>) か。</summary>
    private bool IsArrow()
        => Current.Kind == ShaderSyntaxKind.MinusToken
           && Peek(1).Kind == ShaderSyntaxKind.GreaterThanToken
           && Peek(1).Span.Start == Current.Span.End;

    /// <summary>今の位置が <c>=&gt;</c> (隣り合う <c>=</c> と <c>&gt;</c>) か。</summary>
    private bool IsFatArrow()
        => Current.Kind == ShaderSyntaxKind.EqualsToken
           && Peek(1).Kind == ShaderSyntaxKind.GreaterThanToken
           && Peek(1).Span.Start == Current.Span.End;

    /// <summary>書かれていない戻り値の型 void を、キーワードの位置に幅 0 のトークンとして作る。</summary>
    private static ShaderSyntaxToken SynthesizeVoid(ShaderSyntaxToken at)
        => new(ShaderSyntaxKind.IdentifierToken, at.Source, new TextSpan(at.Span.Start, 0), "void");

    /// <summary>名前のトークンだけの型を作る。</summary>
    private static TypeSyntax MakeType(ShaderSyntaxToken name) => new([], name, [], null, [], null, null, null);
}
