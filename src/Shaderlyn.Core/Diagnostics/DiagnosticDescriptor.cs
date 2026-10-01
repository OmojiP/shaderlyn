using System.Text.RegularExpressions;

namespace Shaderlyn.Core.Diagnostics;

/// <summary>
/// 1 つのルールの定義。ルール ID・表題・メッセージ書式・既定の重要度などを保持する。
/// </summary>
/// <remarks>
/// <para>
/// Roslyn の <c>DiagnosticDescriptor</c> と同じ役割を持つ。
/// アナライザは実行のたびに新しく生成するのではなく、
/// <c>static readonly</c> フィールドとして 1 つだけ定義したものを使い回す。
/// これにより CLI が「実装されている全ルールの一覧」を実行前に列挙でき、
/// SARIF の <c>rules[]</c> セクションやドキュメント生成の入力にできる。
/// </para>
/// <para>
/// <see cref="Description"/> と <see cref="HelpLinkUri"/> は必須ではないが、
/// 組み込みルールについてはテストで存在を検証している。
/// 「なぜ問題なのか」が読めない指摘は無視されるか、誤って抑制されるためである。
/// </para>
/// </remarks>
public sealed partial record DiagnosticDescriptor
{
    /// <summary>
    /// ルール定義を生成する。
    /// </summary>
    /// <param name="id">ルール ID。英大文字の接頭辞と 4 桁の数字から成る (例: <c>SL1003</c>)。</param>
    /// <param name="title">ルールの表題。一覧表示に使う短い説明。</param>
    /// <param name="messageFormat">
    /// 診断メッセージの書式。<see cref="string.Format(IFormatProvider,string,object?[])"/> の書式指定子を含められる。
    /// </param>
    /// <param name="category">分類 (例: <c>Correctness</c>、<c>Naming</c>、<c>Usage</c>)。</param>
    /// <param name="defaultSeverity">設定による上書きが無い場合の重要度。</param>
    /// <param name="description">ルールの詳細説明。何が問題でどう直すかを書く。</param>
    /// <param name="helpLinkUri">詳細ドキュメントへの URL。</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> が規定の書式に合わない場合、
    /// または <paramref name="title"/>・<paramref name="messageFormat"/>・<paramref name="category"/> が空の場合。
    /// </exception>
    public DiagnosticDescriptor(
        string id,
        string title,
        string messageFormat,
        string category,
        DiagnosticSeverity defaultSeverity,
        string? description = null,
        string? helpLinkUri = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageFormat);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        if (!IdPattern().IsMatch(id))
        {
            throw new ArgumentException(
                $"ルール ID '{id}' の書式が不正です。英大文字の接頭辞と 4 桁の数字である必要があります (例: SL1003)。",
                nameof(id));
        }

        if (defaultSeverity == DiagnosticSeverity.None)
        {
            throw new ArgumentException(
                $"ルール {id} の既定の重要度に None は指定できません。無効化は設定ファイルで行ってください。",
                nameof(defaultSeverity));
        }

        Id = id;
        Title = title;
        MessageFormat = messageFormat;
        Category = category;
        DefaultSeverity = defaultSeverity;
        Description = description;
        HelpLinkUri = helpLinkUri;
    }

    /// <summary>ルール ID (例: <c>SL1003</c>)。</summary>
    public string Id { get; }

    /// <summary>ルールの表題。</summary>
    public string Title { get; }

    /// <summary>診断メッセージの書式。</summary>
    public string MessageFormat { get; }

    /// <summary>ルールの分類。</summary>
    public string Category { get; }

    /// <summary>設定による上書きが無い場合の重要度。</summary>
    public DiagnosticSeverity DefaultSeverity { get; }

    /// <summary>ルールの詳細説明。</summary>
    public string? Description { get; }

    /// <summary>詳細ドキュメントへの URL。</summary>
    public string? HelpLinkUri { get; }

    /// <summary>
    /// ルール ID の接頭辞 (英字部分)。
    /// </summary>
    /// <remarks>
    /// <c>SL</c> は ShaderLab、<c>HL</c> は HLSL、<c>URP</c> は URP プロファイル、
    /// <c>USER</c> は設定ファイル由来のユーザー定義ルールを表す。
    /// </remarks>
    public string IdPrefix => IdPattern().Match(Id).Groups["prefix"].Value;

    /// <summary>
    /// <see cref="MessageFormat"/> が必要とする引数の数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 書式の穴 (<c>{0}</c> <c>{1}</c> …) のうち最も大きい番号に 1 を足した値である。
    /// 穴が無ければ 0 になる。
    /// </para>
    /// <para>
    /// <b>報告のたびに数え直さない。</b>
    /// ルール定義は作られたあと変わらないので、一度数えたら覚えておける。
    /// この値は、渡された引数の数と食い違っていないかの確認に使う
    /// (<c>TOOL0006</c>)。
    /// </para>
    /// <para>
    /// <c>{{</c> と <c>}}</c> は文字としての波括弧なので数えない。
    /// </para>
    /// </remarks>
    public int RequiredMessageArgumentCount => _requiredMessageArgumentCount ??= CountPlaceholders(MessageFormat);

    private int? _requiredMessageArgumentCount;

    /// <summary>ルール ID と表題を並べた文字列を返す。</summary>
    /// <returns>デバッグ用の文字列表現。</returns>
    public override string ToString() => $"{Id}: {Title}";

    /// <summary>ルール ID の書式を検証する正規表現。</summary>
    /// <returns>コンパイル済みの正規表現。</returns>
    [GeneratedRegex(@"^(?<prefix>[A-Z]+)(?<number>[0-9]{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>
    /// 書式が必要とする引数の数を数える。
    /// </summary>
    /// <param name="format">対象の書式。</param>
    /// <returns>必要な引数の数。穴が無ければ 0。</returns>
    /// <remarks>
    /// 番号は飛んでいてもよい。<c>{2}</c> だけを使う書式は引数を 3 個必要とする。
    /// <c>string.Format</c> がそう振る舞うので、それに合わせる。
    /// </remarks>
    private static int CountPlaceholders(string format)
    {
        int required = 0;

        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] != '{')
            {
                continue;
            }

            // {{ は文字としての波括弧である。穴ではない。
            if (i + 1 < format.Length && format[i + 1] == '{')
            {
                i++;
                continue;
            }

            int digits = i + 1;
            int index = 0;

            while (digits < format.Length && char.IsAsciiDigit(format[digits]))
            {
                index = (index * 10) + (format[digits] - '0');
                digits++;
            }

            // 数字が 1 桁も無ければ穴ではない。
            if (digits == i + 1)
            {
                continue;
            }

            required = Math.Max(required, index + 1);
            i = digits - 1;
        }

        return required;
    }
}
