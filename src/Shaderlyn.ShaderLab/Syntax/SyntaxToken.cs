using System.Collections.Immutable;
using Shaderlyn.Core.Text;

namespace Shaderlyn.ShaderLab.Syntax;

/// <summary>
/// トークンに付随する空白やコメント。
/// </summary>
/// <remarks>
/// テキストそのものは保持せず範囲だけを持つ。文字列を複製せずに済み、
/// 必要になった時点で <see cref="SourceText"/> から取り出せるためである。
/// コメントの内容は M4 の抑制コメント
/// (<c>// shaderlyn-disable-next-line SL1003</c>) の解釈で使う。
/// </remarks>
/// <param name="Kind">trivia の種別。</param>
/// <param name="Span">ソーステキスト上の範囲。</param>
public readonly record struct SyntaxTrivia(SyntaxKind Kind, TextSpan Span);

/// <summary>
/// ShaderLab の字句単位。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LeadingTrivia"/> と <see cref="TrailingTrivia"/> を保持することで完全忠実性を担保する。
/// トークン列を <see cref="FullSpan"/> の順に連結すると元のテキストが 1 文字も欠けずに復元でき、
/// この性質はラウンドトリップテストで全 fixture に対して検証している。
/// </para>
/// <para>
/// trivia の帰属規則は Roslyn に倣う。トークンの後ろにある空白・コメントは
/// <b>その行の改行までを含めて</b> 後続 trivia とし、それ以降は次のトークンの先行 trivia とする。
/// これにより「行末コメントは直前のトークンに付く」という直感どおりの対応になり、
/// 抑制コメントを「どの行に対する指示か」で解釈しやすくなる。
/// </para>
/// </remarks>
public sealed class SyntaxToken
{
    /// <summary>
    /// トークンを生成する。
    /// </summary>
    /// <param name="kind">トークンの種別。</param>
    /// <param name="span">trivia を除いたトークン本体の範囲。</param>
    /// <param name="text">トークン本体の文字列。</param>
    /// <param name="leadingTrivia">先行 trivia。</param>
    /// <param name="trailingTrivia">後続 trivia。</param>
    /// <param name="valueText">
    /// 文字列リテラルの引用符を外した値など、意味的な値。無い場合は <see langword="null"/>。
    /// </param>
    /// <param name="isMissing">構文エラーからの回復のために合成されたトークンかどうか。</param>
    public SyntaxToken(
        SyntaxKind kind,
        TextSpan span,
        string text,
        ImmutableArray<SyntaxTrivia> leadingTrivia = default,
        ImmutableArray<SyntaxTrivia> trailingTrivia = default,
        string? valueText = null,
        bool isMissing = false)
    {
        Kind = kind;
        Span = span;
        Text = text;
        LeadingTrivia = leadingTrivia.IsDefault ? [] : leadingTrivia;
        TrailingTrivia = trailingTrivia.IsDefault ? [] : trailingTrivia;
        ValueText = valueText ?? text;
        IsMissing = isMissing;
    }

    /// <summary>トークンの種別。</summary>
    public SyntaxKind Kind { get; }

    /// <summary>trivia を除いたトークン本体の範囲。診断の位置として使うのはこちら。</summary>
    public TextSpan Span { get; }

    /// <summary>trivia を含めた範囲。</summary>
    /// <remarks>
    /// 隣接するトークンの <see cref="FullSpan"/> は隙間なく連結し、
    /// 全トークンを通すとファイル全体を覆う。ラウンドトリップの根拠となる性質である。
    /// </remarks>
    public TextSpan FullSpan
    {
        get
        {
            int start = LeadingTrivia.Length > 0 ? LeadingTrivia[0].Span.Start : Span.Start;
            int end = TrailingTrivia.Length > 0 ? TrailingTrivia[^1].Span.End : Span.End;
            return TextSpan.FromBounds(start, end);
        }
    }

    /// <summary>トークン本体の文字列。</summary>
    public string Text { get; }

    /// <summary>
    /// 意味的な値。文字列リテラルなら引用符を外した中身、それ以外は <see cref="Text"/> と同じ。
    /// </summary>
    public string ValueText { get; }

    /// <summary>先行 trivia。</summary>
    public ImmutableArray<SyntaxTrivia> LeadingTrivia { get; }

    /// <summary>後続 trivia。</summary>
    public ImmutableArray<SyntaxTrivia> TrailingTrivia { get; }

    /// <summary>
    /// 構文エラーからの回復のために合成されたトークンかどうか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 期待するトークンが無かった場合、パーサは長さ 0 の欠落トークンを合成して木の形を保つ。
    /// これにより後続のルールは「そこに何かがある」前提で書け、
    /// 木の途中が <see langword="null"/> になる場合分けを各ルールに強いずに済む。
    /// </para>
    /// <para>
    /// <b>ルール側は、欠落トークンに対して診断を出してはならない。</b>
    /// 構文エラーは既に <c>SL0001</c> として報告されており、
    /// 同じ 1 か所の誤りについて重ねて指摘すると出力が読めなくなる。
    /// </para>
    /// </remarks>
    public bool IsMissing { get; }

    /// <summary>
    /// トークンのテキストを大文字小文字を無視して比較する。
    /// </summary>
    /// <param name="text">比較する文字列。</param>
    /// <returns>一致する場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// ShaderLab の命令名は大文字小文字を区別しない (<c>Cull Off</c> と <c>cull off</c> は等価)。
    /// 区別する実装にすると、正しく動作しているシェーダーに対して
    /// 「不明な命令」という誤った指摘を出すことになる。
    /// </remarks>
    public bool TextIs(string text) => string.Equals(Text, text, StringComparison.OrdinalIgnoreCase);

    /// <summary>トークンの種別とテキストを並べた文字列を返す。</summary>
    /// <returns>デバッグ用の文字列表現。</returns>
    public override string ToString() => IsMissing ? $"{Kind}(欠落)" : $"{Kind}('{Text}')";
}
