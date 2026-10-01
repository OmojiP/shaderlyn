using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Cli.Reporting;

/// <summary>
/// 診断を SARIF 2.1.0 として出力する。
/// </summary>
/// <remarks>
/// <para>
/// GitHub の Code Scanning へアップロードするための形式である。
/// アップロードすると PR の差分にインライン注釈が出て、
/// セキュリティタブに一覧が蓄積される。
/// </para>
/// <para>
/// <b>直列化は手で書いている。</b>
/// SARIF の構造は入れ子が深く、必要なのはそのごく一部である。
/// 対応する型を並べるより、書き出す内容をそのまま読めるほうが保守しやすい。
/// Native AOT でリフレクションによる直列化が使えないという事情もある。
/// </para>
/// </remarks>
internal sealed class SarifDiagnosticReporter
{
    /// <summary>SARIF のバージョン。GitHub が受け付けるのは 2.1.0 である。</summary>
    private const string SarifVersion = "2.1.0";

    /// <summary>スキーマの URI。</summary>
    private const string SchemaUri =
        "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json";

    private readonly ImmutableArray<DiagnosticDescriptor> _rules;
    private readonly string _basePath;
    private readonly string _toolVersion;

    /// <summary>
    /// SARIF レポータを生成する。
    /// </summary>
    /// <param name="rules">出力に含めるルール定義。</param>
    /// <param name="basePath">ファイルパスを相対化する基点。</param>
    /// <param name="toolVersion">ツールのバージョン。</param>
    public SarifDiagnosticReporter(
        ImmutableArray<DiagnosticDescriptor> rules,
        string basePath,
        string toolVersion)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        ArgumentNullException.ThrowIfNull(toolVersion);

