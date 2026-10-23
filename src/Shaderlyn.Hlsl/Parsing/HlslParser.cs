using System.Collections.Immutable;
using Shaderlyn.CFamily.Parsing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Parsing;

/// <summary>
/// プリプロセス済みの HLSL トークン列を構文木へ組み立てるパーサ。
/// </summary>
/// <remarks>
/// C 系の言語に共通の構文解析器 (<see cref="ShaderParser"/>) を使い、ここでは HLSL に固有の部分
/// (組み込みの型と修飾子、<c>cbuffer</c> / <c>tbuffer</c> と <c>namespace</c>、
/// <c>vector&lt;T, N&gt;</c> / <c>matrix&lt;T, R, C&gt;</c> の短い名前) と、
/// コンパイラと HLSL のバージョンで使える構文の違いを答える。
/// </remarks>
internal sealed class HlslParser : ShaderParser
{
    /// <summary>
    /// パーサを生成する。
    /// </summary>
    /// <param name="tokens">プリプロセス済みのトークン列。</param>
    public HlslParser(ImmutableArray<ShaderSyntaxToken> tokens)
        : base(tokens)
    {
    }

    /// <summary>
    /// 解析ツールを生成する。利用者のファイルでないヘッダの関数の中身は読み飛ばす。
    /// </summary>
    /// <param name="tokens">解析するトークン列。</param>
    /// <param name="ownSource">解析しているファイル自身のテキスト。</param>
    /// <param name="isUserInclude">
    /// 取り込んだファイルが利用者のものかをパスから判定する。利用者のヘッダの関数は中身まで作る。
    /// <see langword="null"/> なら、解析しているファイルの関数だけを作る。
    /// </param>
    public HlslParser(ImmutableArray<ShaderSyntaxToken> tokens, SourceText? ownSource, Func<string, bool>? isUserInclude = null)
        : base(tokens, ownSource, isUserInclude)
    {
    }

    /// <summary>
    /// 受け付ける構文。このコードをコンパイルするコンパイラと HLSL のバージョンの<b>どれかで</b>使えるもの (<see cref="HlslLanguageFeatures.Any"/>)。
    /// 既定は FXC で使えるもの。
    /// </summary>
    public HlslLanguageFeatures Features { get; init; } = HlslDialect.Fxc.Features;

    /// <summary>
    /// 使えない構文の報告 (HL0003) に入れる、どこで使えないかを伝える語 (<see cref="HlslLanguageOptions.DescribeUnavailable"/>)。
    /// </summary>
    public string DialectSubject { get; init; } = "FXC";

    /// <inheritdoc/>
    protected override string UnavailableSubject => DialectSubject;

    /// <inheritdoc/>
    protected override bool AcceptsEnums => Features.Enums;

    /// <inheritdoc/>
    protected override bool AcceptsTemplates => Features.Templates;

    /// <inheritdoc/>
    protected override bool AcceptsOperatorOverloading => Features.OperatorOverloading;

    /// <inheritdoc/>
    protected override bool AcceptsBitfields => Features.Bitfields;

