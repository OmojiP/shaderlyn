using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Cli.Reporting;

/// <summary>
/// 診断を機械処理向けの JSON として出力する。
/// </summary>
/// <remarks>
/// <para>
/// SARIF より単純な形式で、独自の集計や別のツールへの受け渡しに使う。
/// SARIF は GitHub へ渡すための形式であって、
/// 中身を取り出して使うには入れ子が深すぎる。
/// </para>
/// <para>
/// 行・桁は<b>1 始まり</b>で出力する。
/// 内部表現は 0 始まりだが、この出力は人と外部ツールが読むものであり、
/// エディタの表示に揃えるほうが取り違えが起きにくい。
/// </para>
/// </remarks>
internal sealed class JsonDiagnosticReporter
{
    /// <summary>
    /// 出力に使う JSON の書式。
    /// </summary>
    /// <remarks>
    /// <b>非 ASCII 文字をそのまま出す。</b>
    /// 既定の符号化器は日本語を <c>レ</c> のような形へ逃がすため、
    /// 人が読める出力にならない。診断メッセージは日本語なので、
    /// この既定のままでは出力を目で確認できない。
    /// <para>
    /// <c>UnsafeRelaxed</c> という名前は「HTML へ直接埋め込む場合は危険」という意味であり、
    /// レポートファイルとして書き出す用途では問題にならない。
    /// </para>
    /// </remarks>
    internal static JsonWriterOptions WriterOptions { get; } = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _basePath;

    /// <summary>
    /// レポータを生成する。
    /// </summary>
    /// <param name="basePath">ファイルパスを相対化する基点。</param>
    public JsonDiagnosticReporter(string basePath)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        _basePath = basePath;
    }

    /// <summary>
    /// 診断の一覧を JSON として整形する。
    /// </summary>
    /// <param name="diagnostics">出力する診断。</param>
    /// <param name="analyzedFileCount">解析したファイル数。</param>
    /// <returns>JSON のテキスト。</returns>
    public string Format(ImmutableArray<Diagnostic> diagnostics, int analyzedFileCount)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer, WriterOptions))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("summary");
            writer.WriteNumber("analyzedFiles", analyzedFileCount);
            writer.WriteNumber("errors", diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));
            writer.WriteNumber("warnings", diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning));
            writer.WriteNumber("infos", diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info));
            writer.WriteEndObject();

            writer.WriteStartArray("diagnostics");

            foreach (Diagnostic diagnostic in diagnostics)
            {
                WriteDiagnostic(writer, diagnostic);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + Environment.NewLine;
    }

    private void WriteDiagnostic(Utf8JsonWriter writer, Diagnostic diagnostic)
    {
        LinePositionSpan lineSpan = diagnostic.Location.LineSpan;

        writer.WriteStartObject();
        writer.WriteString("id", diagnostic.Id);
        writer.WriteString("severity", diagnostic.Severity.ToString().ToLowerInvariant());
        writer.WriteString("category", diagnostic.Descriptor.Category);
        writer.WriteString("title", diagnostic.Descriptor.Title);
        writer.WriteString("message", diagnostic.GetMessage());
        writer.WriteString("file", ToRelativePath(diagnostic.Location.FilePath));
        writer.WriteNumber("line", lineSpan.Start.Line + 1);
        writer.WriteNumber("column", lineSpan.Start.Character + 1);
        writer.WriteNumber("endLine", lineSpan.End.Line + 1);
        writer.WriteNumber("endColumn", lineSpan.End.Character + 1);
        writer.WriteString("fingerprint", DiagnosticFingerprint.ComputeSnippetHash(diagnostic));

        if (diagnostic.Descriptor.HelpLinkUri is { Length: > 0 } helpLink)
        {
            writer.WriteString("helpUri", helpLink);
        }

        writer.WriteEndObject();
    }

    private string ToRelativePath(string path)
    {
        try
        {
            string relative = Path.GetRelativePath(_basePath, path).Replace('\\', '/');
            return relative.StartsWith("..", StringComparison.Ordinal) ? path.Replace('\\', '/') : relative;
        }
        catch (ArgumentException)
        {
            return path.Replace('\\', '/');
        }
    }
}
