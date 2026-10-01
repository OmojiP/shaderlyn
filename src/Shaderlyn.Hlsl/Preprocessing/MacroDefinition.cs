using System.Collections.Immutable;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// <c>#define</c> で定義されたマクロ。
/// </summary>
/// <remarks>
/// <para>
/// Unity のシェーダーライブラリは関数形式マクロに強く依存している。
/// <c>TEXTURE2D(_MainTex)</c> や <c>UNITY_SETUP_INSTANCE_ID(input)</c> は
/// 展開しなければ構文として成立せず、マクロ展開なしでは
/// 実用的な URP シェーダーを 1 つも解析できない。
/// </para>
/// </remarks>
public sealed class MacroDefinition
{
    /// <summary>可変長引数マクロの引数を受け取る特別な名前。</summary>
    public const string VariadicArgumentName = "__VA_ARGS__";

    /// <summary>
    /// マクロ定義を生成する。
    /// </summary>
    /// <param name="name">マクロ名。</param>
    /// <param name="nameToken">マクロ名のトークン。診断の位置に使う。</param>
    /// <param name="isFunctionLike">関数形式マクロかどうか。</param>
    /// <param name="parameters">仮引数名。関数形式でない場合は空。</param>
    /// <param name="isVariadic">可変長引数を取るかどうか。</param>
    /// <param name="body">置換される本体のトークン列。</param>
    public MacroDefinition(
        string name,
        HlslSyntaxToken nameToken,
        bool isFunctionLike,
        ImmutableArray<string> parameters,
        bool isVariadic,
        ImmutableArray<HlslSyntaxToken> body)
    {
        Name = name;
        NameToken = nameToken;
        IsFunctionLike = isFunctionLike;
        Parameters = parameters.IsDefault ? [] : parameters;
        IsVariadic = isVariadic;
        Body = body.IsDefault ? [] : body;
    }

    /// <summary>マクロ名。</summary>
    public string Name { get; }

    /// <summary>マクロ名のトークン。</summary>
    public HlslSyntaxToken NameToken { get; }

    /// <summary>
    /// 関数形式マクロかどうか。
    /// </summary>
    /// <remarks>
    /// <c>#define A(x) ...</c> は関数形式、<c>#define A (x)</c> はオブジェクト形式である。
    /// <b>マクロ名の直後に空白を挟まず開き括弧が来るかどうか</b>で決まり、
    /// この区別を誤ると本体がまったく別のものになる。
    /// </remarks>
    public bool IsFunctionLike { get; }

    /// <summary>仮引数名。</summary>
    public ImmutableArray<string> Parameters { get; }

    /// <summary>可変長引数を取るかどうか。</summary>
    public bool IsVariadic { get; }

    /// <summary>置換される本体のトークン列。</summary>
    public ImmutableArray<HlslSyntaxToken> Body { get; }

    /// <summary>
    /// 指定した実引数の個数を受け付けられるかを判定する。
    /// </summary>
    /// <param name="argumentCount">実引数の個数。</param>
    /// <returns>受け付けられる場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// 可変長引数マクロは、名前付き引数の個数以上であれば受け付ける。
    /// 引数を 1 つも取らないマクロに空の実引数 1 つを渡す形
    /// (<c>FOO()</c>) も許容する必要がある。C の規則で空の実引数は妥当なためである。
    /// </remarks>
    public bool AcceptsArgumentCount(int argumentCount)
    {
        if (IsVariadic)
        {
            return argumentCount >= Parameters.Length;
        }

        if (Parameters.Length == 0)
        {
            return argumentCount <= 1;
        }

        return argumentCount == Parameters.Length;
    }
}
