namespace Shaderlyn.Hlsl.Syntax;

/// <summary>
/// HLSL / Cg のトークンおよび trivia の種別。
/// </summary>
/// <remarks>
/// プリプロセッサ指令を trivia にしていないのが ShaderLab との大きな違いである。
/// <c>#</c> はトークンとして残し、行頭かどうかの情報とあわせてプリプロセッサが解釈する。
/// マクロ展開と条件分岐の評価を行うには、指令をトークン列として扱える必要があるためである。
/// </remarks>
public enum HlslSyntaxKind
{
    /// <summary>どの規則にも当てはまらなかった文字。</summary>
    BadToken,

    /// <summary>ファイル終端。</summary>
    EndOfFileToken,

    /// <summary>識別子およびキーワード。</summary>
    /// <remarks>
    /// 字句解析の段階ではキーワードを識別子と区別しない。
    /// HLSL のキーワード集合は言語バージョンとコンパイラで異なり、
    /// <c>sample</c> のように文脈によって識別子にもなる語があるためである。
    /// キーワードかどうかの判定は構文解析の側で文脈とともに行う。
    /// </remarks>
    IdentifierToken,

    /// <summary>数値リテラル。整数・浮動小数・16 進数・型接尾辞付きを含む。</summary>
    NumericLiteralToken,

    /// <summary>二重引用符で囲まれた文字列。</summary>
    StringLiteralToken,

    /// <summary>単一引用符で囲まれた文字。</summary>
    CharacterLiteralToken,

    // 区切り記号
#pragma warning disable CS1591
    OpenBraceToken,
    CloseBraceToken,
    OpenParenToken,
    CloseParenToken,
    OpenBracketToken,
    CloseBracketToken,
    SemicolonToken,
    CommaToken,
    DotToken,
    ColonToken,
    ColonColonToken,
    QuestionToken,

    // 演算子
    PlusToken,
    MinusToken,
    AsteriskToken,
    SlashToken,
    PercentToken,
    PlusPlusToken,
    MinusMinusToken,
    EqualsToken,
    PlusEqualsToken,
    MinusEqualsToken,
    AsteriskEqualsToken,
    SlashEqualsToken,
    PercentEqualsToken,
    EqualsEqualsToken,
    ExclamationEqualsToken,
    LessThanToken,
    GreaterThanToken,
    LessThanEqualsToken,
    GreaterThanEqualsToken,
    AmpersandAmpersandToken,
    BarBarToken,
    ExclamationToken,
    AmpersandToken,
    BarToken,
    CaretToken,
    TildeToken,
    LessThanLessThanToken,
    GreaterThanGreaterThanToken,
    AmpersandEqualsToken,
    BarEqualsToken,
    CaretEqualsToken,
    LessThanLessThanEqualsToken,
    GreaterThanGreaterThanEqualsToken,
#pragma warning restore CS1591

    /// <summary><c>#</c>。プリプロセッサ指令の開始、およびマクロ内の文字列化演算子。</summary>
    HashToken,

    /// <summary><c>##</c>。マクロ内のトークン連結演算子。</summary>
    HashHashToken,

    /// <summary>空白文字の並び (改行を除く)。</summary>
    WhitespaceTrivia,

    /// <summary>改行。</summary>
    EndOfLineTrivia,

    /// <summary>
    /// 行末のバックスラッシュによる行継続。
    /// </summary>
    /// <remarks>
    /// <b>これを改行として扱ってはならない。</b>
    /// 複数行にわたるマクロ定義はこの継続に依存しており、
    /// 改行とみなすとマクロの本体が 1 行目で切れてしまう。
    /// Unity のシェーダーライブラリは長いマクロを多用するため、
    /// これを誤ると大半のファイルが正しく解析できない。
    /// </remarks>
    LineContinuationTrivia,

    /// <summary><c>//</c> から行末までのコメント。</summary>
    SingleLineCommentTrivia,

    /// <summary><c>/*</c> から <c>*/</c> までのコメント。</summary>
    MultiLineCommentTrivia,
}
