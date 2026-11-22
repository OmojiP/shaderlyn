using System.Collections.Immutable;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Core.Diagnostics;

/// <summary>
/// 指摘の修正 1 つ。エディタのクイックフィックスや CLI の修正として、まとめて 1 回で適用する。
/// </summary>
/// <param name="Title">操作の名前 (「static を付ける」など)。エディタのメニューに出る。</param>
/// <param name="Replacements">
/// 置き換えの並び。位置は指摘と同じファイル (<see cref="Diagnostic.Location"/> のソーステキスト) の中のもの。
/// 互いに重なってはならない。
/// </param>
/// <param name="IsPreferred">
/// 修正の方法が 1 つに決まり、既定の候補として扱ってよいか。
/// 候補から選んでもらう修正 (綴りの候補が複数あるなど) は <see langword="false"/> にする。
/// </param>
/// <remarks>
/// <para>
/// <b>指摘の範囲の外も直せる。</b> 宣言の前に修飾子を足す、ファイルの先頭に指令を足す、のように、
/// 指摘の範囲とは別の場所を書き換える修正を表すため、置き換えを並びで持つ。
/// 指摘の範囲をそのまま置き換えるだけなら <see cref="Diagnostic.WithSuggestedReplacement"/> でもよい。
/// </para>
/// <para>
/// <b>既定の候補 (<see cref="IsPreferred"/>) は、修正した後にその指摘が消え、意味が書き手の意図から変わらないものだけにする。</b>
/// エディタは既定の候補を 1 回の操作で適用でき、CLI はまとめて適用できる。「たぶんこうだろう」を既定にすると、誤りが広がる。
/// </para>
/// </remarks>
public sealed record DiagnosticFix(string Title, ImmutableArray<TextReplacement> Replacements, bool IsPreferred)
{
    /// <summary>置き換え 1 つの修正を作る。</summary>
    /// <param name="title">操作の名前。</param>
    /// <param name="span">置き換える範囲。</param>
    /// <param name="newText">置き換える文字列。</param>
    /// <param name="isPreferred">既定の候補として扱ってよいか。</param>
    /// <returns>作った修正。</returns>
    public static DiagnosticFix Replace(string title, TextSpan span, string newText, bool isPreferred = true)
        => new(title, [new TextReplacement(span, newText)], isPreferred);

    /// <summary>位置に文字列を差し込む修正を作る。</summary>
    /// <param name="title">操作の名前。</param>
    /// <param name="position">差し込む位置。</param>
    /// <param name="text">差し込む文字列。</param>
    /// <param name="isPreferred">既定の候補として扱ってよいか。</param>
    /// <returns>作った修正。</returns>
    public static DiagnosticFix Insert(string title, int position, string text, bool isPreferred = true)
        => new(title, [new TextReplacement(new TextSpan(position, 0), text)], isPreferred);
}

/// <summary>
/// ソーステキストの範囲 1 つを、文字列で置き換える。幅 0 の範囲なら差し込み、空の文字列なら削除になる。
/// </summary>
/// <param name="Span">置き換える範囲。</param>
/// <param name="NewText">置き換える文字列。</param>
public readonly record struct TextReplacement(TextSpan Span, string NewText);
