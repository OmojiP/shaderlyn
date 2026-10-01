using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Cli.Baseline;

/// <summary>
/// ベースラインに記録される指摘 1 種類分。
/// </summary>
/// <remarks>
/// <b>行番号は記録しない。</b>
/// 無関係な行を 1 行足しただけでベースラインが総崩れになるようでは、
/// 既存プロジェクトへの導入という目的を果たせない。
/// 代わりに、指摘が出ている行の内容を正規化した指紋で照合する。
/// </remarks>
internal sealed class BaselineEntry
{
    /// <summary>ルール ID。</summary>
    [JsonPropertyName("rule")]
    public string Rule { get; set; } = string.Empty;

    /// <summary>ベースラインファイルからの相対パス。</summary>
    /// <remarks>
    /// <b>相対パスにするのは、ベースラインをバージョン管理へ入れるためである。</b>
    /// 絶対パスを記録すると、別のマシンやランナーで一切照合できない。
    /// </remarks>
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    /// <summary>指摘が出ている行の内容を正規化した指紋。</summary>
    [JsonPropertyName("snippet")]
    public string Snippet { get; set; } = string.Empty;

    /// <summary>同じ指紋で記録された件数。</summary>
    /// <remarks>
    /// 同じ内容の行が同じファイルに複数あると指紋が一致する。
    /// 件数を持たないと、そのうち 1 件を直しても残りが抑制され続ける。
    /// </remarks>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;
}

/// <summary>
/// ベースラインファイルの内容。
/// </summary>
internal sealed class BaselineDocument
{
    /// <summary>ベースラインの書式のバージョン。</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = DiagnosticBaseline.SupportedVersion;

    /// <summary>記録された指摘。</summary>
    [JsonPropertyName("entries")]
    public List<BaselineEntry> Entries { get; set; } = [];
}

/// <summary>
/// ベースラインの入出力に使う JSON の型情報。
/// </summary>
/// <remarks>
/// Native AOT ではリフレクションによる直列化が使えないため、
/// ソース生成された型情報を使う。
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BaselineDocument))]
internal sealed partial class BaselineJsonContext : JsonSerializerContext;

/// <summary>
/// 既存の指摘を凍結し、新規に混入したものだけを報告する仕組み。
/// </summary>
/// <remarks>
/// <para>
/// <b>既存プロジェクトへ静的解析を導入するには、これが無いと始まらない。</b>
/// 数百件の指摘が出る状態で「すべて直してから導入する」のは現実的でなく、
/// かといって指摘を放置したまま運用しても新規の混入に気づけない。
/// </para>
/// <para>
/// ベースラインはバージョン管理へ入れることを前提としている。
/// そのためパスは相対で記録し、内容の順序も決定的にする。
/// 実行のたびに差分が出るファイルはバージョン管理に置けない。
/// </para>
/// </remarks>
internal sealed class DiagnosticBaseline
{
    /// <summary>このツールが理解するベースラインの書式のバージョン。</summary>
    public const int SupportedVersion = 1;

    /// <summary>ベースラインファイルの既定の名前。</summary>
    public const string FileName = ".shaderlyn-baseline.json";

    private readonly Dictionary<string, int> _remaining;
    private readonly string _baseDirectory;

    private DiagnosticBaseline(Dictionary<string, int> remaining, string baseDirectory)
    {
        _remaining = remaining;
        _baseDirectory = baseDirectory;
    }

    /// <summary>記録されている指摘の総数。</summary>
    public int TotalCount { get; private init; }

    /// <summary>
    /// ベースラインファイルを読み込む。
    /// </summary>
    /// <param name="filePath">ベースラインファイルのパス。</param>
    /// <param name="error">読み込みに失敗した場合の説明。</param>
    /// <returns>読み込んだベースライン。失敗した場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>読み込みの失敗を握り潰してはならない。</b>
    /// ベースラインが読めていないことに気づかないまま実行すると、
    /// 抑制されるはずの指摘が大量に出るか、
    /// 逆に「新規の指摘が無い」と誤解する状態になる。
    /// </remarks>
    public static DiagnosticBaseline? Load(string filePath, out string? error)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            string json = System.IO.File.ReadAllText(filePath);
            BaselineDocument? document = JsonSerializer.Deserialize(json, BaselineJsonContext.Default.BaselineDocument);

            if (document is null)
            {
                error = "ベースラインファイルの内容が空です。";
                return null;
            }

            if (document.Version > SupportedVersion)
            {
                error = $"ベースラインのバージョン {document.Version} はこのツールより新しいものです "
                    + $"(対応しているバージョン: {SupportedVersion})。";
                return null;
            }

            Dictionary<string, int> remaining = new(StringComparer.Ordinal);
            int total = 0;

            foreach (BaselineEntry entry in document.Entries)
            {
                string key = CreateKey(entry.Rule, entry.File, entry.Snippet);
                int count = Math.Max(1, entry.Count);

                remaining[key] = remaining.GetValueOrDefault(key) + count;
                total += count;
            }

