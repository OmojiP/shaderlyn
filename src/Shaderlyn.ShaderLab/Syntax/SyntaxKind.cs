namespace Shaderlyn.ShaderLab.Syntax;

/// <summary>
/// ShaderLab のトークンおよび trivia の種別。
/// </summary>
/// <remarks>
/// 構文ノードの種別はここには含めない。ノードの識別は C# の型そのもので行い、
/// 解析ドライバも型でディスパッチする。種別列挙と型の二重管理を避けるためである。
/// </remarks>
public enum SyntaxKind
{
    /// <summary>どの規則にも当てはまらなかった文字。</summary>
    BadToken,

    /// <summary>ファイル終端。</summary>
    EndOfFileToken,

    /// <summary>識別子、およびキーワード。</summary>
    /// <remarks>
    /// ShaderLab のキーワードは字句解析の段階では識別子と区別しない。
    /// <c>Cull</c> のような命令名は、文脈によっては単なる名前として現れうるためである。
    /// キーワードかどうかの判定は構文解析の側で文脈とともに行う。
    /// </remarks>
    IdentifierToken,

    /// <summary>二重引用符で囲まれた文字列。</summary>
    StringLiteralToken,

    /// <summary>数値。整数・小数・符号付きのいずれも含む。</summary>
    NumericLiteralToken,

    /// <summary><c>{</c></summary>
    OpenBraceToken,

    /// <summary><c>}</c></summary>
    CloseBraceToken,

    /// <summary><c>(</c></summary>
    OpenParenToken,

    /// <summary><c>)</c></summary>
    CloseParenToken,

    /// <summary><c>[</c></summary>
    OpenBracketToken,

    /// <summary><c>]</c></summary>
    CloseBracketToken,

    /// <summary><c>,</c></summary>
    CommaToken,

    /// <summary><c>=</c></summary>
    EqualsToken,

    /// <summary><c>.</c></summary>
    /// <remarks>
    /// 属性の引数に現れる修飾名で使う (例: <c>[Enum(UnityEngine.Rendering.CullMode)]</c>)。
    /// 小数点はこのトークンにはならず、数値トークンの一部として扱われる。
    /// </remarks>
    DotToken,

    /// <summary>
    /// <c>CGPROGRAM</c> や <c>HLSLPROGRAM</c> などで始まり、対応する終端で閉じられるコードブロック全体。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 開始キーワードから終了キーワードまでを丸ごと 1 つのトークンとして扱う。
    /// 中身は ShaderLab の文法ではなく HLSL / Cg / GLSL であり、
    /// ShaderLab の字句規則をそのまま適用すると壊れるためである。
    /// </para>
    /// <para>
    /// 中身の解析は M2 で HLSL のフロントエンドが担当する。
    /// このトークンは中身をそのまま保持しており、開始位置も分かるので、
    /// 後段が独立して字句解析をやり直せる。
    /// </para>
    /// </remarks>
    ProgramBlockToken,

    /// <summary>空白文字の並び (改行を除く)。</summary>
    WhitespaceTrivia,

    /// <summary>改行。</summary>
    EndOfLineTrivia,

    /// <summary><c>//</c> から行末までのコメント。</summary>
    SingleLineCommentTrivia,

    /// <summary><c>/*</c> から <c>*/</c> までのコメント。</summary>
    MultiLineCommentTrivia,
}
