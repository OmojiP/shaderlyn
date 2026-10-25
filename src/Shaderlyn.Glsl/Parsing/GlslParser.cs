using System.Collections.Immutable;
using Shaderlyn.CFamily.Parsing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Glsl.Syntax;

namespace Shaderlyn.Glsl.Parsing;

/// <summary>
/// プリプロセス済みの GLSL トークン列を構文木へ組み立てるパーサ。
/// </summary>
/// <remarks>
/// C 系の言語に共通の構文解析器 (<see cref="ShaderParser"/>) を使い、ここでは GLSL の違いだけを答える。
/// 組み込みの型と修飾子、<c>layout(...)</c>、インターフェースブロック、<c>precision</c> 文、修飾子だけの宣言である。
/// HLSL にだけある構文 (template・enum・演算子の多重定義・ビットフィールド) は読まない。
/// </remarks>
internal sealed class GlslParser : ShaderParser
{
    /// <summary>パーサを生成する。</summary>
    /// <param name="tokens">プリプロセス済みのトークン列。</param>
    public GlslParser(ImmutableArray<ShaderSyntaxToken> tokens)
        : base(tokens)
    {
    }

    /// <summary>解析ツールを生成する。利用者のファイルでないヘッダの関数の中身は読み飛ばす。</summary>
    /// <param name="tokens">解析するトークン列。</param>
    /// <param name="ownSource">解析しているファイル自身のテキスト。</param>
    /// <param name="isUserInclude">取り込んだファイルが利用者のものかをパスから判定する。</param>
    public GlslParser(ImmutableArray<ShaderSyntaxToken> tokens, SourceText? ownSource, Func<string, bool>? isUserInclude = null)
        : base(tokens, ownSource, isUserInclude)
    {
    }

    /// <inheritdoc/>
    protected override string UnavailableSubject => "GLSL";

    /// <inheritdoc/>
    protected override bool AcceptsEnums => false;

    /// <inheritdoc/>
    protected override bool AcceptsTemplates => false;

    /// <inheritdoc/>
    protected override bool AcceptsOperatorOverloading => false;

    /// <inheritdoc/>
    protected override bool AcceptsBitfields => false;

    /// <inheritdoc/>
    protected override bool AcceptsArrayTypes => true;

