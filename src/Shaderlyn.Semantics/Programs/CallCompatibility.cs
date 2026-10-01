using Shaderlyn.Core.Syntax;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Conditional;

namespace Shaderlyn.Semantics.Programs;

/// <summary>
/// 呼び出しが宣言に合うかを判定する、小さな規則の集まり。
/// </summary>
/// <remarks>
/// <para>
/// <b>1 か所に置く。</b>
/// 呼び出しの誤りを報告するルール (HL0340 / HL0341 / HL0342) と、
/// 呼び出しを宣言へ結びつける <see cref="ShaderCompilation.ResolveCall"/> は、同じ判定を使う。
/// 書き写すと、片方だけを直したときにルールの指摘と解決の結果が食い違う。
/// </para>
/// <para>
/// 公開しているのは、自作ルールも同じ判定で書けるようにするためである。
/// </para>
/// </remarks>
public static class CallCompatibility
{
    /// <summary>
    /// 実引数の個数が、仮引数の並びに収まるかを判定する。
    /// </summary>
    /// <param name="parameters">仮引数。</param>
    /// <param name="count">実引数の個数。</param>
    /// <returns>収まれば <see langword="true"/>。</returns>
    /// <remarks>既定値のある仮引数は省略できる。</remarks>
    public static bool AcceptsArgumentCount(IEnumerable<ParameterSyntax> parameters, int count)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        int total = 0;
        int required = 0;

        foreach (ParameterSyntax parameter in parameters)
        {
            total++;

            if (parameter.DefaultValue is null)
            {
                required++;
            }
        }

        return count >= required && count <= total;
    }

    /// <summary>
    /// 呼び出し側の変数へ値を書き戻す修飾 (<c>out</c> / <c>inout</c>) かどうかを判定する。
    /// </summary>
    /// <param name="modifier">修飾の語。</param>
    /// <returns>書き戻す修飾なら <see langword="true"/>。</returns>
    public static bool IsWritebackModifier(string modifier)
        => modifier is "out" or "inout";

    /// <summary>
    /// 呼び出し側の変数へ値を書き戻す仮引数かどうかを判定する。
    /// </summary>
    /// <param name="parameter">対象の仮引数。</param>
    /// <returns>書き戻すなら <see langword="true"/>。</returns>
    public static bool IsWriteback(ParameterSyntax parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        return parameter.ModifierTokens.Any(t => IsWritebackModifier(t.Text));
    }

    /// <summary>
    /// 式が書き戻せる先かどうかを判定する。
    /// </summary>
    /// <param name="expression">対象の式。</param>
    /// <returns>書き戻せるなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>書き戻せないと言い切れる形だけを false にする。</b>
    /// リテラル、関数の戻り値、演算の結果には書き戻す先が無い。
    /// それ以外は分からないものとして扱う。
    /// </remarks>
    public static bool IsAssignable(HlslExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsAssignable(parenthesized.Expression),
        LiteralExpressionSyntax => false,
        InvocationExpressionSyntax => false,
        BinaryExpressionSyntax => false,
        CastExpressionSyntax => false,
        ConditionalExpressionSyntax => false,
        _ => true,
    };

    /// <summary>
    /// 呼び出しの構成に、その宣言が存在しうるかを判定する。
    /// </summary>
    /// <param name="conditions">出現条件の索引。</param>
    /// <param name="declaration">対象の宣言。</param>
    /// <param name="call">呼び出しの出現条件。</param>
    /// <returns>存在しうるなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>条件が分からない宣言は、あるものとして扱う。</b>
    /// 無いことにすると、追えなかっただけの宣言を候補から外したうえで
    /// 「合う宣言が無い」「1 つに決まった」と言うことになる。
    /// </remarks>
    public static bool IsPresentWith(ConditionMap conditions, HlslSyntaxNode declaration, SymbolCondition call)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(declaration);

        SymbolCondition declared = conditions.GetCondition(declaration);

        return declared.IsUnknown || conditions.IsPossible(call.And(declared));
    }
}
