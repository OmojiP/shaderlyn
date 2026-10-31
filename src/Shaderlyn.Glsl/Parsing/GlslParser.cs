using System.Collections.Immutable;
using Shaderlyn.CFamily.Parsing;
using Shaderlyn.CFamily.Preprocessing;
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
    /// <summary>語がキーワードかを決める規則。言語のバージョンが分からなければ無い (表の語はすべてキーワード)。</summary>
    private readonly GlslWordRules? _rules;

    /// <summary>語と、今の位置より前の <c>#extension</c> の数ごとの、キーワードかの答え。</summary>
    private readonly Dictionary<(string Word, int Directives), bool> _keywords = [];

    /// <summary>
    /// <c>#extension</c> の指令。出力の中の位置 (この位置から後ろのトークンに効く)、拡張の名前 (<c>all</c> を含む)、有効にするか。出力の中の位置の順。
    /// 無ければ、ファイルのどこかで有効にした拡張 (<see cref="GlslLanguageOptions.Extensions"/>) を見る。
    /// </summary>
    private readonly ImmutableArray<(int OutputIndex, string Name, bool On)> _extensionDirectives = [];

    /// <summary>ファイルのどこかで有効にした拡張。<see cref="_extensionDirectives"/> が無いときに見る。</summary>
    private readonly GlslExtensionSet _extensions = GlslExtensionSet.Empty;

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
    /// <param name="language">言語の設定。バージョンと拡張で、語がキーワードかが変わる (<see cref="GlslWordRules"/>)。</param>
    /// <param name="directives">
    /// プリプロセッサが覚えた <c>#version</c> と <c>#extension</c>。<c>#extension</c> は書いた位置より後ろのトークンにだけ効く (glslang)。
    /// 渡さなければ、ファイルのどこかで有効にした拡張をどの位置でも有効とする。
    /// </param>
    public GlslParser(
        ImmutableArray<ShaderSyntaxToken> tokens,
        SourceText? ownSource,
        Func<string, bool>? isUserInclude = null,
        GlslLanguageOptions? language = null,
        ImmutableArray<RecordedDirective> directives = default)
        : base(tokens, ownSource, isUserInclude)
    {
        _extensions = language?.Extensions ?? GlslExtensionSet.Empty;
        _extensionDirectives =
        [
            .. (directives.IsDefault ? [] : directives)
                .Where(d => d.Directive.Text == "extension" && d.Tokens is [_, { Kind: ShaderSyntaxKind.ColonToken }, _, ..])
                .Select(d => (d.OutputIndex, d.Tokens[0].Text, d.Tokens[2].Text != "disable")),
        ];
        _rules = language is { Version: { } version }
            ? new GlslWordRules(version, language.Profile == GlslProfile.Es, language.Stage, language.Vulkan, IsExtensionOnHere)
            : null;
    }

    /// <summary>今の位置で拡張が有効か。今の位置より前の、最後のその拡張 (か <c>all</c>) の <c>#extension</c> で決まる。</summary>
    private bool IsExtensionOnHere(string name)
    {
        if (_extensionDirectives.IsEmpty)
        {
            return _extensions.Contains(name);
        }

        bool on = false;

        foreach ((int outputIndex, string extension, bool enabled) in _extensionDirectives)
        {
            if (outputIndex > Position)
            {
                break;
            }

            if (extension == name || extension == "all")
            {
                on = enabled;
            }
        }

        return on;
    }

    /// <summary>今の位置より前にある <c>#extension</c> の数。</summary>
    private int DirectivesBefore()
    {
        int count = 0;

        while (count < _extensionDirectives.Length && _extensionDirectives[count].OutputIndex <= Position)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// 語が、言語のバージョンと有効にした拡張でキーワードかを判定する。
    /// 4.00 より前の <c>sample</c> や、拡張を有効にしていない <c>rayPayloadEXT</c> は名前である。
    /// </summary>
    /// <param name="word">語。</param>
    /// <returns>キーワードなら <see langword="true"/>。予約語は名前として読む (誤りは別に報告する)。</returns>
    private bool IsKeyword(string word)
    {
        if (_rules is null)
        {
            return true;
        }

        int directives = DirectivesBefore();

        if (!_keywords.TryGetValue((word, directives), out bool keyword))
        {
            keyword = _keywords[(word, directives)] = _rules.Classify(word).Kind == GlslWordKind.Keyword;
        }

        return keyword;
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

    /// <inheritdoc/>
    /// <remarks>
    /// レイトレーシングの <c>ignoreIntersectionEXT;</c> / <c>terminateRayEXT;</c> (GL_EXT_ray_tracing)、
    /// <c>demote;</c> (GL_EXT_demote_to_helper_invocation)、<c>terminateInvocation;</c> (GL_EXT_terminate_invocation)。
    /// </remarks>
    protected override bool IsLanguageJumpKeyword(string text)
        => text is "ignoreIntersectionEXT" or "terminateRayEXT" or "demote" or "terminateInvocation" && IsKeyword(text);

    /// <inheritdoc/>
    /// <remarks>
    /// glslang 16.6.0 の字句解析と同じ。整数は <c>u</c>・<c>l</c>・<c>s</c> をこの順に組み合わせたもの (<c>u</c>、<c>ul</c>、<c>us</c>、<c>uls</c>、<c>l</c>、<c>s</c>)。
    /// 浮動小数点数は <c>f</c>、<c>lf</c> (<c>double</c>)、<c>hf</c> (<c>float16_t</c>) と、8 ビットの浮動小数点数の <c>fe2m1</c> などである。
    /// 小数点も指数も無い <c>1f</c> は誤り (glslang の「float literal needs a decimal point or exponent」)。
    /// </remarks>
    protected override bool IsNumericSuffixAccepted(string suffix, bool floating)
    {
        string lower = suffix.ToLowerInvariant();

        return floating
            ? lower is "f" or "lf" or "hf" || suffix is "fe2m1" or "fe3m2" or "fe2m3" or "fue8m0" or "fmxint8"
            : lower is "u" or "ul" or "us" or "uls" or "l" or "s";
    }

    /// <summary>そのトークン列が、文・宣言として過不足なく閉じているかを判定する。</summary>
    /// <param name="tokens">判定するトークン列。</param>
    /// <returns>閉じていれば <see langword="true"/>。</returns>
    public static bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens)
        => IsCompleteUnits(tokens, static t => new GlslParser(t));

    /// <inheritdoc/>
    protected override bool IsBuiltInTypeName(string name) => GlslKeywords.IsBuiltInType(name) && IsKeyword(name);

    /// <inheritdoc/>
    /// <remarks>型の引数を取る型 (<c>coopmat&lt;…&gt;(x)</c>、<c>vector&lt;float, 5&gt;(x)</c>) の値の作成を、比較の連なりと取り違えないために使う。</remarks>
    protected override bool IsBuiltInTemplateTypeName(string name) => GlslKeywords.ParameterizedTypes.Contains(name) && IsKeyword(name);

    /// <inheritdoc/>
    protected override bool ReadsTypeNameCallAsExpression => true;

    /// <inheritdoc/>
    protected override bool TakesParenthesizedTypeArguments(string name) => name == "spirv_type" && IsKeyword(name);

    /// <inheritdoc/>
    /// <remarks>
    /// <c>nonuniformEXT</c> は修飾子 (<c>nonuniformEXT in vec4 v;</c>) にも、値に付ける関数のような書き方 (<c>nonuniformEXT(i)</c>) にもなる。
    /// 開き括弧が続けば式として読む。
    /// </remarks>
    protected override bool IsDeclarationModifier(string text)
        => GlslKeywords.Qualifiers.Contains(text) && IsKeyword(text) && !(text == "nonuniformEXT" && Peek(1).Kind == ShaderSyntaxKind.OpenParenToken);

    /// <inheritdoc/>
    protected override bool IsTypePrefixModifier(string text) => false;

    /// <inheritdoc/>
    protected override bool IsParameterModifier(string text) => false;

    /// <inheritdoc/>
    /// <remarks>
    /// <c>layout(...)</c> と、GL_EXT_spirv_intrinsics の <c>spirv_decorate(...)</c> などを括弧ごと修飾子として読む。中の値は構文木にしない。
    /// </remarks>
    protected override bool TryParseModifierGroup(ImmutableArray<ShaderSyntaxToken>.Builder modifiers)
    {
        if (!IsQualifierGroup(Current.Text) || Peek(1).Kind != ShaderSyntaxKind.OpenParenToken)
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
        if (CurrentTextIs("precision") && IsKeyword("precision"))
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
            && !IsBuiltInTypeName(next.Text)
            && Peek(afterModifiers + 1).Kind is ShaderSyntaxKind.SemicolonToken or ShaderSyntaxKind.CommaToken)
        {
            return SkipToSemicolon();
        }

        // インターフェースブロック: uniform UBO { ... } ubo;
        // ディスクリプターヒープの中には、名前の無いブロックも置ける (GL_EXT_structured_descriptor_heap の layout(buffer_reference) buffer { ... } inlineRef;)。
        if (hasBlockQualifier
            && ((next.Kind == ShaderSyntaxKind.IdentifierToken && Peek(afterModifiers + 1).Kind == ShaderSyntaxKind.OpenBraceToken)
                || next.Kind == ShaderSyntaxKind.OpenBraceToken))
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

            if (IsQualifierGroup(token.Text) && Peek(offset + 1).Kind == ShaderSyntaxKind.OpenParenToken)
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

            if (!GlslKeywords.Qualifiers.Contains(token.Text)
                || !IsKeyword(token.Text)
                || (token.Text == "nonuniformEXT" && Peek(offset + 1).Kind == ShaderSyntaxKind.OpenParenToken))
            {
                return offset;
            }

            hasBlockQualifier |= GlslKeywords.BlockQualifiers.Contains(token.Text);
            modifierCount++;
            offset++;
        }
    }

    /// <summary>括弧に値を取る修飾子か (<see cref="GlslKeywords.QualifierGroups"/> と、関数と宣言に付く GL_EXT_spirv_intrinsics の指定)。</summary>
    /// <param name="text">トークンの文字列。</param>
    /// <returns>そうなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <c>spirv_instruction(...)</c> は関数の宣言の前に、<c>spirv_execution_mode(...)</c> は修飾子だけの宣言として書く。
    /// どちらも宣言の修飾子として読めば、それぞれ関数の宣言と、読み飛ばす宣言になる。
    /// </remarks>
    private bool IsQualifierGroup(string text)
        => (GlslKeywords.QualifierGroups.Contains(text) || text is "spirv_instruction" or "spirv_execution_mode" or "spirv_execution_mode_id")
           && IsKeyword(text);

    /// <summary>インターフェースブロックを読む。</summary>
    /// <returns>読んだ宣言。</returns>
    /// <remarks>
    /// <para>
    /// <b>インスタンスの名前があるブロック (<c>uniform UBO { mat4 m; } ubo;</c>) は、構造体の宣言として読む。</b>
    /// 構造体とそれに続けて宣言した変数 (<c>struct UBO { mat4 m; } ubo;</c>) と同じく、<c>ubo.m</c> で使うからである。
    /// 最後の修飾子をキーワードに、それより前の修飾子を <see cref="StructDeclarationSyntax.Modifiers"/> に置く。
    /// </para>
    /// <para>
    /// インスタンスの名前が無いブロックは、メンバーがそのまま外側の名前になる (HLSL の <c>cbuffer</c> と同じ)。
    /// <see cref="InterfaceBlockDeclarationSyntax"/> として読む。
    /// </para>
    /// </remarks>
    private DeclarationSyntax ParseInterfaceBlock()
    {
        ImmutableArray<ShaderSyntaxToken> modifiers = ParseModifiers();
        ShaderSyntaxToken? name = Current.Kind == ShaderSyntaxKind.OpenBraceToken ? null : Advance();
        ShaderSyntaxToken openBrace = Expect(ShaderSyntaxKind.OpenBraceToken, "'{'");
        ImmutableArray<DeclarationSyntax> members = ParseMemberDeclarations();
        ShaderSyntaxToken closeBrace = Expect(ShaderSyntaxKind.CloseBraceToken, "'}'");

        // layout(buffer_reference) のブロックは型を宣言する (GL_EXT_buffer_reference)。Vertices v = Vertices(address); と使う。
        // layout(buffer_type) のブロックも、ディスクリプターヒープの中に置く型を宣言する (GL_EXT_structured_descriptor_heap)。
        bool declaresType = modifiers.Any(m => m.Text is "buffer_reference" or "buffer_type");

        if (Current.Kind == ShaderSyntaxKind.IdentifierToken)
        {
            VariableDeclaratorSyntax instance = ParseDeclaratorRest(Advance());
            ShaderSyntaxToken end = Expect(ShaderSyntaxKind.SemicolonToken, "';'");

            // ブロックの名前は型の名前ではなく、入力と出力で同じ名前を使える。修飾子と合わせた名前 (in block) を型の名前にする。
            // 名前の無いブロックは、変数の名前と合わせる。型を宣言するブロックの名前は、そのまま型の名前である。
            return new StructDeclarationSyntax(
                modifiers[^1], name, openBrace, members, closeBrace, [ShaderNodeOrTokenEntry.FromNode(instance)], end)
            {
                Modifiers = modifiers[..^1],
                NameOverride = name is null ? $"{modifiers[^1].Text} {instance.Name}"
                    : declaresType ? null
                    : $"{modifiers[^1].Text} {name.Text}",
            };
        }

        name ??= Expect(ShaderSyntaxKind.IdentifierToken, "ブロックの変数の名前");
        ShaderSyntaxToken semicolon = Expect(ShaderSyntaxKind.SemicolonToken, "';'");

        if (declaresType)
        {
            return new StructDeclarationSyntax(modifiers[^1], name, openBrace, members, closeBrace, [], semicolon)
            {
                Modifiers = modifiers[..^1],
            };
        }

        return new InterfaceBlockDeclarationSyntax(modifiers, name, openBrace, members, closeBrace, semicolon);
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