    /// <summary>そのトークン列が、文・宣言として過不足なく閉じているかを判定する。</summary>
    /// <param name="tokens">判定するトークン列。</param>
    /// <returns>閉じていれば <see langword="true"/>。</returns>
    public static bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens)
        => IsCompleteUnits(tokens, static t => new GlslParser(t));

    /// <inheritdoc/>
    protected override bool IsBuiltInTypeName(string name) => GlslKeywords.IsBuiltInType(name);

    /// <inheritdoc/>
    protected override bool IsDeclarationModifier(string text) => GlslKeywords.Qualifiers.Contains(text);

    /// <inheritdoc/>
    protected override bool IsTypePrefixModifier(string text) => false;

    /// <inheritdoc/>
    protected override bool IsParameterModifier(string text) => false;

    /// <inheritdoc/>
    /// <remarks><c>layout(...)</c> を括弧ごと修飾子として読む。中の値は構文木にしない。</remarks>
    protected override bool TryParseModifierGroup(ImmutableArray<ShaderSyntaxToken>.Builder modifiers)
    {
        if (!CurrentTextIs("layout") || Peek(1).Kind != ShaderSyntaxKind.OpenParenToken)
        {
            return false;
        }

        modifiers.Add(Advance());
        int depth = 0;

        while (!AtEnd)
        {
            ShaderSyntaxToken token = Advance();
            modifiers.Add(token);

            if (token.Kind == ShaderSyntaxKind.OpenParenToken)
            {
                depth++;
            }
            else if (token.Kind == ShaderSyntaxKind.CloseParenToken && --depth == 0)
            {
                break;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    protected override DeclarationSyntax? TryParseLanguageDeclaration()
    {
        // precision highp float;
        if (CurrentTextIs("precision"))
        {
            return SkipToSemicolon();
        }

        int afterModifiers = SkipModifiersAhead(out bool hasBlockQualifier, out int modifierCount);

        if (modifierCount == 0)
        {
            return null;
        }

        ShaderSyntaxToken next = Peek(afterModifiers);

        // 修飾子だけの宣言: layout(local_size_x = 16) in; / layout(triangles) in;
        if (next.Kind == ShaderSyntaxKind.SemicolonToken)
        {
            return SkipToSemicolon();
        }

        // 既にある名前に修飾子を足す宣言: invariant gl_Position; / precise x, y;
        if (next.Kind == ShaderSyntaxKind.IdentifierToken
            && !GlslKeywords.IsBuiltInType(next.Text)
            && Peek(afterModifiers + 1).Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CommaToken)
        {
            return SkipToSemicolon();
        }

        // インターフェースブロック: uniform UBO { ... } ubo;
        if (hasBlockQualifier
            && next.Kind == ShaderSyntaxKind.IdentifierToken
            && Peek(afterModifiers + 1).Kind == ShaderSyntaxKind.OpenBraceToken)
        {
            return ParseInterfaceBlock();
        }

        return null;
    }

    /// <summary>
    /// 今の位置から、修飾子 (1 語と <c>layout(...)</c>) の並びを読み進めずに数える。
    /// </summary>
    /// <param name="hasBlockQualifier">インターフェースブロックを作る修飾子があったか。</param>
    /// <param name="modifierCount">修飾子の数。</param>
    /// <returns>修飾子の並びの直後のトークンの、今の位置からの距離。</returns>
    private int SkipModifiersAhead(out bool hasBlockQualifier, out int modifierCount)
    {
        hasBlockQualifier = false;
        modifierCount = 0;
        int offset = 0;

        while (true)
        {
            ShaderSyntaxToken token = Peek(offset);

            if (token.Kind != ShaderSyntaxKind.IdentifierToken)
            {
                return offset;
            }

            if (token.Text == "layout" && Peek(offset + 1).Kind == ShaderSyntaxKind.OpenParenToken)
            {
                int depth = 0;
                offset++;

                while (Peek(offset).Kind != ShaderSyntaxKind.EndOfFileToken)
                {
                    ShaderSyntaxKind kind = Peek(offset).Kind;
                    offset++;

                    if (kind == ShaderSyntaxKind.OpenParenToken)
                    {
                        depth++;
                    }
                    else if (kind == ShaderSyntaxKind.CloseParenToken && --depth == 0)
                    {
                        break;
                    }
                }

                modifierCount++;
                continue;
            }

            if (!GlslKeywords.Qualifiers.Contains(token.Text))
            {
                return offset;
            }

            hasBlockQualifier |= GlslKeywords.BlockQualifiers.Contains(token.Text);
            modifierCount++;
            offset++;
        }
    }

    /// <summary>インターフェースブロックを読む。</summary>
    /// <returns>読んだ宣言。</returns>
    private InterfaceBlockDeclarationSyntax ParseInterfaceBlock()
    {
        ImmutableArray<ShaderSyntaxToken> modifiers = ParseModifiers();
        ShaderSyntaxToken name = Advance();
        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<DeclarationSyntax> members = ParseMemberDeclarations();
        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");

        ShaderSyntaxToken? instance = Current.Kind == ShaderSyntaxKind.IdentifierToken ? Advance() : null;
        ImmutableArray<ShaderSyntaxToken> ranks = instance is null ? [] : ParseArrayRanks();
        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");

        return new InterfaceBlockDeclarationSyntax(modifiers, name, openBrace, members, closeBrace, instance, ranks, semicolon);
    }

    /// <summary>セミコロンまでを、構文木にしない宣言として読む。</summary>
    /// <returns>読んだトークンを持つ宣言。</returns>
    private IncompleteDeclarationSyntax SkipToSemicolon()
    {
        ImmutableArray<ShaderSyntaxToken>.Builder tokens = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();

        while (!AtEnd && Current.Kind != ShaderSyntaxKind.SemicolonToken)
        {
            tokens.Add(Advance());
        }

        tokens.Add(Expect(ShaderSyntaxKind.SemicolonToken, "';'"));
        return new IncompleteDeclarationSyntax(tokens.ToImmutable());
    }
}