            error = null;
            return new DiagnosticBaseline(remaining, GetBaseDirectory(filePath)) { TotalCount = total };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// ベースラインに記録されていない指摘だけを残す。
    /// </summary>
    /// <param name="diagnostics">対象の診断。</param>
    /// <returns>新規に混入した診断。</returns>
    /// <remarks>
    /// 同じ指紋が複数記録されている場合、その件数までを抑制する。
    /// 件数を超えた分は新規として報告する。
    /// </remarks>
    public ImmutableArray<Diagnostic> Filter(ImmutableArray<Diagnostic> diagnostics)
    {
        Dictionary<string, int> budget = new(_remaining, StringComparer.Ordinal);
        ImmutableArray<Diagnostic>.Builder kept = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach (Diagnostic diagnostic in diagnostics)
        {
            string key = CreateKey(diagnostic, _baseDirectory);

            if (budget.TryGetValue(key, out int count) && count > 0)
            {
                budget[key] = count - 1;
                continue;
            }

            kept.Add(diagnostic);
        }

        return kept.ToImmutable();
    }

    /// <summary>
    /// 診断からベースラインファイルの内容を組み立てる。
    /// </summary>
    /// <param name="diagnostics">記録する診断。</param>
    /// <param name="filePath">ベースラインファイルのパス。相対パスの基点になる。</param>
    /// <returns>書き出す JSON。</returns>
    /// <remarks>
    /// 出力は決定的に並べる。実行のたびに差分が出るファイルはバージョン管理に置けない。
    /// </remarks>
    public static string CreateContent(ImmutableArray<Diagnostic> diagnostics, string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        string baseDirectory = GetBaseDirectory(filePath);
        Dictionary<string, BaselineEntry> entries = new(StringComparer.Ordinal);

        foreach (Diagnostic diagnostic in diagnostics)
        {
            string relativePath = GetRelativePath(diagnostic.Location.FilePath, baseDirectory);
            string snippet = ComputeSnippet(diagnostic);
            string key = CreateKey(diagnostic.Id, relativePath, snippet);

            if (entries.TryGetValue(key, out BaselineEntry? existing))
            {
                existing.Count++;
                continue;
            }

            entries[key] = new BaselineEntry
            {
                Rule = diagnostic.Id,
                File = relativePath,
                Snippet = snippet,
                Count = 1,
            };
        }

        BaselineDocument document = new()
        {
            Version = SupportedVersion,
            Entries =
            [
                .. entries.Values
                    .OrderBy(e => e.File, StringComparer.Ordinal)
                    .ThenBy(e => e.Rule, StringComparer.Ordinal)
                    .ThenBy(e => e.Snippet, StringComparer.Ordinal)
            ],
        };

        return JsonSerializer.Serialize(document, BaselineJsonContext.Default.BaselineDocument);
    }

    /// <summary>ベースラインファイルの置かれたフォルダーを求める。</summary>
    /// <param name="filePath">ベースラインファイルのパス。</param>
    /// <returns>基点となるフォルダー。</returns>
    private static string GetBaseDirectory(string filePath)
        => Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Directory.GetCurrentDirectory();

    private static string CreateKey(Diagnostic diagnostic, string baseDirectory)
        => CreateKey(
            diagnostic.Id,
            GetRelativePath(diagnostic.Location.FilePath, baseDirectory),
            ComputeSnippet(diagnostic));

    private static string CreateKey(string ruleId, string relativePath, string snippet)
        => $"{ruleId}\0{relativePath}\0{snippet}";

    /// <summary>
    /// パスをベースラインファイルからの相対に直す。
    /// </summary>
    /// <param name="path">対象のパス。</param>
    /// <param name="baseDirectory">基点となるフォルダー。</param>
    /// <returns>相対パス。区切りは <c>/</c> に統一する。</returns>
    /// <remarks>
    /// 区切り文字を統一するのは、Windows で作ったベースラインを
    /// Linux のランナーで照合できるようにするためである。
    /// </remarks>
    private static string GetRelativePath(string path, string baseDirectory)
    {
        try
        {
            return Path.GetRelativePath(baseDirectory, path).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return path.Replace('\\', '/');
        }
    }

    /// <summary>
    /// 指摘が出ている行の内容から指紋を計算する。
    /// </summary>
    /// <param name="diagnostic">対象の診断。</param>
    /// <returns>16 桁の 16 進数。</returns>
    /// <remarks>
    /// SARIF の <c>partialFingerprints</c> と同じ計算を使う。
    /// 別々に計算すると、片方だけが一致しなくなるという分かりにくい挙動になる。
    /// </remarks>
    private static string ComputeSnippet(Diagnostic diagnostic)
        => DiagnosticFingerprint.ComputeSnippetHash(diagnostic);
}
