using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// エディタのカーソルが何の上にあるかを判定する。
/// </summary>
/// <remarks>
/// <b>ホバー・定義へ移動・参照の検索・名前の変更が、同じ判定を使う。</b>
/// 書き写すと、ある機能では語の直後でも答えるのに別の機能では答えない、という食い違いが起きる。
/// </remarks>
internal static class CursorTarget
{
    /// <summary>
    /// カーソルがその範囲の上にあるかを判定する。
    /// </summary>
    /// <param name="span">語やノードの範囲。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>上にあれば <see langword="true"/>。</returns>
    /// <remarks>
    /// 語を打ち終えた直後 (語の終端) も上にあるとみなす。
    /// 空の範囲 (構文エラーの回復で補われた語) は、どこにカーソルがあっても上にあるとみなさない。
    /// </remarks>
    public static bool Touches(TextSpan span, int offset)
        => !span.IsEmpty && span.IntersectsWith(offset);

    /// <summary>
    /// カーソルの下にある識別子を、展開前のトークン列から探す。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="offset">カーソルのオフセット。</param>
    /// <returns>識別子のトークン。無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// 展開前のトークン列から探す。
    /// 展開後の構文木では、マクロから生まれた名前が呼び出し位置に重なっており、
    /// カーソルの下にある語と一致しない。マクロの名前そのものも、展開後には消えている。
    /// </remarks>
    public static HlslSyntaxToken? FindIdentifier(ShaderCompilation compilation, int offset)
        => compilation.CodeTokens.FirstOrDefault(
            t => t.Kind == HlslSyntaxKind.IdentifierToken && Touches(t.Span, offset));
}
