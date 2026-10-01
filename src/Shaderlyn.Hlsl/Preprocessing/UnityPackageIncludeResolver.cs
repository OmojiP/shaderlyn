using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// Unity のパッケージ参照形式の <c>#include</c> を解決する。
/// </summary>
/// <remarks>
/// <para>
/// Unity のシェーダーは他パッケージのヘッダを
/// <c>#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"</c>
/// という形で参照する。この <c>Packages/</c> は実在するフォルダーではなく、
/// Unity がパッケージの実体へ読み替える仮想的な接頭辞である。
/// </para>
/// <para>
/// <b>この解決器が無いと URP のシェーダーはほぼ解析できない。</b>
/// パッケージのヘッダが読めなければ <c>TEXTURE2D</c> をはじめとする
/// マクロがすべて未定義のままになり、構文エラーの山になる。
/// </para>
/// <para>
/// パッケージの実体は次の場所にある。
/// </para>
/// <list type="bullet">
///   <item><description>プロジェクトの <c>Library/PackageCache</c> (バージョン付きの名前)</description></item>
///   <item><description>プロジェクトの <c>Packages</c> (埋め込みパッケージ)</description></item>
///   <item><description>Editor の <c>Resources/PackageManager/BuiltInPackages</c> (同梱パッケージ)</description></item>
/// </list>
/// </remarks>
internal sealed class UnityPackageIncludeResolver : IIncludeResolver
{
    /// <summary>Unity のパッケージ参照を表す接頭辞。</summary>
    private const string PackagePrefix = "Packages/";

    private readonly ImmutableArray<string> _packageRoots;

    // 解析は複数のファイルを並列に処理するため、1 つの解決器が同時に呼ばれる。
    // 通常の辞書では書き込みの競合で内部構造が壊れる。
    private readonly ConcurrentDictionary<string, string?> _packageDirectoryCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, SourceText?> _fileCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IncludeBoundary _boundary;

    /// <summary>
    /// 解決器を生成する。
    /// </summary>
    /// <param name="packageRoots">パッケージが置かれているフォルダー。指定された順に探す。</param>
    /// <param name="boundary">
    /// 読み込みを許す範囲。省略すると、パッケージのフォルダーの中だけを許す。
    /// </param>
    public UnityPackageIncludeResolver(IEnumerable<string> packageRoots, IncludeBoundary? boundary = null)
    {
        ArgumentNullException.ThrowIfNull(packageRoots);
        _packageRoots = [.. packageRoots.Where(Directory.Exists)];

        // パッケージの取り込みは Packages/<名前>/... の形に限られる。
        // 相対で外へ出る理由が無いので、既定でパッケージのフォルダーを境界にする。
        _boundary = boundary ?? new IncludeBoundary(_packageRoots);
    }

    /// <summary>
    /// Unity プロジェクトのフォルダーから、標準的なパッケージの置き場所を推定して解決器を作る。
    /// </summary>
    /// <param name="projectDirectory">Unity プロジェクトのルート。</param>
    /// <param name="unityEditorDataDirectory">Unity Editor のデータフォルダー。無い場合は <see langword="null"/>。</param>
    /// <returns>生成した解決器。</returns>
    public static UnityPackageIncludeResolver ForProject(
        string projectDirectory,
        string? unityEditorDataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(projectDirectory);

        List<string> roots =
        [
            Path.Combine(projectDirectory, "Library", "PackageCache"),
            Path.Combine(projectDirectory, "Packages"),
        ];

        if (unityEditorDataDirectory is not null)
        {
            roots.Add(Path.Combine(unityEditorDataDirectory, "Resources", "PackageManager", "BuiltInPackages"));
        }

        return new UnityPackageIncludeResolver(roots);
    }

    /// <summary>パッケージを探すフォルダー。存在するものだけを、探す順に並べる。</summary>
    public ImmutableArray<string> PackageRoots => _packageRoots;

