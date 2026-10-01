using System.Globalization;

namespace Shaderlyn.Core.Diagnostics;

/// <summary>
/// 1 件の指摘。どのルールが、どこで、どの重要度で何を報告したかを表す。
/// </summary>
/// <remarks>
/// <see cref="Severity"/> は <see cref="DiagnosticDescriptor.DefaultSeverity"/> とは別に保持している。
/// 設定ファイルによる重要度の上書きが適用された結果をここに入れるためであり、
/// 出力層はルール定義を見ずにこの値だけを見ればよい。
/// </remarks>
public sealed class Diagnostic
{
    private readonly object?[] _messageArguments;

    private Diagnostic(
        DiagnosticDescriptor descriptor,
        Location location,
        DiagnosticSeverity severity,
        bool isSeverityExplicit,
        object?[] messageArguments,
        string? suggestedReplacement)
    {
        Descriptor = descriptor;
        Location = location;
        Severity = severity;
        IsSeverityExplicit = isSeverityExplicit;
        _messageArguments = messageArguments;
        SuggestedReplacement = suggestedReplacement;
    }

    /// <summary>
    /// 指摘箇所をそのまま置き換えられる文字列。無い場合は <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>直し方が一意に決まる指摘だけが持つ。</b>
    /// スペルミスのように「正しい綴りが分かっている」場合、
    /// その知識はルールの側にしかない。
    /// メッセージの文面から読み取らせると、文面を変えるたびに直しが壊れる。
    /// </para>
    /// <para>
    /// <see cref="Location"/> の範囲をこの文字列で置き換えれば直る、という約束である。
    /// 引用符を含む範囲を指しているなら、引用符ごと置き換えられる文字列でなければならない。
    /// </para>
    /// </remarks>
    public string? SuggestedReplacement { get; }

    /// <summary>報告元のルール定義。</summary>
    public DiagnosticDescriptor Descriptor { get; }

    /// <summary>指摘箇所。</summary>
    public Location Location { get; }

    /// <summary>設定の上書きを適用したあとの実効重要度。</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>
    /// 報告元が重要度を明示しているかどうか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 設定ファイルで 1 件ずつ重要度を指定できるルール
    /// (<c>banned-symbols</c> など) のために必要な区別である。
    /// 通常の診断はルール単位の設定で重要度が決まるが、
    /// これらは同じルール ID の中で件ごとに重要度が異なる。
    /// </para>
    /// <para>
    /// <b>ルールを無効化する設定は、明示された重要度より優先される。</b>
    /// 無効化はその指摘を見たくないという意思表示であり、
    /// 個別の重要度指定で覆されてはならない。
    /// </para>
    /// </remarks>
    public bool IsSeverityExplicit { get; }

    /// <summary>ルール ID。</summary>
    public string Id => Descriptor.Id;

    /// <summary>
    /// メッセージ書式へ渡された引数の数。
    /// </summary>
    /// <remarks>
    /// <see cref="DiagnosticDescriptor.RequiredMessageArgumentCount"/> と突き合わせて、
    /// 書式の穴が埋まらないまま利用者へ表示されるのを防ぐために使う (<c>TOOL0006</c>)。
    /// </remarks>
    public int MessageArgumentCount => _messageArguments.Length;

