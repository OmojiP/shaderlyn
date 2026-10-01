using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// <c>#if</c> / <c>#elif</c> の条件式を評価する。
/// </summary>
/// <remarks>
/// <para>
/// C プリプロセッサの条件式は整数式に限られる。浮動小数も文字列も使えず、
/// 値はすべて 64 ビット整数として扱う。
/// </para>
/// <para>
/// <b>マクロ展開後に残った識別子は 0 とみなす。</b>
/// これは C の規則であり、<c>#if UNDEFINED_MACRO</c> が偽になる根拠でもある。
/// 未定義を誤りとして扱うと、条件付きコンパイルを使う実用的なコードが
/// ほとんど解析できなくなる。
/// </para>
/// </remarks>
internal static class ConditionalExpressionEvaluator
{
    /// <summary>
    /// 条件式を評価する。
    /// </summary>
    /// <param name="tokens">評価する式のトークン列。マクロ展開済みであること。</param>
    /// <param name="error">評価に失敗した場合の説明。</param>
    /// <returns>評価結果。失敗した場合は <see langword="false"/> を返す。</returns>
    /// <remarks>
    /// 評価に失敗した場合も例外は投げず、条件を偽として扱えるようにする。
    /// 解釈できない条件式 1 つでファイル全体の解析を諦めるのは避けたい。
    /// </remarks>
    public static bool Evaluate(ImmutableArray<HlslSyntaxToken> tokens, out string? error)
    {
        Parser parser = new(tokens);
        long value = parser.ParseExpression();

        if (parser.Error is not null)
        {
            error = parser.Error;
            return false;
        }

        if (!parser.AtEnd)
        {
            error = $"条件式の末尾に余分な記述があります: '{parser.CurrentText}'";
            return false;
        }

        error = null;
        return value != 0;
    }

    /// <summary>
    /// 条件式を再帰下降で解析しながら評価する。
    /// </summary>
    /// <remarks>
    /// 構文木を作らず、解析しながら値を計算する。
    /// 条件式は評価結果しか必要なく、木を保持する用途が無いためである。
    /// </remarks>
    private sealed class Parser(ImmutableArray<HlslSyntaxToken> tokens)
    {
        private readonly ImmutableArray<HlslSyntaxToken> _tokens = tokens;
        private int _index;

        /// <summary>解析中に発生した最初のエラー。</summary>
        public string? Error { get; private set; }

        /// <summary>すべてのトークンを読み終えたかどうか。</summary>
        public bool AtEnd => _index >= _tokens.Length;

        /// <summary>現在位置のトークンのテキスト。</summary>
        public string CurrentText => AtEnd ? "<式の終わり>" : _tokens[_index].Text;

        private HlslSyntaxKind CurrentKind => AtEnd ? HlslSyntaxKind.EndOfFileToken : _tokens[_index].Kind;

        /// <summary>条件式全体を評価する。</summary>
        /// <returns>評価結果。</returns>
        public long ParseExpression() => ParseConditional();

        /// <summary>三項演算子を評価する。</summary>
        /// <returns>評価結果。</returns>
        private long ParseConditional()
        {
            long condition = ParseBinary(0);

            if (CurrentKind != HlslSyntaxKind.QuestionToken)
            {
                return condition;
            }

            _index++;
            long whenTrue = ParseConditional();

            if (CurrentKind != HlslSyntaxKind.ColonToken)
            {
                Fail("三項演算子に ':' がありません。");
                return 0;
            }

            _index++;
            long whenFalse = ParseConditional();

            return condition != 0 ? whenTrue : whenFalse;
        }

        /// <summary>
        /// 優先順位つきで二項演算子を評価する。
        /// </summary>
        /// <param name="minimumPrecedence">この優先順位以上の演算子だけを処理する。</param>
        /// <returns>評価結果。</returns>
        /// <remarks>
        /// 優先順位登坂法 (precedence climbing) で実装している。
        /// 演算子ごとに再帰関数を用意する方式に比べ、
        /// 優先順位が 11 段階ある C の式を短く正確に書ける。
        /// </remarks>
        private long ParseBinary(int minimumPrecedence)
        {
            long left = ParseUnary();

            while (!AtEnd)
            {
                int precedence = GetPrecedence(CurrentKind);
                if (precedence < 0 || precedence < minimumPrecedence)
                {
                    break;
                }

                HlslSyntaxKind operatorKind = CurrentKind;
                _index++;

                // && と || は短絡評価する。
                // 右辺に 0 除算があっても左辺で結果が決まるなら評価してはならない。
                if (operatorKind == HlslSyntaxKind.AmpersandAmpersandToken)
                {
                    long right = ParseBinary(precedence + 1);
                    left = (left != 0 && right != 0) ? 1 : 0;
                    continue;
                }

                if (operatorKind == HlslSyntaxKind.BarBarToken)
                {
                    long right = ParseBinary(precedence + 1);
                    left = (left != 0 || right != 0) ? 1 : 0;
                    continue;
                }

                left = Apply(operatorKind, left, ParseBinary(precedence + 1));
            }

            return left;
        }

