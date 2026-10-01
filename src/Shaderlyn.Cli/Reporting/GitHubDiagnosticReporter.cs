using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Cli.Reporting;

/// <summary>
/// 診断を GitHub Actions のワークフローコマンドとして出力する。
/// </summary>
/// <remarks>
/// <para>
/// <b>Code Scanning を有効にしていないリポジトリでも PR にインライン注釈が出る。</b>
/// SARIF のアップロードには Code Scanning の有効化が要り、
/// プライベートリポジトリでは有料プランが前提になることがある。
/// この形式なら、標準出力へ書くだけで注釈が付く。
/// </para>
/// <para>
/// 出力は GitHub のログにそのまま現れるため、
/// 人が読んでも意味が分かる形になっている。
/// </para>
/// </remarks>
internal sealed class GitHubDiagnosticReporter
{
    private readonly string _basePath;

    /// <summary>
    /// レポータを生成する。
    /// </summary>
    /// <param name="basePath">ファイルパスを相対化する基点。</param>
    public GitHubDiagnosticReporter(string basePath)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        _basePath = basePath;
    }

    /// <summary>
    /// 診断の一覧をワークフローコマンドとして整形する。
    /// </summary>
    /// <param name="diagnostics">出力する診断。</param>
    /// <param name="analyzedFileCount">解析したファイル数。</param>
    /// <returns>整形済みのテキスト。</returns>
    public string Format(ImmutableArray<Diagnostic> diagnostics, int analyzedFileCount)
    {
        StringBuilder builder = new();

        foreach (Diagnostic diagnostic in diagnostics)
        {
            AppendDiagnostic(builder, diagnostic);
        }

        int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        int infos = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info);

        builder.Append(CultureInfo.InvariantCulture,
            $"{analyzedFileCount} ファイルを解析: エラー {errors} 件, 警告 {warnings} 件, 情報 {infos} 件");
        builder.AppendLine();

        return builder.ToString();
    }

    private void AppendDiagnostic(StringBuilder builder, Diagnostic diagnostic)
    {
        LinePositionSpan lineSpan = diagnostic.Location.LineSpan;

        builder.Append("::");
        builder.Append(ToCommandName(diagnostic.Severity));
        builder.Append(" file=");
        builder.Append(EscapeProperty(ToRelativePath(diagnostic.Location.FilePath)));

        // ワークフローコマンドの行・桁は 1 始まりである。
        builder.Append(CultureInfo.InvariantCulture, $",line={lineSpan.Start.Line + 1}");
        builder.Append(CultureInfo.InvariantCulture, $",col={lineSpan.Start.Character + 1}");
        builder.Append(CultureInfo.InvariantCulture, $",endLine={lineSpan.End.Line + 1}");
        builder.Append(CultureInfo.InvariantCulture, $",endColumn={lineSpan.End.Character + 1}");
        builder.Append(",title=");
        builder.Append(EscapeProperty($"{diagnostic.Id}: {diagnostic.Descriptor.Title}"));
        builder.Append("::");
        builder.Append(EscapeData($"{diagnostic.Id}: {diagnostic.GetMessage()}"));
        builder.AppendLine();
    }

    /// <summary>重要度に対応するワークフローコマンド名を返す。</summary>
    /// <param name="severity">対象の重要度。</param>
    /// <returns>コマンド名。</returns>
    /// <remarks>
    /// GitHub が注釈として表示するのは <c>error</c>、<c>warning</c>、<c>notice</c> の 3 種類である。
    /// </remarks>
    private static string ToCommandName(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "notice",
    };

    /// <summary>
    /// ファイルパスをリポジトリのルートからの相対に直す。
    /// </summary>
    /// <param name="path">対象のパス。</param>
    /// <returns>相対パス。</returns>
    /// <remarks>
    /// <b>絶対パスでは注釈が差分に対応づかない。</b>
    /// GitHub はワークスペースからの相対パスでファイルを特定する。
    /// </remarks>
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

    /// <summary>
    /// ワークフローコマンドの本文をエスケープする。
    /// </summary>
    /// <param name="text">対象の文字列。</param>
    /// <returns>エスケープした文字列。</returns>
    /// <remarks>
    /// 改行をそのまま出すとコマンドがそこで切れ、
    /// 残りがログに素の文字列として現れる。
    /// </remarks>
    private static string EscapeData(string text) => text
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal);

    /// <summary>
    /// ワークフローコマンドの属性値をエスケープする。
    /// </summary>
    /// <param name="text">対象の文字列。</param>
    /// <returns>エスケープした文字列。</returns>
    /// <remarks>
    /// 本文のエスケープに加えて、区切りに使われる <c>,</c> と <c>:</c> も置き換える。
    /// ルールの表題にコロンが含まれると、そこで属性が切れて位置がずれる。
    /// </remarks>
    private static string EscapeProperty(string text) => EscapeData(text)
        .Replace(",", "%2C", StringComparison.Ordinal)
        .Replace(":", "%3A", StringComparison.Ordinal);
}
