using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.CFamily.Preprocessing;

/// <summary>
/// 展開したコードの言語の設定 (<see cref="PreprocessorOptions.Language"/>)。
/// </summary>
/// <remarks>
/// プリプロセッサは C 系の言語に共通で、言語ごとの違いはここから読む。
/// 言語ごとの設定 (HLSL の方言など) は、派生した型が持つ。
/// </remarks>
public abstract record ShaderLanguageOptions
{
    /// <summary>
    /// 使えない構文を報告するとき (HL0003) に、どこで使えないかを伝える名前 (<c>FXC</c> / <c>Unity</c>)。
    /// </summary>
    /// <returns>名前。</returns>
    public abstract string DescribeUnavailable();

    /// <summary>
    /// トークン列が、1 つ以上の宣言または文として過不足なく読めるかを判定する。
    /// </summary>
    /// <param name="tokens">展開済みのトークン列。</param>
    /// <returns>読めれば <see langword="true"/>。</returns>
    /// <remarks>巻き上げで複製した断片を採るかどうかの判定に使う。</remarks>
    public abstract bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens);
}
