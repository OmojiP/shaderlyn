using Shaderlyn.Core.Text;

namespace Shaderlyn.Core.Diagnostics;

/// <summary>
/// 診断が指し示すソースコード上の位置。
/// </summary>
/// <remarks>
/// <see cref="SourceText"/> への参照を保持しているため、出力層は
/// 位置オブジェクトだけを受け取れば行番号も該当行のテキストも取り出せる。
/// 診断を作る側がファイルを開き直す必要が無いようにするための設計である。
/// </remarks>
public sealed class Location
{
    private Location(SourceText source, TextSpan span)
    {
        Source = source;
        Span = span;
    }

    /// <summary>位置が属するソーステキスト。</summary>
    public SourceText Source { get; }

    /// <summary>ソーステキスト上の文字範囲。</summary>
    public TextSpan Span { get; }

    /// <summary>位置が属するファイルのパス。</summary>
    public string FilePath => Source.FilePath;

    /// <summary>行・桁で表した範囲。</summary>
    public LinePositionSpan LineSpan => Source.GetLinePositionSpan(Span);

    /// <summary>
    /// ソーステキストと文字範囲から位置を生成する。
    /// </summary>
    /// <param name="source">対象のソーステキスト。</param>
    /// <param name="span">対象の文字範囲。</param>
    /// <returns>生成された位置。</returns>
    /// <exception cref="ArgumentOutOfRangeException">範囲がソーステキストの外にはみ出す場合。</exception>
    public static Location Create(SourceText source, TextSpan span)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(span.End, source.Length);
        return new Location(source, span);
    }

    /// <summary>
    /// この位置が含まれる行のテキストを返す。
    /// </summary>
    /// <returns>該当行の文字列 (改行文字を含まない)。複数行にまたがる場合は開始行を返す。</returns>
    /// <remarks>コンソール出力で指摘箇所のソース断片を表示するために使う。</remarks>
    public string GetLineText() => Source.GetLineText(LineSpan.Start.Line);

    /// <summary>この位置を <c>パス(行,桁)</c> 形式で表す。</summary>
    /// <returns>人間が読む前提の文字列表現。</returns>
    public override string ToString() => $"{FilePath}({LineSpan.Start})";
}