    /// <summary>
    /// そのトークン列が、文・宣言として過不足なく閉じているかを判定する。
    /// </summary>
    /// <param name="tokens">判定するトークン列。</param>
    /// <param name="features">受け付ける構文 (コンパイラと HLSL のバージョンで決まる)。<see langword="null"/> なら FXC。</param>
    /// <returns>閉じていれば <see langword="true"/>。</returns>
    /// <remarks>
    /// 構文解析と同じ構文で読む。FXC で読むと、HLSL 2021 の構文 (ビットフィールドなど) を含む文を閉じていないと判断し、
    /// 条件の巻き上げで文を複製できなかった。
    /// </remarks>
    public static bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens, HlslLanguageFeatures? features = null)
    {
        HlslLanguageFeatures accepted = features ?? HlslDialect.Fxc.Features;
        return IsCompleteUnits(tokens, t => new HlslParser(t) { Features = accepted });
    }

    /// <summary>
    /// トークン列の先頭から、文・宣言を 1 つだけ読む (コンパイラは FXC)。
    /// </summary>
    /// <param name="tokens">読むトークン列。</param>
    /// <param name="consumed">読んだトークンの数。読めなかった場合は 0。</param>
    /// <returns>誤り無く読めた場合は <see langword="true"/>。</returns>
    public static bool TryParseSingleUnit(ImmutableArray<ShaderSyntaxToken> tokens, out int consumed)
        => TryParseSingleUnit(tokens, static t => new HlslParser(t), out consumed);

    /// <inheritdoc/>
    protected override bool IsBuiltInTypeName(string name) => HlslKeywords.IsKnownTypeName(name);

    /// <inheritdoc/>
    protected override bool IsDeclarationModifier(string text) => HlslKeywords.DeclarationModifiers.Contains(text);

    /// <inheritdoc/>
    protected override bool IsTypePrefixModifier(string text) => HlslKeywords.TypePrefixModifiers.Contains(text);

    /// <inheritdoc/>
    protected override bool IsParameterModifier(string text) => HlslKeywords.MeshParameterModifiers.Contains(text);

    /// <inheritdoc/>
    /// <remarks>
    /// <c>vector&lt;float, 3&gt;</c> と <c>matrix&lt;half, 4, 4&gt;</c> は <c>float3</c> / <c>half4x4</c>、
    /// 引数の無い <c>vector</c> / <c>matrix</c> は <c>float4</c> / <c>float4x4</c> にする。
    /// 引数を読み取れない形は、短い名前にしない。
    /// </remarks>
    protected override string? GetCanonicalTypeName(
        ShaderSyntaxToken name,
        ShaderSyntaxToken? openAngle,
        ImmutableArray<ShaderSyntaxToken> templateArguments)
    {
        bool isVector = name.Text == "vector";

        if (!isVector && name.Text != "matrix")
        {
            return null;
        }

        if (openAngle is null)
        {
            return isVector ? "float4" : "float4x4";
        }

        // vector<T, N> は 3 個、matrix<T, R, C> は 5 個のトークン (カンマを含む) になる。
        int expected = isVector ? 3 : 5;

        // 要素の型はスカラーの基底名に限る (float4 のような名前が作れるもの)。
        if (templateArguments.Length != expected
            || !HlslKeywords.BuiltInTypes.Contains(templateArguments[0].Text + "4"))
        {
            return null;
        }

        for (int i = 1; i < expected; i += 2)
        {
            if (templateArguments[i].Kind != ShaderSyntaxKind.CommaToken
                || templateArguments[i + 1].Text is not { Length: 1 } digit
                || digit[0] is < '1' or > '4')
            {
                return null;
            }
        }

        return isVector
            ? templateArguments[0].Text + templateArguments[2].Text
            : templateArguments[0].Text + templateArguments[2].Text + "x" + templateArguments[4].Text;
    }

    /// <inheritdoc/>
    protected override bool IsBuiltInTemplateTypeName(string name) => name is "vector" or "matrix";

    /// <inheritdoc/>
    protected override DeclarationSyntax? TryParseLanguageDeclaration()
    {
        if (CurrentTextIs("cbuffer") || CurrentTextIs("tbuffer"))
        {
            return ParseConstantBuffer();
        }

        if (CurrentTextIs("namespace"))
        {
            return ParseNamespace();
        }

        return null;
    }

    /// <summary>定数バッファの宣言を解析する。</summary>
    /// <returns>解析した宣言。</returns>
    private ConstantBufferDeclarationSyntax ParseConstantBuffer()
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken? name = Current.Kind == ShaderSyntaxKind.IdentifierToken ? Advance() : null;
        ImmutableArray<SemanticSyntax> semantics = ParseSemantics();

        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<DeclarationSyntax> members = ParseMemberDeclarations();
        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");

        // cbuffer のセミコロンは省略できる。
        ShaderSyntaxToken? semicolon = Current.Kind == ShaderSyntaxKind.SemicolonToken ? Advance() : null;

        return new ConstantBufferDeclarationSyntax(keyword, name, semantics, openBrace, members, closeBrace, semicolon);
    }

    /// <summary><c>namespace</c> の宣言を解析する。</summary>
    /// <returns>解析した宣言。</returns>
    /// <remarks>
    /// 文法: <c>Namespace := 'namespace' Identifier? '{' Declaration* '}'</c>
    /// 構造体や定数バッファと違い、終端のセミコロンは付かない。
    /// </remarks>
    private NamespaceDeclarationSyntax ParseNamespace()
    {
        ShaderSyntaxToken keyword = Advance();
        ShaderSyntaxToken? name = Current.Kind == ShaderSyntaxKind.IdentifierToken ? Advance() : null;
        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<DeclarationSyntax> members = ParseMemberDeclarations();
        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");

        return new NamespaceDeclarationSyntax(keyword, name, openBrace, members, closeBrace);
    }
}
