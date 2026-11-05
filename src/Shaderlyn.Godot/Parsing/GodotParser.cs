using System.Collections.Immutable;
using Shaderlyn.CFamily.Parsing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Godot.Parsing;

/// <summary>
/// プリプロセス済みの Godot シェーダー言語のトークン列を構文木へ組み立てるパーサ。
/// </summary>
/// <remarks>
/// <para>
/// C 系の言語に共通の構文解析器 (<see cref="ShaderParser"/>) を使い、ここでは Godot の違いだけを答える。
/// Godot シェーダー言語は GLSL ES 3.0 から派生した言語で、<c>#version</c>・<c>layout</c>・インターフェースブロックは無い。
/// </para>
/// <para>
/// Godot に固有の構文は次のとおり。
/// <list type="bullet">
/// <item><c>shader_type spatial;</c>、<c>render_mode unshaded, cull_disabled;</c>、<c>stencil_mode …;</c>、<c>group_uniforms a.b;</c>。
/// 構文木にしない宣言 (<see cref="IncompleteDeclarationSyntax"/>) として読み、中の語は意味解析と規則が読む</item>
/// <item>uniform のヒント (<c>uniform vec4 c : source_color, hint_range(0, 1) = vec4(1.0);</c>)。
/// セマンティクス (<see cref="SemanticSyntax"/>) の並びとして読む。2 つ目からの区切りはカンマである</item>
/// <item><c>instance uniform</c> / <c>global uniform</c>、<c>varying</c>、補間 (<c>flat</c> / <c>smooth</c>) と精度 (<c>lowp</c> など) の修飾子</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class GodotParser : ShaderParser
{
    /// <summary>パーサを生成する。</summary>
    /// <param name="tokens">プリプロセス済みのトークン列。</param>
    public GodotParser(ImmutableArray<ShaderSyntaxToken> tokens)
        : base(tokens)
    {
    }

    /// <summary>パーサを生成する。利用者のファイルでない取り込みの関数の中身は読み飛ばす。</summary>
    /// <param name="tokens">解析するトークン列。</param>
    /// <param name="ownSource">解析しているファイル自身のテキスト。</param>
    /// <param name="isUserInclude">取り込んだファイルが利用者のものかをパスから判定する。</param>
    public GodotParser(ImmutableArray<ShaderSyntaxToken> tokens, SourceText? ownSource, Func<string, bool>? isUserInclude = null)
        : base(tokens, ownSource, isUserInclude)
    {
    }

    /// <summary>そのトークン列が、文・宣言として過不足なく閉じているかを判定する。</summary>
    /// <param name="tokens">判定するトークン列。</param>
    /// <returns>閉じていれば <see langword="true"/>。</returns>
    public static bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens)
        => IsCompleteUnits(tokens, static t => new GodotParser(t));

    /// <inheritdoc/>
    protected override string UnavailableSubject => "Godot シェーダー言語";

    /// <inheritdoc/>
    protected override bool AcceptsEnums => false;

    /// <inheritdoc/>
    protected override bool AcceptsTemplates => false;

    /// <inheritdoc/>
    protected override bool AcceptsOperatorOverloading => false;

    /// <inheritdoc/>
    protected override bool AcceptsBitfields => false;

    /// <inheritdoc/>
    /// <remarks>Godot は <c>float[3] a;</c> の書き方も読む。</remarks>
    protected override bool AcceptsArrayTypes => true;

    /// <inheritdoc/>
    protected override bool ReadsTypeNameCallAsExpression => true;

    /// <inheritdoc/>
    /// <remarks>Godot の字句解析は、浮動小数点数の <c>f</c> と整数の <c>u</c> だけを接尾辞として読む。</remarks>
    protected override bool IsNumericSuffixAccepted(string suffix, bool floating)
        => floating ? suffix is "f" or "F" : suffix is "u" or "U";

    /// <inheritdoc/>
    protected override bool IsBuiltInTypeName(string name) => GodotKeywords.BuiltInTypes.Contains(name);

    /// <inheritdoc/>
    /// <remarks>仮引数の <c>in</c> / <c>out</c> / <c>inout</c> も、宣言の前の修飾子として読む (仮引数の前の修飾子の並びも同じ読み方をする)。</remarks>
    protected override bool IsDeclarationModifier(string text)
        => GodotKeywords.Qualifiers.Contains(text) || GodotKeywords.ParameterQualifiers.Contains(text);

    /// <inheritdoc/>
    protected override bool IsTypePrefixModifier(string text) => false;

    /// <inheritdoc/>
    protected override bool IsParameterModifier(string text) => false;

    /// <inheritdoc/>
    /// <remarks>uniform のヒントの並び (<c>: source_color, filter_linear</c>) は、カンマの後ろがヒントの名前なら続く。</remarks>
    protected override bool ContinuesSemanticList(string name) => GodotKeywords.Hints.Contains(name);

    /// <inheritdoc/>
    protected override DeclarationSyntax? TryParseLanguageDeclaration()
    {
        // shader_type spatial; / render_mode unshaded, cull_disabled; / stencil_mode read, compare_equal, 1; / group_uniforms a.b;
        if (Current.Kind == ShaderSyntaxKind.IdentifierToken && GodotKeywords.ModeDeclarations.Contains(Current.Text))
        {
            return SkipToSemicolon();
        }

        return null;
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
