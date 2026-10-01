using System.Collections.Immutable;

namespace Shaderlyn.Hlsl.Syntax;

/// <summary>
/// 構文の上で決まっている事実を答える。
/// </summary>
/// <remarks>
/// <b>言語の規則をルールの側に書き写させない。</b>
/// 「どれが代入演算子か」「角括弧の並びが何要素を表すか」「その文のキーワードはどれか」は
/// どれも HLSL の規則であって、ルールごとの判断ではない。
/// 書き写されたものは、片方だけが直されて食い違う。
/// </remarks>
public static class HlslSyntaxFacts
{
    /// <summary>代入演算子かどうかを判定する。</summary>
    /// <param name="kind">判定するトークン種別。</param>
    /// <returns>代入演算子の場合は <see langword="true"/>。</returns>
    /// <remarks>単純な代入 (<c>=</c>) と複合代入の両方を含む。</remarks>
    public static bool IsAssignmentOperator(HlslSyntaxKind kind)
        => kind == HlslSyntaxKind.EqualsToken || IsCompoundAssignment(kind);

    /// <summary>複合代入かどうかを判定する。</summary>
    /// <param name="kind">判定するトークン種別。</param>
    /// <returns><c>+=</c> のような複合代入の場合は <see langword="true"/>。</returns>
    public static bool IsCompoundAssignment(HlslSyntaxKind kind)
        => GetCompoundAssignmentOperand(kind) is not null;

    /// <summary>
    /// 複合代入が行う演算を返す。
    /// </summary>
    /// <param name="kind">判定するトークン種別。</param>
    /// <returns>
    /// <c>%=</c> なら <see cref="HlslSyntaxKind.PercentToken"/>。
    /// 複合代入でない場合は <see langword="null"/>。
    /// </returns>
    /// <remarks>
    /// 「<c>%</c> を禁じる」ようなルールが <c>%=</c> を取りこぼさないために要る。
    /// 末尾の <c>=</c> を落とす形で自前に求めると、<c>&lt;=</c> のような比較を巻き込む。
    /// </remarks>
    public static HlslSyntaxKind? GetCompoundAssignmentOperand(HlslSyntaxKind kind) => kind switch
    {
        HlslSyntaxKind.PlusEqualsToken => HlslSyntaxKind.PlusToken,
        HlslSyntaxKind.MinusEqualsToken => HlslSyntaxKind.MinusToken,
        HlslSyntaxKind.AsteriskEqualsToken => HlslSyntaxKind.AsteriskToken,
        HlslSyntaxKind.SlashEqualsToken => HlslSyntaxKind.SlashToken,
        HlslSyntaxKind.PercentEqualsToken => HlslSyntaxKind.PercentToken,
        HlslSyntaxKind.AmpersandEqualsToken => HlslSyntaxKind.AmpersandToken,
        HlslSyntaxKind.BarEqualsToken => HlslSyntaxKind.BarToken,
        HlslSyntaxKind.CaretEqualsToken => HlslSyntaxKind.CaretToken,
        HlslSyntaxKind.LessThanLessThanEqualsToken => HlslSyntaxKind.LessThanLessThanToken,
        HlslSyntaxKind.GreaterThanGreaterThanEqualsToken => HlslSyntaxKind.GreaterThanGreaterThanToken,
        _ => null,
    };

    /// <summary>
    /// その文を始めるキーワードのトークンを返す。
    /// </summary>
    /// <param name="statement">対象の文。</param>
    /// <returns>キーワードのトークン。キーワードで始まらない文では <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>報告はキーワードだけを指すべきである。</b>
    /// 文全体を指すと、中身が数十行ある <c>for</c> では波線もそれだけ広がる。
    /// </remarks>
    public static HlslSyntaxToken? GetKeyword(HlslStatementSyntax statement) => statement switch
    {
        IfStatementSyntax s => s.IfKeyword,
        ForStatementSyntax s => s.ForKeyword,
        WhileStatementSyntax s => s.WhileKeyword,
        DoWhileStatementSyntax s => s.DoKeyword,
        SwitchStatementSyntax s => s.SwitchKeyword,
        ReturnStatementSyntax s => s.Keyword,
        JumpStatementSyntax s => s.Keyword,
        _ => null,
    };

    /// <summary>配列の次元の数を求める。</summary>
    /// <param name="rankTokens">配列の次元を表すトークン列 (<c>[4][8]</c> なら 6 個)。</param>
    /// <returns>次元の数。配列でなければ 0。</returns>
    public static int CountArrayDimensions(ImmutableArray<HlslSyntaxToken> rankTokens)
        => rankTokens.Count(token => token.Kind == HlslSyntaxKind.OpenBracketToken);

    /// <summary>
    /// 角括弧の並びが表す要素の総数を求める。
    /// </summary>
    /// <param name="rankTokens">角括弧のトークン。</param>
    /// <param name="length">要素の総数。</param>
    /// <returns>数として書かれていた場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 1 つの次元は <c>[</c> 数 <c>]</c> の 3 トークンである。多次元は次元の積になる。
    /// </para>
    /// <para>
    /// <b>長さが式やマクロで書かれている場合は <see langword="false"/> を返す。</b>
    /// 分からないものを 0 や 1 として扱うと、それを根拠にした指摘が出る。
    /// </para>
    /// </remarks>
    public static bool TryGetArrayLength(ImmutableArray<HlslSyntaxToken> rankTokens, out int length)
    {
        length = 0;

        if (rankTokens.Length == 0 || rankTokens.Length % 3 != 0)
        {
            return false;
        }

        int total = 1;

        for (int i = 0; i < rankTokens.Length; i += 3)
        {
            if (rankTokens[i].Kind != HlslSyntaxKind.OpenBracketToken
                || !HlslLiteral.TryGetInt64(rankTokens[i + 1], out long dimension)
                || dimension <= 0
                || dimension > int.MaxValue)
            {
                return false;
            }

            total *= (int)dimension;
        }

        length = total;
        return true;
    }
}