        /// <summary>単項演算子と基本要素を評価する。</summary>
        /// <returns>評価結果。</returns>
        private long ParseUnary()
        {
            if (AtEnd)
            {
                Fail("条件式が空です。");
                return 0;
            }

            switch (CurrentKind)
            {
                case HlslSyntaxKind.ExclamationToken:
                    _index++;
                    return ParseUnary() == 0 ? 1 : 0;

                case HlslSyntaxKind.MinusToken:
                    _index++;
                    return -ParseUnary();

                case HlslSyntaxKind.PlusToken:
                    _index++;
                    return ParseUnary();

                case HlslSyntaxKind.TildeToken:
                    _index++;
                    return ~ParseUnary();

                case HlslSyntaxKind.OpenParenToken:
                {
                    _index++;
                    long value = ParseConditional();

                    if (CurrentKind != HlslSyntaxKind.CloseParenToken)
                    {
                        Fail("条件式の括弧が閉じられていません。");
                        return 0;
                    }

                    _index++;
                    return value;
                }

                case HlslSyntaxKind.NumericLiteralToken:
                    return ParseNumericLiteral();

                case HlslSyntaxKind.IdentifierToken:
                    // マクロ展開後に残った識別子は 0 とみなす (C の規則)。
                    _index++;
                    return 0;

                default:
                    Fail($"条件式に使えない記述があります: '{CurrentText}'");
                    return 0;
            }
        }

        /// <summary>
        /// 数値リテラルを評価する。
        /// </summary>
        /// <returns>評価結果。</returns>
        /// <remarks>
        /// 16 進数と型接尾辞に対応する。
        /// 条件式に浮動小数は書けないため、小数点を含む値はエラーとする。
        /// </remarks>
        private long ParseNumericLiteral()
        {
            string text = _tokens[_index].Text;
            _index++;

            string digits = text.TrimEnd('u', 'U', 'l', 'L');

            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
                {
                    return hex;
                }

                Fail($"16 進数として解釈できません: '{text}'");
                return 0;
            }

            if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                return value;
            }

            Fail($"条件式には整数しか書けません: '{text}'");
            return 0;
        }

        /// <summary>二項演算を適用する。</summary>
        /// <param name="operatorKind">演算子の種別。</param>
        /// <param name="left">左辺の値。</param>
        /// <param name="right">右辺の値。</param>
        /// <returns>演算結果。</returns>
        private long Apply(HlslSyntaxKind operatorKind, long left, long right)
        {
            switch (operatorKind)
            {
                case HlslSyntaxKind.BarToken: return left | right;
                case HlslSyntaxKind.CaretToken: return left ^ right;
                case HlslSyntaxKind.AmpersandToken: return left & right;
                case HlslSyntaxKind.EqualsEqualsToken: return left == right ? 1 : 0;
                case HlslSyntaxKind.ExclamationEqualsToken: return left != right ? 1 : 0;
                case HlslSyntaxKind.LessThanToken: return left < right ? 1 : 0;
                case HlslSyntaxKind.GreaterThanToken: return left > right ? 1 : 0;
                case HlslSyntaxKind.LessThanEqualsToken: return left <= right ? 1 : 0;
                case HlslSyntaxKind.GreaterThanEqualsToken: return left >= right ? 1 : 0;
                case HlslSyntaxKind.LessThanLessThanToken: return left << (int)(right & 63);
                case HlslSyntaxKind.GreaterThanGreaterThanToken: return left >> (int)(right & 63);
                case HlslSyntaxKind.PlusToken: return left + right;
                case HlslSyntaxKind.MinusToken: return left - right;
                case HlslSyntaxKind.AsteriskToken: return left * right;

                // 0 除算で例外を投げてはならない。条件式の書き損じで解析全体が止まる。
                case HlslSyntaxKind.SlashToken:
                    if (right == 0)
                    {
                        Fail("条件式で 0 による除算が発生しました。");
                        return 0;
                    }

                    return left / right;

                case HlslSyntaxKind.PercentToken:
                    if (right == 0)
                    {
                        Fail("条件式で 0 による剰余算が発生しました。");
                        return 0;
                    }

                    return left % right;

                default:
                    Fail($"条件式に使えない演算子です: '{operatorKind}'");
                    return 0;
            }
        }

        /// <summary>演算子の優先順位を返す。値が大きいほど強く結合する。</summary>
        /// <param name="kind">演算子の種別。</param>
        /// <returns>優先順位。演算子でない場合は負の値。</returns>
        private static int GetPrecedence(HlslSyntaxKind kind) => kind switch
        {
            HlslSyntaxKind.BarBarToken => 1,
            HlslSyntaxKind.AmpersandAmpersandToken => 2,
            HlslSyntaxKind.BarToken => 3,
            HlslSyntaxKind.CaretToken => 4,
            HlslSyntaxKind.AmpersandToken => 5,
            HlslSyntaxKind.EqualsEqualsToken or HlslSyntaxKind.ExclamationEqualsToken => 6,
            HlslSyntaxKind.LessThanToken or HlslSyntaxKind.GreaterThanToken
                or HlslSyntaxKind.LessThanEqualsToken or HlslSyntaxKind.GreaterThanEqualsToken => 7,
            HlslSyntaxKind.LessThanLessThanToken or HlslSyntaxKind.GreaterThanGreaterThanToken => 8,
            HlslSyntaxKind.PlusToken or HlslSyntaxKind.MinusToken => 9,
            HlslSyntaxKind.AsteriskToken or HlslSyntaxKind.SlashToken or HlslSyntaxKind.PercentToken => 10,
            _ => -1,
        };

        /// <summary>最初のエラーだけを記録する。</summary>
        /// <param name="message">エラーの内容。</param>
        /// <remarks>
        /// 後続のエラーで上書きしないのは、最初の 1 件が原因で
        /// 以降が連鎖的に壊れているだけの場合が多いためである。
        /// </remarks>
        private void Fail(string message)
        {
            Error ??= message;
            _index = _tokens.Length;
        }
    }
}