    /// <summary>
    /// 診断を生成する。重要度はルールの既定値を使う。
    /// </summary>
    /// <param name="descriptor">報告元のルール定義。</param>
    /// <param name="location">指摘箇所。</param>
    /// <param name="messageArguments">メッセージ書式へ埋め込む引数。</param>
    /// <returns>生成された診断。</returns>
    public static Diagnostic Create(DiagnosticDescriptor descriptor, Location location, params object?[] messageArguments)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(location);
        return new Diagnostic(
            descriptor, location, descriptor.DefaultSeverity, isSeverityExplicit: false, messageArguments ?? [],
            suggestedReplacement: null);
    }

    /// <summary>
    /// 直し方を添えた複製を返す。
    /// </summary>
    /// <param name="replacement">指摘箇所を置き換える文字列。</param>
    /// <returns>直し方を添えた診断。元の診断は変更しない。</returns>
    /// <remarks>
    /// <b>直し方が一意に決まる場合にだけ添えること。</b>
    /// 「たぶんこうだろう」を添えると、
    /// エディタがそれを 1 回の操作で適用できてしまい、誤りが広がる。
    /// </remarks>
    public Diagnostic WithSuggestedReplacement(string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        return new Diagnostic(Descriptor, Location, Severity, IsSeverityExplicit, _messageArguments, replacement);
    }

    /// <summary>
    /// 重要度を明示して診断を生成する。
    /// </summary>
    /// <param name="descriptor">報告元のルール定義。</param>
    /// <param name="location">指摘箇所。</param>
    /// <param name="severity">適用する実効重要度。</param>
    /// <param name="messageArguments">メッセージ書式へ埋め込む引数。</param>
    /// <returns>生成された診断。</returns>
    /// <remarks>
    /// <para>
    /// 設定ファイルで 1 件ずつ重要度を指定できるルールが使う。
    /// この経路で作られた診断は <see cref="IsSeverityExplicit"/> が真になり、
    /// ルール単位の重要度設定では上書きされない。
    /// </para>
    /// <para>
    /// ルールを無効化する設定 (<see cref="DiagnosticSeverity.None"/>) は依然として優先される。
    /// </para>
    /// </remarks>
    public static Diagnostic Create(
        DiagnosticDescriptor descriptor,
        Location location,
        DiagnosticSeverity severity,
        params object?[] messageArguments)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(location);
        return new Diagnostic(
            descriptor, location, severity, isSeverityExplicit: true, messageArguments ?? [],
            suggestedReplacement: null);
    }

    /// <summary>
    /// 重要度だけを差し替えた複製を返す。
    /// </summary>
    /// <param name="severity">新しい重要度。</param>
    /// <returns>重要度を差し替えた診断。元の診断は変更しない。</returns>
    public Diagnostic WithSeverity(DiagnosticSeverity severity)
        => severity == Severity
            ? this
            : new Diagnostic(Descriptor, Location, severity, IsSeverityExplicit, _messageArguments, SuggestedReplacement);

    /// <summary>
    /// 位置だけを差し替えた複製を返す。
    /// </summary>
    /// <param name="location">新しい位置。</param>
    /// <returns>位置を差し替えた診断。元の診断は変更しない。</returns>
    /// <remarks>
    /// <b>同じ指摘を別のソーステキスト上の位置として言い直すための手段である。</b>
    /// 埋め込み HLSL は元のファイルをマスクした複製の上で解析されるため、
    /// そこで出た診断は元のファイルを指すものとして報告し直す必要がある。
    /// オフセットは複製と元のファイルで完全に一致するので、範囲はそのまま持ち替えてよい。
    /// </remarks>
    public Diagnostic WithLocation(Location location)
    {
        ArgumentNullException.ThrowIfNull(location);

        return ReferenceEquals(location, Location)
            ? this
            : new Diagnostic(Descriptor, location, Severity, IsSeverityExplicit, _messageArguments, SuggestedReplacement);
    }

    /// <summary>
    /// 書式に引数を埋め込んだ最終的なメッセージを返す。
    /// </summary>
    /// <returns>利用者に提示するメッセージ。</returns>
    /// <remarks>
    /// 書式指定子と引数の数が食い違っていても例外にせず、書式文字列をそのまま返す。
    /// ルール実装の記述ミスでツール全体が停止するのは、リンタとして望ましくないためである。
    /// </remarks>
    public string GetMessage()
    {
        if (_messageArguments.Length == 0)
        {
            return Descriptor.MessageFormat;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, Descriptor.MessageFormat, _messageArguments);
        }
        catch (FormatException)
        {
            return Descriptor.MessageFormat;
        }
    }

    /// <summary>
    /// 診断をソースコード上の出現順に並べるための比較子。
    /// </summary>
    /// <remarks>
    /// ファイルパス、位置、ルール ID の順で比較する。
    /// 出力順が実行ごとに揺れるとゴールデンテストが不安定になり、
    /// SARIF の差分も無意味に膨らむため、完全に決定的な順序を与える必要がある。
    /// </remarks>
    public static IComparer<Diagnostic> DocumentOrderComparer { get; } = new DocumentOrderComparerImpl();

    /// <summary>この診断を <c>パス(行,桁): 重要度 ID: メッセージ</c> 形式で表す。</summary>
    /// <returns>人間が読む前提の文字列表現。</returns>
    public override string ToString()
        => $"{Location}: {Severity.ToString().ToLowerInvariant()} {Id}: {GetMessage()}";

    private sealed class DocumentOrderComparerImpl : IComparer<Diagnostic>
    {
        public int Compare(Diagnostic? x, Diagnostic? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            int byPath = string.CompareOrdinal(x.Location.FilePath, y.Location.FilePath);
            if (byPath != 0)
            {
                return byPath;
            }

            int bySpan = x.Location.Span.CompareTo(y.Location.Span);
            return bySpan != 0 ? bySpan : string.CompareOrdinal(x.Id, y.Id);
        }
    }
}
