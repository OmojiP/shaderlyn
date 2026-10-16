using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Unreal;

/// <summary>
/// Unreal の仮想パス (<c>/Engine/Private/Common.ush</c>) をディスクのファイルに解決する。
/// </summary>
/// <remarks>
/// <para>
/// <b>Unreal のシェーダーは、ディスクの場所ではなく仮想パスで取り込む。</b>
/// エンジンは <c>/Engine</c> を <c>Engine/Shaders</c> に、プラグインは <c>/Plugin/名前</c> をプラグインの <c>Shaders</c> に割り当てる
/// (C++ の <c>AddShaderSourceDirectoryMapping</c>)。割り当ては設定で与える (<see cref="UnrealOptions"/>)。
/// </para>
/// <para>
/// 割り当てが重なるときは、長いほうを使う (<c>/Plugin/A/Sub</c> と <c>/Plugin/A</c> なら前者)。
/// 仮想パスでない書き方 (相対パス) は解決しない。ほかの解決器に任せる。
/// </para>
/// </remarks>
public sealed class UnrealVirtualPathResolver : IIncludeResolver
{
    private readonly ImmutableArray<(string VirtualPath, FileSystemIncludeResolver Resolver)> _mappings;

    /// <summary>割り当てから作る。</summary>
    /// <param name="mappings">仮想パス (<c>/Engine</c>) から、ディスクのフォルダーへの割り当て。</param>
    public UnrealVirtualPathResolver(IReadOnlyDictionary<string, string> mappings)
        : this(mappings, null)
    {
    }

    /// <summary>割り当てと、読み込みを許す範囲から作る。</summary>
    /// <param name="mappings">仮想パス (<c>/Engine</c>) から、ディスクのフォルダーへの割り当て。</param>
    /// <param name="boundary">読み込みを許す範囲。<see langword="null"/> なら制限しない。</param>
    internal UnrealVirtualPathResolver(IReadOnlyDictionary<string, string> mappings, IncludeBoundary? boundary)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        _mappings =
        [
            .. mappings
                .Select(pair => (VirtualPath: Normalize(pair.Key), Directory: pair.Value))
                .OrderByDescending(pair => pair.VirtualPath.Length)
                .Select(pair => (pair.VirtualPath, new FileSystemIncludeResolver([pair.Directory], boundary))),
        ];
    }

    /// <inheritdoc/>
    public bool TryResolve(string path, string includingFilePath, [NotNullWhen(true)] out SourceText? resolved)
    {
        ArgumentNullException.ThrowIfNull(path);

        string normalized = path.Replace('\\', '/');

        foreach ((string virtualPath, FileSystemIncludeResolver resolver) in _mappings)
        {
            if (normalized.StartsWith(virtualPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                // 割り当てたフォルダーからの相対で引く。書いているファイルからの相対は見ない (空のパスを渡す)。
                return resolver.TryResolve(normalized[(virtualPath.Length + 1)..], string.Empty, out resolved);
            }
        }

        resolved = null;
        return false;
    }

    /// <summary>仮想パスの書き方を揃える (<c>/</c> で始め、末尾の <c>/</c> を外す)。</summary>
    /// <param name="virtualPath">仮想パス。</param>
    /// <returns>揃えた仮想パス。</returns>
    private static string Normalize(string virtualPath)
    {
        string normalized = virtualPath.Replace('\\', '/').TrimEnd('/');
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }
}