    /// <summary>
    /// パッケージ名から実体のフォルダーを探す。
    /// </summary>
    /// <param name="packageName"><c>com.unity.render-pipelines.universal</c> のようなパッケージ名。</param>
    /// <returns>見つかったフォルダー。無ければ <see langword="null"/>。</returns>
    /// <remarks><c>#include</c> を解決するときと同じ順序で探す。</remarks>
    public string? FindPackage(string packageName)
    {
        ArgumentNullException.ThrowIfNull(packageName);
        return FindPackageDirectory(packageName);
    }

    /// <inheritdoc/>
    public bool TryResolve(string path, string includingFilePath, [NotNullWhen(true)] out SourceText? resolved)
    {
        ArgumentNullException.ThrowIfNull(path);
        resolved = null;

        string normalized = path.Replace('\\', '/');

        if (!normalized.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string remainder = normalized[PackagePrefix.Length..];
        int separatorIndex = remainder.IndexOf('/', StringComparison.Ordinal);

        if (separatorIndex <= 0)
        {
            return false;
        }

        string packageName = remainder[..separatorIndex];
        string relativePath = remainder[(separatorIndex + 1)..];

        string? packageDirectory = FindPackageDirectory(packageName);
        if (packageDirectory is null)
        {
            return false;
        }

        string fullPath = Path.GetFullPath(Path.Combine(packageDirectory, relativePath));

        // 許された範囲の外は、そこにあっても読まない (IncludeBoundary)。
        if (!_boundary.Allows(fullPath))
        {
            return false;
        }

        resolved = Load(fullPath);
        return resolved is not null;
    }

    /// <summary>
    /// パッケージ名から実体のフォルダーを探す。
    /// </summary>
    /// <param name="packageName">パッケージ名。</param>
    /// <returns>見つかったフォルダー。無ければ <see langword="null"/>。</returns>
    /// <remarks>
    /// <c>Library/PackageCache</c> のフォルダー名は
    /// <c>com.unity.render-pipelines.universal@14.0.8</c> のようにバージョンを含む。
    /// バージョンは include のパスには現れないため、
    /// <c>@</c> より前の部分で照合する必要がある。
    /// </remarks>
    private string? FindPackageDirectory(string packageName)
    {
        if (_packageDirectoryCache.TryGetValue(packageName, out string? cached))
        {
            return cached;
        }

        string? found = null;

        foreach (string root in _packageRoots)
        {
            string direct = Path.Combine(root, packageName);
            if (Directory.Exists(direct))
            {
                found = direct;
                break;
            }

            found = FindVersionedPackageDirectory(root, packageName);
            if (found is not null)
            {
                break;
            }
        }

        _packageDirectoryCache[packageName] = found;
        return found;
    }

    /// <summary>バージョン付きの名前を持つパッケージフォルダーを探す。</summary>
    /// <param name="root">探索するフォルダー。</param>
    /// <param name="packageName">パッケージ名。</param>
    /// <returns>見つかったフォルダー。無ければ <see langword="null"/>。</returns>
    private static string? FindVersionedPackageDirectory(string root, string packageName)
    {
        try
        {
            foreach (string candidate in Directory.EnumerateDirectories(root, $"{packageName}@*"))
            {
                return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>ファイルを読み込む。失敗した結果も含めてキャッシュする。</summary>
    /// <param name="fullPath">読み込む絶対パス。</param>
    /// <returns>読み込めたソーステキスト。読み込めない場合は <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>1 つのパスからは 1 つのインスタンスしか作らない。</b>
    /// 字句解析のキャッシュ (<see cref="HlslTokenCache"/>) はソーステキストの
    /// <b>参照</b>をキーにしているため、
    /// 同じファイルから別々のインスタンスが生まれると、
    /// 同じヘッダを何度も字句解析し直すことになる。
    /// </remarks>
    private SourceText? Load(string fullPath)
        => _fileCache.GetOrAdd(fullPath, static path =>
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
