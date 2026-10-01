using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// <c>#include</c> のパスを実際のソーステキストへ解決する。
/// </summary>
/// <remarks>
/// インターフェイスにしているのは、テストから実ファイルを用意せずに
/// 任意の include 構成を組めるようにするためである。
/// include 解決の分岐はプリプロセッサの中でも間違いが起きやすい部分であり、
/// ファイルシステムに依存したままでは十分な検証ができない。
/// </remarks>
public interface IIncludeResolver
{
    /// <summary>
    /// include のパスを解決する。
    /// </summary>
    /// <param name="path"><c>#include</c> に書かれたパス。引用符や山括弧は含まない。</param>
    /// <param name="includingFilePath">この include を書いているファイルのパス。相対解決の基点になる。</param>
    /// <param name="resolved">解決できたソーステキスト。</param>
    /// <returns>解決できた場合は <see langword="true"/>。</returns>
    bool TryResolve(string path, string includingFilePath, [NotNullWhen(true)] out SourceText? resolved);
}

/// <summary>
/// ファイルシステムから include を解決する。
/// </summary>
/// <remarks>
/// <para>
/// 探索順序は「include を書いているファイルからの相対」→「指定された探索パスの順」である。
/// Unity のシェーダーは <c>#include "Packages/com.unity.render-pipelines.universal/..."</c> のように
/// プロジェクトルートからのパスで書かれることが多いため、
/// 探索パスにプロジェクトルートを含めておく必要がある。
/// </para>
/// <para>
/// 一度読み込んだファイルは再利用する。同じヘッダが数十回 include されるのは普通であり、
/// そのたびにディスクから読み直すと解析時間が跳ね上がる。
/// </para>
/// <para>
/// <b>キャッシュは並行アクセスに耐える必要がある。</b>
/// 解析は複数のファイルを並列に処理するため、1 つの解決器が同時に呼ばれる。
/// 通常の辞書を使うと、書き込みの競合で内部構造が壊れて無限ループや例外になる。
/// </para>
/// </remarks>
internal sealed class FileSystemIncludeResolver : IIncludeResolver
{
    private readonly ImmutableArray<string> _searchPaths;
    private readonly IncludeBoundary _boundary;
    private readonly ConcurrentDictionary<string, SourceText?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 解決器を生成する。
    /// </summary>
    /// <param name="searchPaths">探索するフォルダー。指定された順に探す。</param>
    /// <param name="boundary">
    /// 読み込みを許す範囲。省略すると制限しない (<see cref="IncludeBoundary.Unrestricted"/>)。
    /// </param>
    public FileSystemIncludeResolver(IEnumerable<string> searchPaths, IncludeBoundary? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(searchPaths);
        _searchPaths = [.. searchPaths];
        _boundary = boundary ?? IncludeBoundary.Unrestricted;
    }

    /// <inheritdoc/>
    public bool TryResolve(string path, string includingFilePath, [NotNullWhen(true)] out SourceText? resolved)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (string candidate in EnumerateCandidates(path, includingFilePath))
        {
            // 許された範囲の外は、そこにあっても読まない (IncludeBoundary)。
            if (!_boundary.Allows(candidate))
            {
                continue;
            }

            resolved = Load(candidate);
            if (resolved is not null)
            {
                return true;
            }
        }

        resolved = null;
        return false;
    }

    /// <summary>候補となる絶対パスを探索順に列挙する。</summary>
    /// <param name="path">include に書かれたパス。</param>
    /// <param name="includingFilePath">include を書いているファイルのパス。</param>
    /// <returns>候補パスの列。</returns>
    private IEnumerable<string> EnumerateCandidates(string path, string includingFilePath)
    {
        string? includingDirectory = Path.GetDirectoryName(includingFilePath);
        if (!string.IsNullOrEmpty(includingDirectory))
        {
            yield return Path.GetFullPath(Path.Combine(includingDirectory, path));
        }

        foreach (string searchPath in _searchPaths)
        {
            yield return Path.GetFullPath(Path.Combine(searchPath, path));
        }
    }

    /// <summary>
    /// ファイルを読み込む。失敗した結果も含めてキャッシュする。
    /// </summary>
    /// <param name="fullPath">読み込む絶対パス。</param>
    /// <returns>読み込めたソーステキスト。読み込めない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 「見つからなかった」こともキャッシュする。
    /// 存在しない候補パスへの問い合わせは探索順序の分だけ繰り返し発生するため、
    /// 失敗を覚えておかないと無駄なファイルシステムアクセスが積み上がる。
    /// </para>
    /// <para>
    /// <b>1 つのパスからは 1 つのインスタンスしか作らない。</b>
    /// 字句解析のキャッシュ (<see cref="HlslTokenCache"/>) はソーステキストの
    /// <b>参照</b>をキーにしている。
    /// 「調べてから入れる」書き方だと、複数のスレッドが同時に来たときに
    /// 同じファイルから別々のインスタンスが生まれ、
    /// 同じヘッダを何度も字句解析し直すことになる。
    /// </para>
    /// </remarks>
    private SourceText? Load(string fullPath)
        => _cache.GetOrAdd(fullPath, static path =>
        {
            try
            {
                return File.Exists(path) ? SourceText.From(File.ReadAllText(path), path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });
}

/// <summary>
/// あらかじめ用意した内容から include を解決する。
/// </summary>
/// <remarks>
/// テストで使うほか、Unity をインストールしていない CI 環境で
/// 最小のスタブヘッダを提供する用途にも使う。
/// </remarks>
internal sealed class InMemoryIncludeResolver : IIncludeResolver
{
    private readonly Dictionary<string, SourceText> _files;

    /// <summary>
    /// 解決器を生成する。
    /// </summary>
    /// <param name="files">パスから内容への対応表。</param>
    public InMemoryIncludeResolver(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files.ToDictionary(
            pair => Normalize(pair.Key),
            pair => SourceText.From(pair.Value, pair.Key),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public bool TryResolve(string path, string includingFilePath, [NotNullWhen(true)] out SourceText? resolved)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _files.TryGetValue(Normalize(path), out resolved);
    }

    /// <summary>パス区切りの違いを吸収する。</summary>
    /// <param name="path">正規化するパス。</param>
    /// <returns>正規化されたパス。</returns>
    private static string Normalize(string path) => path.Replace('\\', '/');
}

/// <summary>
/// 複数の解決器を順に試す。
/// </summary>
/// <remarks>
/// 「まず実ファイルを探し、見つからなければ同梱スタブで代替する」という構成に使う。
/// Unity がインストールされていない CI 環境でも解析が成立するために必要な仕組みである。
/// </remarks>
internal sealed class CompositeIncludeResolver : IIncludeResolver
{
    private readonly ImmutableArray<IIncludeResolver> _resolvers;

    /// <summary>
    /// 解決器を生成する。
    /// </summary>
    /// <param name="resolvers">順に試す解決器。</param>
    public CompositeIncludeResolver(params IIncludeResolver[] resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        _resolvers = [.. resolvers];
    }

    /// <inheritdoc/>
    public bool TryResolve(string path, string includingFilePath, [NotNullWhen(true)] out SourceText? resolved)
    {
        foreach (IIncludeResolver resolver in _resolvers)
        {
            if (resolver.TryResolve(path, includingFilePath, out resolved))
            {
                return true;
            }
        }

        resolved = null;
        return false;
    }
}
