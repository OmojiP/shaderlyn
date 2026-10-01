using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.ShaderLab.Parsing;

/// <summary>
/// ShaderLab のトークン列を構文木へ組み立てる再帰下降パーサ。
/// </summary>
/// <remarks>
/// <para>
/// <b>このパーサは決して例外を投げない。</b>
/// 期待するトークンが無い場合は長さ 0 の欠落トークンを合成して木の形を保ち、
/// 解釈できない区間は <see cref="SkippedTokensSyntax"/> へ回収する。
/// 壊れた入力を日常的に与えられるリンタとして必須の性質である。
/// </para>
/// <para>
/// 読み飛ばしたトークンも木の上に残すため、どれだけ壊れた入力でも
/// ラウンドトリップ (元テキストの完全復元) は成立する。
/// </para>
/// </remarks>
internal sealed class ShaderLabParser
{
    /// <summary>
    /// 専用の構文を持ち、汎用命令として解釈してはならないキーワード。
    /// </summary>
    /// <remarks>
    /// ここに無いものはすべて <see cref="CommandSyntax"/> として扱う。
    /// ShaderLab の命令は数十種類あり Unity のバージョンで増減するため、
    /// 命令ごとに構文を作り分けると新しい命令が出るたびにパーサの変更が必要になる。
    /// 「知らない命令は構文エラーにしない」という方針にすることで、
    /// ツールが未対応の新命令を使っているだけのシェーダーを壊れていると誤判定せずに済む。
    /// </remarks>
    private static readonly string[] BlockKeywords =
        ["Properties", "SubShader", "Pass", "Category", "Tags", "Stencil", "GrabPass"];