        _rules = rules;
        _basePath = basePath;
        _toolVersion = toolVersion;
    }

    /// <summary>
    /// 診断の一覧を SARIF として整形する。
    /// </summary>
    /// <param name="diagnostics">出力する診断。</param>
    /// <returns>SARIF の JSON。</returns>
    public string Format(ImmutableArray<Diagnostic> diagnostics)
    {
        ArrayBufferWriter<byte> buffer = new();

        // 非 ASCII 文字をそのまま出す。詳細は JsonDiagnosticReporter.WriterOptions を参照。
        using (Utf8JsonWriter writer = new(buffer, JsonDiagnosticReporter.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", SchemaUri);
            writer.WriteString("version", SarifVersion);

            writer.WriteStartArray("runs");
            WriteRun(writer, diagnostics);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void WriteRun(Utf8JsonWriter writer, ImmutableArray<Diagnostic> diagnostics)
    {
        writer.WriteStartObject();

        writer.WriteStartObject("tool");
        writer.WriteStartObject("driver");
        writer.WriteString("name", "shaderlyn");
        writer.WriteString("semanticVersion", _toolVersion);
        writer.WriteString("informationUri", "https://github.com/OmojiP/shaderlyn");

        writer.WriteStartArray("rules");

        foreach (DiagnosticDescriptor rule in _rules)
        {
            WriteRule(writer, rule);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartArray("results");

        foreach (Diagnostic diagnostic in diagnostics)
        {
            WriteResult(writer, diagnostic);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// ルール定義 1 件を書き出す。
    /// </summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="rule">対象のルール。</param>
    /// <remarks>
    /// <c>defaultConfiguration.level</c> は既定の重要度であり、
    /// 実際の指摘の重要度 (設定で上書きされうる) とは別に書く。
    /// GitHub は結果側の <c>level</c> を優先する。
    /// </remarks>
    private static void WriteRule(Utf8JsonWriter writer, DiagnosticDescriptor rule)
    {
        writer.WriteStartObject();
        writer.WriteString("id", rule.Id);
        writer.WriteString("name", rule.Id);

        writer.WriteStartObject("shortDescription");
        writer.WriteString("text", rule.Title);
        writer.WriteEndObject();

        if (rule.Description is { Length: > 0 } description)
        {
            writer.WriteStartObject("fullDescription");
            writer.WriteString("text", description);
            writer.WriteEndObject();

            // GitHub のアラート画面はここを本文として表示する。
            writer.WriteStartObject("help");
            writer.WriteString("text", description);
            writer.WriteEndObject();
        }

        if (rule.HelpLinkUri is { Length: > 0 } helpLink)
        {
            writer.WriteString("helpUri", helpLink);
        }

        writer.WriteStartObject("defaultConfiguration");
        writer.WriteString("level", ToSarifLevel(rule.DefaultSeverity));
        writer.WriteEndObject();

        writer.WriteStartObject("properties");
        writer.WriteStartArray("tags");
        writer.WriteStringValue(rule.Category);
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>
    /// 指摘 1 件を書き出す。
    /// </summary>
    /// <param name="writer">書き出し先。</param>
    /// <param name="diagnostic">対象の診断。</param>
    /// <remarks>
    /// <b><c>partialFingerprints</c> を必ず付ける。</b>
    /// これが無いと、GitHub は行番号を含めた位置で同一性を判断する。
    /// 無関係な行を足しただけで同じ問題が新しいアラートとして立ち上がり、
    /// 解決済みのアラートが積み上がっていく。
    /// </remarks>
    private void WriteResult(Utf8JsonWriter writer, Diagnostic diagnostic)
    {
        LinePositionSpan lineSpan = diagnostic.Location.LineSpan;

        writer.WriteStartObject();
        writer.WriteString("ruleId", diagnostic.Id);
        writer.WriteString("level", ToSarifLevel(diagnostic.Severity));

        writer.WriteStartObject("message");
        writer.WriteString("text", diagnostic.GetMessage());
        writer.WriteEndObject();

        writer.WriteStartArray("locations");
        writer.WriteStartObject();
        writer.WriteStartObject("physicalLocation");

        writer.WriteStartObject("artifactLocation");
        writer.WriteString("uri", ToRelativeUri(diagnostic.Location.FilePath));
        writer.WriteEndObject();

        // SARIF の行・桁はいずれも 1 始まりである。内部表現は 0 始まりなので変換する。
        writer.WriteStartObject("region");
        writer.WriteNumber("startLine", lineSpan.Start.Line + 1);
        writer.WriteNumber("startColumn", lineSpan.Start.Character + 1);
        writer.WriteNumber("endLine", lineSpan.End.Line + 1);
        writer.WriteNumber("endColumn", lineSpan.End.Character + 1);
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();

        writer.WriteStartObject("partialFingerprints");
        writer.WriteString("shaderlyn/v1", DiagnosticFingerprint.ComputeSnippetHash(diagnostic));
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>
    /// ファイルパスを、リポジトリのルートからの相対 URI に直す。
    /// </summary>
    /// <param name="path">対象のパス。</param>
    /// <returns>相対 URI。</returns>
    /// <remarks>
    /// <b>絶対パスのままでは GitHub がリポジトリ内のファイルへ対応づけられない。</b>
    /// 対応づかない結果はアラートとして表示されず、
    /// 「アップロードは成功したのに何も出ない」という分かりにくい失敗になる。
    /// 区切りは <c>/</c> に統一する。SARIF の URI はそう定められている。
    /// </remarks>
    private string ToRelativeUri(string path)
    {
        try
        {
            string relative = Path.GetRelativePath(_basePath, path).Replace('\\', '/');

            // 基点の外にあるファイルは相対化しても意味が無いので、そのまま渡す。
            return relative.StartsWith("..", StringComparison.Ordinal) ? path.Replace('\\', '/') : relative;
        }
        catch (ArgumentException)
        {
            return path.Replace('\\', '/');
        }
    }

    /// <summary>重要度を SARIF の水準へ変換する。</summary>
    /// <param name="severity">対象の重要度。</param>
    /// <returns>SARIF の <c>level</c>。</returns>
    private static string ToSarifLevel(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "note",
    };
}
