using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.CFamily.Preprocessing;

/// <summary>
/// 展開したコードの言語の設定 (<see cref="PreprocessorOptions.Language"/>)。
/// </summary>
/// <remarks>
/// プリプロセッサは C 系の言語に共通で、言語ごとの違いはここから読む。
/// 言語ごとの設定 (HLSL のコンパイラとバージョンなど) は、派生した型が持つ。
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

    /// <summary>その言語に固有のプリプロセッサの指令か (Slang の <c>#language</c>)。展開には影響しないものに限る。</summary>
    /// <param name="name">指令の名前 (<c>#</c> を除く)。</param>
    /// <returns>固有の指令なら <see langword="true"/>。既定は <see langword="false"/>。</returns>
    public virtual bool IsLanguageDirective(string name) => false;

    /// <summary>条件式の <c>__has_feature(名前)</c> の値 (Slang)。その言語に無い書き方なら <see langword="null"/>。</summary>
    /// <param name="feature">機能の名前。</param>
    /// <returns>機能があれば <see langword="true"/>、無ければ <see langword="false"/>。既定は <see langword="null"/> (読まない)。</returns>
    public virtual bool? HasFeature(string feature) => null;

    /// <summary>行番号を書かない <c>#line</c> を許すか (Slang。行番号の付け替えを元に戻す)。既定は <see langword="false"/>。</summary>
    public virtual bool AllowsBareLineDirective => false;
}