    /// <summary>
    /// 引数の並びを打ち切る目印として使う、よく知られた命令名。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>これは値の検証用の表ではなく、命令の切れ目を見つけるためだけの目印である。</b>
    /// ここに無い命令も通常どおり解析されるし、ここに有ることが正しさを意味するわけでもない。
    /// 値の妥当性はルール側 (SL1021) が別の表を使って判断する。
    /// </para>
    /// <para>
    /// これが必要なのは <c>Pass { Cull Off ZWrite Off }</c> のように
    /// 1 行に複数の命令を書けるためである。行末だけを終端とみなすと
    /// <c>Cull</c> が後続の <c>ZWrite Off</c> まで引数として飲み込んでしまう。
    /// </para>
    /// <para>
    /// この方式が破綻するのは「ある命令の引数として、別の命令と同じ名前の値を取る」場合である。
    /// 現在の ShaderLab にその組み合わせは無い
    /// (<c>Cull Back</c> の <c>Back</c> も <c>Stencil</c> の <c>Pass Keep</c> の <c>Keep</c> も命令名ではない)。
    /// 将来そうした命令が追加された場合はここから外すこと。
    /// </para>
    /// </remarks>
    private static readonly FrozenSet<string> CommandNames = new[]
    {
        // レンダーステート
        "Cull", "ZWrite", "ZTest", "ZClip", "Blend", "BlendOp", "ColorMask", "Offset",
        "AlphaToMask", "Conservative", "Lighting", "LOD", "Fog",
        // 宣言・参照
        "Name", "Fallback", "CustomEditor", "UsePass", "Dependency",
        // ステンシル
        "Ref", "ReadMask", "WriteMask",
        "Comp", "CompBack", "CompFront",
        "Pass", "PassBack", "PassFront",
        "Fail", "FailBack", "FailFront",
        "ZFail", "ZFailBack", "ZFailFront",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly SourceText _text;
    private readonly ImmutableArray<SyntaxToken> _tokens;
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
    private int _index;

    /// <summary>
    /// パーサを生成する。
    /// </summary>
    /// <param name="text">解析対象のソーステキスト。</param>
    /// <param name="tokens">字句解析済みのトークン列。末尾は終端トークンでなければならない。</param>
    public ShaderLabParser(SourceText text, ImmutableArray<SyntaxToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
        _tokens = tokens;
    }

    /// <summary>現在位置のトークン。</summary>
    private SyntaxToken Current => _tokens[Math.Min(_index, _tokens.Length - 1)];

    /// <summary>現在位置がファイル終端かどうか。</summary>
    private bool AtEnd => Current.Kind == SyntaxKind.EndOfFileToken;

    /// <summary>
    /// ファイル全体を解析する。
    /// </summary>
    /// <param name="diagnostics">解析中に検出した構文エラー。</param>
    /// <returns>構文木の根。</returns>
    /// <remarks>
    /// 1 ファイルに <c>Shader</c> 宣言は 1 つだけ置ける。
    /// その前後にある解釈できないトークンは読み飛ばしとして回収し、構文エラーを報告する。
    /// </remarks>
    public ShaderLabCompilationUnitSyntax ParseCompilationUnit(out ImmutableArray<Diagnostic> diagnostics)
    {
        SkippedTokensSyntax? leadingSkipped = SkipUntilShaderKeyword();

        ShaderDeclarationSyntax? shader = null;
        if (!AtEnd)
        {
            shader = ParseShaderDeclaration();
        }
        else if (leadingSkipped is null)
        {
            ReportError(Current.Span, "'Shader' 宣言が見つかりません。ShaderLab ファイルは Shader \"名前\" { ... } で始まる必要があります。");
        }

        SkippedTokensSyntax? trailingSkipped = null;
        if (!AtEnd)
        {
            ReportError(Current.Span, "'Shader' 宣言より後に余分な記述があります。1 つのファイルに書ける Shader 宣言は 1 つだけです。");
            trailingSkipped = SkipToEnd();
        }

        SyntaxToken endOfFile = Current;
        diagnostics = _diagnostics.ToImmutable();

        return new ShaderLabCompilationUnitSyntax(shader, leadingSkipped, trailingSkipped, endOfFile);
    }

    /// <summary>
    /// <c>Shader</c> キーワードが現れるまでトークンを読み飛ばす。
    /// </summary>
    /// <returns>読み飛ばしたトークン。読み飛ばしが無ければ <see langword="null"/>。</returns>
    private SkippedTokensSyntax? SkipUntilShaderKeyword()
    {
        if (AtEnd || IsKeyword(Current, "Shader"))
        {
            return null;
        }

        ReportError(Current.Span, "ファイルの先頭に 'Shader' 以外の記述があります。");

        ImmutableArray<SyntaxToken>.Builder skipped = ImmutableArray.CreateBuilder<SyntaxToken>();
        while (!AtEnd && !IsKeyword(Current, "Shader"))
        {
            skipped.Add(Advance());
        }

        return new SkippedTokensSyntax(skipped.ToImmutable());
    }

    /// <summary>ファイル終端まで全トークンを読み飛ばす。</summary>
    /// <returns>読み飛ばしたトークン。</returns>
    private SkippedTokensSyntax SkipToEnd()
    {
        ImmutableArray<SyntaxToken>.Builder skipped = ImmutableArray.CreateBuilder<SyntaxToken>();
        while (!AtEnd)
        {
            skipped.Add(Advance());
        }

        return new SkippedTokensSyntax(skipped.ToImmutable());
    }

    /// <summary>
    /// <c>Shader "名前" { ... }</c> を解析する。
    /// </summary>
    /// <returns>解析した宣言。</returns>
    /// <remarks>文法: <c>ShaderDeclaration := 'Shader' StringLiteral Block</c></remarks>
    private ShaderDeclarationSyntax ParseShaderDeclaration()
    {
        SyntaxToken keyword = Advance();
        SyntaxToken name = Expect(SyntaxKind.StringLiteralToken, "シェーダー名の文字列");
        BlockSyntax body = ParseBlock();
        return new ShaderDeclarationSyntax(keyword, name, body);
    }

    /// <summary>
    /// 波括弧で囲まれた文の並びを解析する。
    /// </summary>
    /// <returns>解析したブロック。</returns>
    /// <remarks>
    /// <para>文法: <c>Block := '{' Statement* '}'</c></para>
    /// <para>
    /// 開き波括弧が無い場合も欠落トークンを合成して続行する。
    /// ここで解析を打ち切ると、その先にある正しい記述がすべて検査されなくなるためである。
    /// </para>
    /// </remarks>
    private BlockSyntax ParseBlock()
    {
        SyntaxToken openBrace = Expect(SyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<ShaderLabStatementSyntax>.Builder statements =
            ImmutableArray.CreateBuilder<ShaderLabStatementSyntax>();

        while (!AtEnd && Current.Kind != SyntaxKind.CloseBraceToken)
        {
            int positionBefore = _index;
            statements.Add(ParseStatement());

            // 解析が 1 トークンも進まなかった場合は無限ループになる。
            // 文法の想定外に落ちた時の最後の防壁として、必ず 1 トークン進めて読み飛ばす。
            if (_index == positionBefore)
            {
                statements.Add(new SkippedTokensSyntax([Advance()]));
            }
        }

        SyntaxToken closeBrace = Expect(SyntaxKind.CloseBraceToken, "'}'");
        return new BlockSyntax(openBrace, statements.ToImmutable(), closeBrace);
    }

    /// <summary>
    /// ブロック内の文を 1 つ解析する。
    /// </summary>
    /// <returns>解析した文。</returns>
    private ShaderLabStatementSyntax ParseStatement()
    {
        if (Current.Kind == SyntaxKind.ProgramBlockToken)
        {
            return new ProgramBlockSyntax(Advance());
        }

        if (Current.Kind != SyntaxKind.IdentifierToken)
        {
            ReportError(Current.Span, $"命令名が必要ですが '{Current.Text}' がありました。");
            return new SkippedTokensSyntax([Advance()]);
        }

        if (IsKeyword(Current, "Properties"))
        {
            return ParsePropertiesBlock();
        }

        if (IsKeyword(Current, "SubShader"))
        {
            return new SubShaderSyntax(Advance(), ParseBlock());
        }

        if (IsKeyword(Current, "Pass"))
        {
            return new PassSyntax(Advance(), ParseBlock());
        }

        if (IsKeyword(Current, "Category"))
        {
            return new CategorySyntax(Advance(), ParseBlock());
        }

        if (IsKeyword(Current, "Tags"))
        {
            return ParseTagsBlock();
        }

        if (IsKeyword(Current, "Stencil"))
        {
            return ParseStencilBlock();
        }

        if (IsKeyword(Current, "GrabPass"))
        {
            return ParseGrabPass();
        }

        return ParseCommand();
    }

    /// <summary>
    /// <c>Properties { ... }</c> を解析する。
    /// </summary>
    /// <returns>解析したブロック。</returns>
    /// <remarks>文法: <c>PropertiesBlock := 'Properties' '{' PropertyDeclaration* '}'</c></remarks>
    private PropertiesBlockSyntax ParsePropertiesBlock()
    {
        SyntaxToken keyword = Advance();
        SyntaxToken openBrace = Expect(SyntaxKind.OpenBraceToken, "'{'");

        ImmutableArray<PropertyDeclarationSyntax>.Builder properties =
            ImmutableArray.CreateBuilder<PropertyDeclarationSyntax>();
        ImmutableArray<SkippedTokensSyntax>.Builder skipped =
            ImmutableArray.CreateBuilder<SkippedTokensSyntax>();

        while (!AtEnd && Current.Kind != SyntaxKind.CloseBraceToken)
        {
            if (StartsPropertyDeclaration())
            {
                int positionBefore = _index;
                properties.Add(ParsePropertyDeclaration());

                if (_index != positionBefore)
                {
                    continue;
                }
            }

            skipped.Add(SkipToNextProperty());
        }

        SyntaxToken closeBrace = Expect(SyntaxKind.CloseBraceToken, "'}'");
        return new PropertiesBlockSyntax(
            keyword, openBrace, properties.ToImmutable(), skipped.ToImmutable(), closeBrace);
    }

    /// <summary>
    /// 現在位置がプロパティ宣言の始まりに見えるかを判定する。
    /// </summary>
    /// <returns>プロパティ宣言として解析を試みてよい場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 「識別子だから宣言だろう」と見切り発車で解析を始めてはならない。
    /// プロパティ宣言の解析は、型名の位置で任意の識別子を受け入れる。
    /// そのため壊れた記述に対して解析を始めてしまうと、
    /// <b>次の宣言の名前を型名として食べてしまい</b>、正しい宣言まで巻き添えで失われる。
    /// </para>
    /// <para>
    /// 属性の開始 <c>[</c> か、<c>識別子 (</c> という並びを確認してから解析に入ることで、
    /// 1 か所の書き損じが以降の宣言へ波及しないようにしている。
    /// </para>
    /// </remarks>
    private bool StartsPropertyDeclaration()
        => Current.Kind == SyntaxKind.OpenBracketToken
            || (Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.OpenParenToken);

    /// <summary>
    /// プロパティ宣言として解釈できない区間を、次のプロパティが始まりそうな位置まで読み飛ばす。
    /// </summary>
    /// <returns>読み飛ばしたトークン。</returns>
    /// <remarks>
    /// 同期点は「属性の開始 <c>[</c>」「<c>識別子 (</c> という並び」「ブロックの終わり <c>}</c>」の 3 つ。
    /// プロパティ宣言が必ずこのいずれかで始まることを利用して、
    /// 1 つのプロパティの書き損じが以降のプロパティすべてを巻き添えにしないようにしている。
    /// </remarks>
    private SkippedTokensSyntax SkipToNextProperty()
    {
        ReportError(Current.Span, $"プロパティ宣言として解釈できない記述があります: '{Current.Text}'");

        ImmutableArray<SyntaxToken>.Builder skipped = ImmutableArray.CreateBuilder<SyntaxToken>();
        skipped.Add(Advance());

        while (!AtEnd && Current.Kind != SyntaxKind.CloseBraceToken && !StartsPropertyDeclaration())
        {
            skipped.Add(Advance());
        }

        return new SkippedTokensSyntax(skipped.ToImmutable());
    }

    /// <summary>
    /// プロパティ宣言を 1 件解析する。
    /// </summary>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// 文法:
    /// <c>PropertyDeclaration := Attribute* Identifier '(' StringLiteral ',' PropertyType ')' '=' DefaultValue</c>
    /// </remarks>
    private PropertyDeclarationSyntax ParsePropertyDeclaration()
    {
        ImmutableArray<PropertyAttributeSyntax>.Builder attributes =
            ImmutableArray.CreateBuilder<PropertyAttributeSyntax>();

        while (Current.Kind == SyntaxKind.OpenBracketToken)
        {
            attributes.Add(ParsePropertyAttribute());
        }

        SyntaxToken name = Expect(SyntaxKind.IdentifierToken, "プロパティ名");
        SyntaxToken openParen = Expect(SyntaxKind.OpenParenToken, "'('");
        SyntaxToken displayName = Expect(SyntaxKind.StringLiteralToken, "インスペクタに表示する名前の文字列");
        SyntaxToken comma = Expect(SyntaxKind.CommaToken, "','");
        PropertyTypeSyntax type = ParsePropertyType();
        SyntaxToken closeParen = Expect(SyntaxKind.CloseParenToken, "')'");
        SyntaxToken equals = Expect(SyntaxKind.EqualsToken, "'='");
        PropertyDefaultValueSyntax? defaultValue = ParsePropertyDefaultValue();

        return new PropertyDeclarationSyntax(
            attributes.ToImmutable(), name, openParen, displayName, comma, type, closeParen, equals, defaultValue);
    }

    /// <summary>
    /// プロパティの属性を 1 件解析する。
    /// </summary>
    /// <returns>解析した属性。</returns>
    /// <remarks>
    /// 文法: <c>Attribute := '[' Identifier ( '(' Token* ')' )? ']'</c>
    /// 引数の中身は属性ごとに意味が異なるため、ここでは解釈せずトークン列として保持する。
    /// </remarks>
    private PropertyAttributeSyntax ParsePropertyAttribute()
    {
        SyntaxToken openBracket = Advance();
        SyntaxToken name = Expect(SyntaxKind.IdentifierToken, "属性名");

        SyntaxToken? openParen = null;
        SyntaxToken? closeParen = null;
        ImmutableArray<SyntaxToken>.Builder arguments = ImmutableArray.CreateBuilder<SyntaxToken>();

        if (Current.Kind == SyntaxKind.OpenParenToken)
        {
            openParen = Advance();

            while (!AtEnd
                && Current.Kind != SyntaxKind.CloseParenToken
                && Current.Kind != SyntaxKind.CloseBracketToken)
            {
                arguments.Add(Advance());
            }

            closeParen = Expect(SyntaxKind.CloseParenToken, "')'");
        }

        SyntaxToken closeBracket = Expect(SyntaxKind.CloseBracketToken, "']'");
        return new PropertyAttributeSyntax(
            openBracket, name, openParen, arguments.ToImmutable(), closeParen, closeBracket);
    }

    /// <summary>
    /// プロパティの型を解析する。
    /// </summary>
    /// <returns>解析した型。</returns>
    /// <remarks>
    /// 文法: <c>PropertyType := Identifier ( '(' Token* ')' )?</c>
    /// 丸括弧を伴うのは <c>Range(min, max)</c> の場合である。
    /// 型名の妥当性はここでは検査せず、ルール側 (SL1003) が判断する。
    /// </remarks>
    private PropertyTypeSyntax ParsePropertyType()
    {
        // 型名は 2D や 2DArray のように数字で始まりうるが、
        // これらは字句解析ツールが識別子として返す (ShaderLabLexer.LexNumericLiteral を参照)。
        SyntaxToken typeToken = Expect(SyntaxKind.IdentifierToken, "プロパティの型名");

        SyntaxToken? openParen = null;
        SyntaxToken? closeParen = null;
        ImmutableArray<SyntaxToken>.Builder arguments = ImmutableArray.CreateBuilder<SyntaxToken>();

        if (Current.Kind == SyntaxKind.OpenParenToken)
        {
            openParen = Advance();

            while (!AtEnd && Current.Kind != SyntaxKind.CloseParenToken && Current.Kind != SyntaxKind.CloseBraceToken)
            {
                arguments.Add(Advance());
            }

            closeParen = Expect(SyntaxKind.CloseParenToken, "')'");
        }

        return new PropertyTypeSyntax(typeToken, openParen, arguments.ToImmutable(), closeParen);
    }

    /// <summary>
    /// プロパティの既定値を解析する。
    /// </summary>
    /// <returns>解析した既定値。解釈できない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// 文法:
    /// <c>DefaultValue := Number | '(' Number ( ',' Number )* ')' | StringLiteral ( '{' Token* '}' )?</c>
    /// 先頭のトークン種別だけで一意に分岐できる。
    /// </remarks>
    private PropertyDefaultValueSyntax? ParsePropertyDefaultValue()
    {
        switch (Current.Kind)
        {
            case SyntaxKind.NumericLiteralToken:
                return new ScalarDefaultValueSyntax(Advance());

            case SyntaxKind.OpenParenToken:
                return ParseVectorDefaultValue();

            case SyntaxKind.StringLiteralToken:
                return ParseTextureDefaultValue();

            default:
                ReportError(Current.Span, $"プロパティの既定値が必要ですが '{Current.Text}' がありました。");
                return null;
        }
    }

    /// <summary>ベクトルまたは色の既定値を解析する。</summary>
    /// <returns>解析した既定値。</returns>
    private VectorDefaultValueSyntax ParseVectorDefaultValue()
    {
        SyntaxToken openParen = Advance();
        ImmutableArray<SyntaxToken>.Builder components = ImmutableArray.CreateBuilder<SyntaxToken>();

        while (!AtEnd && Current.Kind != SyntaxKind.CloseParenToken && Current.Kind != SyntaxKind.CloseBraceToken)
        {
            components.Add(Advance());
        }

        SyntaxToken closeParen = Expect(SyntaxKind.CloseParenToken, "')'");
        return new VectorDefaultValueSyntax(openParen, components.ToImmutable(), closeParen);
    }

    /// <summary>テクスチャの既定値を解析する。</summary>
    /// <returns>解析した既定値。</returns>
    private TextureDefaultValueSyntax ParseTextureDefaultValue()
    {
        SyntaxToken value = Advance();

        SyntaxToken? openBrace = null;
        SyntaxToken? closeBrace = null;
        ImmutableArray<SyntaxToken>.Builder body = ImmutableArray.CreateBuilder<SyntaxToken>();

        if (Current.Kind == SyntaxKind.OpenBraceToken)
        {
            openBrace = Advance();

            while (!AtEnd && Current.Kind != SyntaxKind.CloseBraceToken)
            {
                body.Add(Advance());
            }

            closeBrace = Expect(SyntaxKind.CloseBraceToken, "'}'");
        }

        return new TextureDefaultValueSyntax(value, openBrace, body.ToImmutable(), closeBrace);
    }

    /// <summary>
    /// <c>Tags { "キー" = "値" }</c> を解析する。
    /// </summary>
    /// <returns>解析したブロック。</returns>
    /// <remarks>文法: <c>TagsBlock := 'Tags' '{' ( StringLiteral '=' StringLiteral )* '}'</c></remarks>
    private TagsBlockSyntax ParseTagsBlock()
    {
        SyntaxToken keyword = Advance();
        SyntaxToken openBrace = Expect(SyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<TagSyntax>.Builder tags = ImmutableArray.CreateBuilder<TagSyntax>();

        while (!AtEnd && Current.Kind != SyntaxKind.CloseBraceToken)
        {
            if (Current.Kind != SyntaxKind.StringLiteralToken)
            {
                ReportError(Current.Span, $"タグ名の文字列が必要ですが '{Current.Text}' がありました。");
                Advance();
                continue;
            }

            SyntaxToken key = Advance();
            SyntaxToken equals = Expect(SyntaxKind.EqualsToken, "'='");
            SyntaxToken value = Expect(SyntaxKind.StringLiteralToken, "タグ値の文字列");
            tags.Add(new TagSyntax(key, equals, value));
        }

        SyntaxToken closeBrace = Expect(SyntaxKind.CloseBraceToken, "'}'");
        return new TagsBlockSyntax(keyword, openBrace, tags.ToImmutable(), closeBrace);
    }

    /// <summary>
    /// <c>Stencil { ... }</c> を解析する。
    /// </summary>
    /// <returns>解析したブロック。</returns>
    /// <remarks>
    /// 中身は <c>Ref 1</c> や <c>Comp Equal</c> といった命令の並びである。
    /// ここを汎用の文として解析してはならない。ステンシルの命令には <c>Pass Keep</c> があり、
    /// <c>Pass</c> が Pass ブロックの開始と誤って解釈されてしまうためである。
    /// </remarks>
    private StencilBlockSyntax ParseStencilBlock()
    {
        SyntaxToken keyword = Advance();
        SyntaxToken openBrace = Expect(SyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<CommandSyntax>.Builder commands = ImmutableArray.CreateBuilder<CommandSyntax>();

        while (!AtEnd && Current.Kind != SyntaxKind.CloseBraceToken)
        {
            if (Current.Kind != SyntaxKind.IdentifierToken)
            {
                ReportError(Current.Span, $"ステンシルの設定名が必要ですが '{Current.Text}' がありました。");
                Advance();
                continue;
            }

            commands.Add(ParseCommand());
        }

        SyntaxToken closeBrace = Expect(SyntaxKind.CloseBraceToken, "'}'");
        return new StencilBlockSyntax(keyword, openBrace, commands.ToImmutable(), closeBrace);
    }

    /// <summary>
    /// <c>GrabPass { }</c> を解析する。
    /// </summary>
    /// <returns>解析したブロック。</returns>
    private GrabPassSyntax ParseGrabPass()
    {
        SyntaxToken keyword = Advance();
        SyntaxToken openBrace = Expect(SyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<SyntaxToken>.Builder body = ImmutableArray.CreateBuilder<SyntaxToken>();

        while (!AtEnd && Current.Kind != SyntaxKind.CloseBraceToken)
        {
            body.Add(Advance());
        }

        SyntaxToken closeBrace = Expect(SyntaxKind.CloseBraceToken, "'}'");
        return new GrabPassSyntax(keyword, openBrace, body.ToImmutable(), closeBrace);
    }

    /// <summary>
    /// 命令を 1 つ解析する。
    /// </summary>
    /// <returns>解析した命令。</returns>
    /// <remarks>
    /// <para>文法: <c>Command := Identifier Argument*</c></para>
    /// <para>
    /// <b>ShaderLab には文の終端記号が無い</b>ため、引数がどこで終わるかを決める規則が必要になる。
    /// このパーサは<b>行末を終端とみなす</b>。ShaderLab は 1 行 1 命令で書かれるのが事実上の慣習であり、
    /// 命令ごとの引数の個数表を持たずに済むためである
    /// (表を持つと Unity のバージョンで命令が増えるたびに追従が必要になる)。
    /// </para>
    /// <para>
    /// 例外として、行末がカンマで終わっている場合は次の行へ継続する。
    /// <c>Blend One Zero,</c> のような明示的な継続を取りこぼさないためである。
    /// </para>
    /// <para>
    /// 行末だけでは不十分で、<c>Pass { Cull Off ZWrite Off }</c> のように
    /// 1 行に複数の命令を書く記法にも対応する必要がある。
    /// そのため、既に引数を 1 つ以上読んだあとで
    /// <see cref="CommandNames"/> に載っている名前が現れたらそこで打ち切る。
    /// </para>
    /// <para>
    /// 想定外の改行位置で命令が分断された場合、後半は名前だけの別の命令として解釈される。
    /// これが誤検出につながらないのは、未知の命令名をルールが問題として扱わないためである。
    /// </para>
    /// </remarks>
    private CommandSyntax ParseCommand()
    {
        SyntaxToken name = Advance();
        ImmutableArray<CommandArgumentSyntax>.Builder arguments =
            ImmutableArray.CreateBuilder<CommandArgumentSyntax>();

        SyntaxToken previous = name;
        int valueArgumentCount = 0;

        while (!AtEnd && IsArgumentStart(Current.Kind))
        {
            bool continuesAcrossLine = previous.Kind == SyntaxKind.CommaToken;
            if (EndsLine(previous) && !continuesAcrossLine)
            {
                break;
            }

            // 既に引数を読んだあとに別の命令名が現れたら、そこが次の命令の始まりである。
            if (valueArgumentCount > 0
                && Current.Kind == SyntaxKind.IdentifierToken
                && CommandNames.Contains(Current.Text))
            {
                break;
            }

            if (Current.Kind == SyntaxKind.OpenBracketToken)
            {
                arguments.Add(ParsePropertyReferenceArgument(out previous));
                valueArgumentCount++;
            }
            else if (Current.Kind == SyntaxKind.CommaToken)
            {
                previous = Advance();
                arguments.Add(new ArgumentSeparatorSyntax(previous));
            }
            else
            {
                previous = Advance();
                arguments.Add(new LiteralArgumentSyntax(previous));
                valueArgumentCount++;
            }
        }

        // Fog { Mode Off } や SetTexture [_MainTex] { combine primary } のように
        // 波括弧の本体を取る命令がある。固定機能パイプライン時代の記法だが、
        // Unity 同梱のシェーダーに現在も残っており、扱えないと構文エラーになる。
        SyntaxToken? openBrace = null;
        SyntaxToken? closeBrace = null;
        ImmutableArray<SyntaxToken>.Builder body = ImmutableArray.CreateBuilder<SyntaxToken>();

        if (Current.Kind == SyntaxKind.OpenBraceToken)
        {
            openBrace = Advance();
            int depth = 0;

            while (!AtEnd)
            {
                if (Current.Kind == SyntaxKind.CloseBraceToken && depth == 0)
                {
                    break;
                }

                if (Current.Kind == SyntaxKind.OpenBraceToken)
                {
                    depth++;
                }
                else if (Current.Kind == SyntaxKind.CloseBraceToken)
                {
                    depth--;
                }

                body.Add(Advance());
            }

            closeBrace = Expect(SyntaxKind.CloseBraceToken, "'}'");
        }

        return new CommandSyntax(name, arguments.ToImmutable(), openBrace, body.ToImmutable(), closeBrace);
    }

    /// <summary>
    /// <c>[_PropertyName]</c> 形式の引数を解析する。
    /// </summary>
    /// <param name="lastToken">読み取った最後のトークン。行末判定のために呼び出し元へ返す。</param>
    /// <returns>解析した引数。</returns>
    private PropertyReferenceArgumentSyntax ParsePropertyReferenceArgument(out SyntaxToken lastToken)
    {
        SyntaxToken openBracket = Advance();
        SyntaxToken name = Expect(SyntaxKind.IdentifierToken, "参照するプロパティ名");
        SyntaxToken closeBracket = Expect(SyntaxKind.CloseBracketToken, "']'");
        lastToken = closeBracket;
        return new PropertyReferenceArgumentSyntax(openBracket, name, closeBracket);
    }

    /// <summary>命令の引数になりうるトークン種別かを判定する。</summary>
    /// <param name="kind">判定するトークン種別。</param>
    /// <returns>引数になりうる場合は <see langword="true"/>。</returns>
    private static bool IsArgumentStart(SyntaxKind kind) => kind is
        SyntaxKind.IdentifierToken or
        SyntaxKind.NumericLiteralToken or
        SyntaxKind.StringLiteralToken or
        SyntaxKind.OpenBracketToken or
        SyntaxKind.CommaToken or
        SyntaxKind.EqualsToken;

    /// <summary>トークンの後ろで行が変わるかを判定する。</summary>
    /// <param name="token">判定するトークン。</param>
    /// <returns>後続 trivia に改行が含まれる場合は <see langword="true"/>。</returns>
    private static bool EndsLine(SyntaxToken token)
        => token.TrailingTrivia.Any(t => t.Kind == SyntaxKind.EndOfLineTrivia);

    /// <summary>トークンが指定したキーワードかを判定する。</summary>
    /// <param name="token">判定するトークン。</param>
    /// <param name="keyword">比較するキーワード。</param>
    /// <returns>一致する場合は <see langword="true"/>。</returns>
    /// <remarks>ShaderLab のキーワードは大文字小文字を区別しない。</remarks>
    private static bool IsKeyword(SyntaxToken token, string keyword)
        => token.Kind == SyntaxKind.IdentifierToken && token.TextIs(keyword);

    /// <summary>ブロック構文を持つキーワードかを判定する。</summary>
    /// <param name="token">判定するトークン。</param>
    /// <returns>ブロックキーワードの場合は <see langword="true"/>。</returns>
    public static bool IsBlockKeyword(SyntaxToken token)
        => BlockKeywords.Any(keyword => IsKeyword(token, keyword));

    /// <summary>現在のトークンを返して 1 つ進む。</summary>
    /// <returns>進む前のトークン。</returns>
    private SyntaxToken Advance()
    {
        SyntaxToken current = Current;
        if (_index < _tokens.Length - 1)
        {
            _index++;
        }

        return current;
    }

    /// <summary>指定した相対位置のトークンを取得する。</summary>
    /// <param name="offset">現在位置からの相対オフセット。</param>
    /// <returns>その位置のトークン。範囲外の場合は終端トークン。</returns>
    private SyntaxToken Peek(int offset)
        => _tokens[Math.Clamp(_index + offset, 0, _tokens.Length - 1)];

    /// <summary>
    /// 期待する種別のトークンを消費する。無い場合は欠落トークンを合成する。
    /// </summary>
    /// <param name="kind">期待するトークン種別。</param>
    /// <param name="expectation">エラーメッセージに出す「何が必要だったか」。</param>
    /// <returns>消費したトークン、または合成した欠落トークン。</returns>
    /// <remarks>
    /// <b>期待外れの場合にトークンを消費しない</b>のが要点である。
    /// 消費してしまうと、本来そのトークンで始まるはずだった後続の構文まで巻き添えで壊れる。
    /// 欠落トークンを挿むだけにすることで、次の解析はその場から再開できる。
    /// </remarks>
    private SyntaxToken Expect(SyntaxKind kind, string expectation)
    {
        if (Current.Kind == kind)
        {
            return Advance();
        }

        string found = Current.Kind == SyntaxKind.EndOfFileToken ? "ファイルの終わり" : $"'{Current.Text}'";
        ReportError(Current.Span, $"{expectation} が必要ですが {found} がありました。");

        return new SyntaxToken(kind, new TextSpan(Current.Span.Start, 0), string.Empty, isMissing: true);
    }

    /// <summary>構文エラーを記録する。</summary>
    /// <param name="span">問題のある範囲。</param>
    /// <param name="message">エラーの内容。</param>
    private void ReportError(TextSpan span, string message)
        => _diagnostics.Add(Diagnostic.Create(
            ShaderLabDescriptors.SyntaxError, Location.Create(_text, span), message));
}
