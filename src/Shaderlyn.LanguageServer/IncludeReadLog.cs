using System.Collections.Concurrent;
using Shaderlyn.Semantics;

namespace Shaderlyn.LanguageServer;

/// <summary>
/// 解析の単位 (<see cref="AnalysisSession"/>) が取り込みで読み込んだファイルと、読み込んだときの更新日時と大きさ。
/// </summary>
/// <remarks>
/// <para>
/// <b>保存のときに取り込みのキャッシュを捨てるかを、これで決める。</b>
/// 取り込みの読み込み結果と展開結果は解析の単位が持っていて、捨てると次の解析がすべてのヘッダを読み直す
/// (URP の Lit.shader で、保存のあとの解析が 1.5 秒から 5 秒になる)。
/// 次のどれかに当たるときだけ捨てる。
/// </para>
/// <list type="bullet">
///   <item>保存したファイルを、取り込みで読み込んでいた (開いているヘッダを書き換えた)</item>
///   <item>読み込んだファイルの更新日時か大きさが変わった、または消えた (エディタの外で書き換えた)</item>
///   <item>
///     解決できなかった取り込みがあった。保存の前に、エディタの外でそのファイルを作ったかもしれない
///     (<c>--unreal-generate</c> で生成ファイルを書き出したなど)。見つからなかったことも覚えているので、捨てないと読まない
///   </item>
///   <item>読み込みの途中でファイルが書き換えられ、記録した日時が読み込んだ中身のものか分からない</item>
/// </list>
/// <para>
/// 取り込み以外で読み込むもの (エンジンの ini、Unity Editor の場所) は確認しない。設定ファイルを書き換えた場合は、
/// 解析の単位ごと作り直す (<see cref="ShaderLanguageServer"/> の SessionFor)。
/// </para>
/// </remarks>
internal sealed class IncludeReadLog
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, FileStamp?> _files = new(PathComparer);
    private readonly ConcurrentDictionary<string, byte> _unresolved = new(PathComparer);
    private volatile bool _changedWhileReading;

    /// <summary>モデルが読み込んだ取り込み先を覚える。</summary>
    /// <param name="compilation">組み立てたモデル。</param>
    /// <param name="startedUtc">モデルを組み立て始めた時刻。これより後に書き換えられたファイルは、どちらの中身を読み込んだか分からない。</param>
    public void Record(ShaderCompilation compilation, DateTime startedUtc)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        foreach (string path in compilation.AllIncludePaths)
        {
            // 最初に覚えた時点が、解決器が読み込んだ時点に一番近い。解決器は一度読み込んだファイルを読み直さない。
            if (_files.ContainsKey(path) || string.Equals(path, compilation.Text.FilePath, StringComparison.Ordinal))
            {
                continue;
            }

            FileStamp? stamp = FileStamp.Of(path);

            if (stamp is { } written && written.WrittenUtc >= startedUtc)
            {
                _changedWhileReading = true;
            }

            _files.TryAdd(path, stamp);
        }

        foreach (string path in compilation.UnresolvedIncludes)
        {
            _unresolved.TryAdd(path, 0);
        }
    }

    /// <summary>保存したファイルに合わせて、取り込みのキャッシュを捨てるべきかを返す。</summary>
    /// <param name="savedPath">保存したファイル。分からなければ <see langword="null"/>。</param>
    /// <returns>捨てるべきなら、その理由 (ログに出す)。捨てなくてよければ <see langword="null"/>。</returns>
    public string? ReasonToForget(string? savedPath)
    {
        if (_changedWhileReading)
        {
            return "読み込みの途中で取り込み先が書き換えられた";
        }

        if (!_unresolved.IsEmpty)
        {
            return "解決できなかった取り込みがある";
        }

        if (savedPath is not null && _files.ContainsKey(savedPath))
        {
            return "保存したファイルを取り込んでいる";
        }

        foreach ((string path, FileStamp? stamp) in _files)
        {
            if (FileStamp.Of(path) != stamp)
            {
                return $"取り込み先が変わった ({path})";
            }
        }

        return null;
    }

    /// <summary>ファイルの更新日時と大きさ。</summary>
    /// <param name="WrittenUtc">更新日時。</param>
    /// <param name="Length">大きさ。</param>
    private readonly record struct FileStamp(DateTime WrittenUtc, long Length)
    {
        /// <summary>今の状態を返す。存在しないか読めなければ <see langword="null"/>。</summary>
        /// <param name="path">ファイル。</param>
        /// <returns>状態。</returns>
        public static FileStamp? Of(string path)
        {
            try
            {
                FileInfo info = new(path);
                return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }
}
