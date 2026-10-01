using System.Text.Json.Serialization;

namespace Shaderlyn.Cli.Caching;

/// <summary>
/// キャッシュに記録するルール定義。
/// </summary>
/// <remarks>
/// <b>ルール定義もキャッシュへ書き出す。</b>
/// 復元した診断を SARIF などへ出力するには、表題・分類・詳細ページの URL が要る。
/// 実行時のルール一覧から引き直す手もあるが、
/// 設定ファイルで定義された利用者のルールは一覧に現れないため、
/// 「自作ルールの指摘だけが、気づかないままキャッシュから落ちる」ことになる。
/// </remarks>
internal sealed class CachedRule
{
    /// <summary>ルール ID。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>ルールの表題。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>分類。</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>設定による上書きが無い場合の重要度。</summary>
    [JsonPropertyName("severity")]
    public string DefaultSeverity { get; set; } = string.Empty;

    /// <summary>ルールの詳細説明。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>詳細ドキュメントへの URL。</summary>
    [JsonPropertyName("help")]
    public string? HelpLinkUri { get; set; }
}

/// <summary>
/// キャッシュに記録する指摘 1 件。
/// </summary>
/// <remarks>
/// <b>メッセージは組み立て終わった文字列で持つ。</b>
/// 書式と引数のまま持つと、引数の型 (数値・列挙・独自の型) を
/// JSON へ往復させる必要があり、書式指定子の解釈がずれた瞬間に
/// 「キャッシュしたときだけ文面が違う」という最悪の症状になる。
/// </remarks>
internal sealed class CachedDiagnostic
{
    /// <summary><see cref="AnalysisCacheDocument.Rules"/> の添字。</summary>
    [JsonPropertyName("rule")]
    public int Rule { get; set; }

    /// <summary>
    /// 指摘箇所があるファイルの、<see cref="AnalysisCacheDocument.Sources"/> の添字。
    /// 解析したファイル自身なら -1。
    /// </summary>
    /// <remarks>
    /// 利用者が書いて取り込んだヘッダの指摘は、そのヘッダを指す。
    /// ヘッダは取り込むファイル一式 (<see cref="CachedFile.Includes"/>) に入っており、内容の一致はそちらで確かめる。
    /// </remarks>
    [JsonPropertyName("file")]
    public int File { get; set; } = -1;

    /// <summary>指摘箇所の開始位置。</summary>
    [JsonPropertyName("start")]
    public int Start { get; set; }

    /// <summary>指摘箇所の長さ。</summary>
    [JsonPropertyName("length")]
    public int Length { get; set; }

    /// <summary>設定の上書きを適用したあとの実効重要度。</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    /// <summary>報告元が重要度を明示しているかどうか。</summary>
    [JsonPropertyName("explicit")]
    public bool IsSeverityExplicit { get; set; }

    /// <summary>組み立て終わったメッセージ。</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>指摘箇所をそのまま置き換えられる文字列。</summary>
    [JsonPropertyName("fix")]
    public string? SuggestedReplacement { get; set; }
}

/// <summary>
/// 取り込んだファイル 1 つと、記録した時点でのその内容のハッシュ。
/// </summary>
/// <remarks>
/// パスは <see cref="AnalysisCacheDocument.Sources"/> の添字で持つ。
/// 中規模プロジェクトでは同じヘッダ群を数百のシェーダーが取り込むため、
/// パスをそのまま並べるとキャッシュファイルの大半が同じ文字列の繰り返しになる。
/// </remarks>
internal sealed class CachedInclude
{
    /// <summary><see cref="AnalysisCacheDocument.Sources"/> の添字。</summary>
    [JsonPropertyName("i")]
    public int Source { get; set; }

    /// <summary>記録した時点での内容のハッシュ。</summary>
    [JsonPropertyName("h")]
    public string Hash { get; set; } = string.Empty;
}

/// <summary>
/// 1 ファイル分の解析結果。
/// </summary>
internal sealed class CachedFile
{
    /// <summary><see cref="AnalysisCacheDocument.Sources"/> の添字。</summary>
    [JsonPropertyName("i")]
    public int Source { get; set; }

    /// <summary>記録した時点でのこのファイルの内容のハッシュ。</summary>
    [JsonPropertyName("h")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// 解析中に解決した (あるいは解決できなかった) 取り込み先。
    /// </summary>
    /// <remarks>
    /// <b>これがキャッシュの要点である。</b>
    /// ヘッダを 1 つ直したら、それを取り込むシェーダーはすべて解析し直さなければならない。
    /// 解決できなかったパスも記録する。あとからファイルが増えれば結果が変わるためである。
    /// </remarks>
    [JsonPropertyName("includes")]
    public List<CachedInclude> Includes { get; set; } = [];

    /// <summary>そのファイルで報告された指摘。</summary>
    [JsonPropertyName("diagnostics")]
    public List<CachedDiagnostic> Diagnostics { get; set; } = [];
}

/// <summary>
/// キャッシュファイルの内容。
/// </summary>
internal sealed class AnalysisCacheDocument
{
    /// <summary>キャッシュの書式のバージョン。</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = AnalysisCache.SupportedVersion;

    /// <summary>
    /// 解析ツールと設定を表す鍵。
    /// </summary>
    /// <remarks>
    /// 一致しない場合、記録されている結果は<b>すべて捨てる</b>。
    /// 設定やルールが変わったのに古い結果を出すのが、この仕組みの最悪の壊れ方である。
    /// </remarks>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>ファイルパスの表。ほかの項目は添字で参照する。</summary>
    [JsonPropertyName("sources")]
    public List<string> Sources { get; set; } = [];

    /// <summary>ルール定義の表。</summary>
    [JsonPropertyName("rules")]
    public List<CachedRule> Rules { get; set; } = [];

    /// <summary>ファイルごとの結果。</summary>
    [JsonPropertyName("files")]
    public List<CachedFile> Files { get; set; } = [];
}

/// <summary>
/// キャッシュの入出力に使う JSON の型情報。
/// </summary>
/// <remarks>
/// Native AOT ではリフレクションによる直列化が使えないため、
/// ソース生成された型情報を使う。
/// </remarks>
[JsonSerializable(typeof(AnalysisCacheDocument))]
internal sealed partial class AnalysisCacheJsonContext : JsonSerializerContext;
