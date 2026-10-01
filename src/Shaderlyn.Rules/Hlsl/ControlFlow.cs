using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Rules;

/// <summary>
/// 文を通ったあと、必ず関数を抜けるかを判定する。
/// </summary>
/// <remarks>
/// <para>
/// <b>知らない形は「抜ける」に倒す。</b>
/// この判定は「抜けない経路がある」ことを根拠に指摘を出すために使う。
/// 分からない形を「抜けない」と扱うと、
/// 実際には必ず返している関数を誤って指摘することになる。
/// 逆に倒せば、見落とすだけで済む。
/// </para>
/// <para>
/// <b>規則は実物のコンパイラに合わせてある。</b>
/// Unity 6 (DX11 / FXC) で 1 件ずつ確かめた結果を反映している。
/// 推測で決めた最初のバージョンは、<c>discard</c> とループの両方で実物と逆だった。
/// </para>
/// </remarks>
internal static class ControlFlow
{
    /// <summary>
    /// その文を通ると必ず関数を抜けるかを判定する。
    /// </summary>
    /// <param name="statement">対象の文。</param>
    /// <returns>必ず抜けるなら <see langword="true"/>。</returns>
    public static bool AlwaysExits(HlslStatementSyntax? statement) => AlwaysExits(statement, static _ => true);

    /// <summary>
    /// 1 つの構成に存在する文だけを見て、その文を通ると必ず関数を抜けるかを判定する。
    /// </summary>
    /// <param name="statement">対象の文。</param>
    /// <param name="present">その構成に文が存在するかを答える。</param>
    /// <returns>必ず抜けるなら <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>両方の分岐を並べた木には、同時には存在しない文が並んでいる。</b>
    /// <c>#ifdef _B</c> の中の <c>return</c> を、並べた木のまま数えると、
    /// <c>!_B</c> の構成でも必ず返すように見える。
    /// 存在しない文は、無いもの (空の文) として扱う。
    /// </remarks>
    public static bool AlwaysExits(HlslStatementSyntax? statement, Func<HlslSyntaxNode, bool> present) => statement switch
    {
        null => false,

        _ when !present(statement) => false,

        ReturnStatementSyntax => true,

        // 途中の 1 文でも必ず抜けるなら、その先へは進まない。
        BlockStatementSyntax block => block.Statements.Any(s => AlwaysExits(s, present)),

        // else が無ければ、条件が偽のときに素通りする。
        IfStatementSyntax branch =>
            branch.ElseStatement is { } otherwise
            && AlwaysExits(branch.ThenStatement, present)
            && AlwaysExits(otherwise, present),

        // ループは判断しない。
        // コンパイラは境界が定数のループを展開するため、
        // for (int i = 0; i < 4; i++) { return 1; } は「必ず返る」と扱われる。
        // 展開できるかどうかをここで判定することはできないので、報告しない側に倒す。
        // 代わりに while (true) { } を見落とすが、そう書く人はまずいない。
        ForStatementSyntax or WhileStatementSyntax or DoWhileStatementSyntax => true,

        // 値を評価するだけの文は抜けない。
        // discard もここに入る。そのピクセルの処理は終わるが、
        // コンパイラは値を返す関数に return を求める (実測で確認)。
        ExpressionStatementSyntax
            or LocalDeclarationStatementSyntax
            or EmptyStatementSyntax
            or JumpStatementSyntax
            or SwitchLabelStatementSyntax => false,

        // switch も、構文の誤りから作られた文も、ここでは判断しない。
        _ => true,
    };
}
