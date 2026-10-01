using Shaderlyn.Core.Text;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// エディタで開かれているファイル 1 つ分。
/// </summary>
/// <remarks>
/// <b>保存前の中身を持つのがこの型の役目である。</b>
/// ディスクの中身は編集中の状態と一致しない。
/// 解析はここが持つテキストに対して行う。
/// </remarks>
internal sealed class OpenDocument
{
    /// <summary>
    /// 開いているファイルを表す。
    /// </summary>
    /// <param name="uri">LSP の URI。</param>
    /// <param name="filePath">ファイルパス。</param>
    /// <param name="version">エディタが付けたバージョン。</param>
    /// <param name="text">現在の中身。</param>
    public OpenDocument(string uri, string filePath, int version, SourceText text)
    {
        Uri = uri;
        FilePath = filePath;
        Version = version;
        Text = text;
    }

    /// <summary>LSP の URI。</summary>
    public string Uri { get; }

    /// <summary>ファイルパス。</summary>
    public string FilePath { get; }

    /// <summary>エディタが付けたバージョン。</summary>
    public int Version { get; }

    /// <summary>現在の中身。</summary>
    public SourceText Text { get; }

    /// <summary>
    /// 中身を差し替えた複製を返す。
    /// </summary>
    /// <param name="version">新しいバージョン。</param>
    /// <param name="content">新しい中身。</param>
    /// <returns>差し替えたスクリプト。</returns>
    public OpenDocument WithText(int version, string content)
        => new(Uri, FilePath, version, SourceText.From(content, FilePath));
}
