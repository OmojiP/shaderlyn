using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Godot;

/// <summary>
/// Godot のプロジェクト (<c>project.godot</c> のあるフォルダー) を探して、その設定を読む。
/// </summary>
/// <remarks>
/// <para>
/// プロジェクトのフォルダーは <c>res://</c> の根であり、<c>#include "res://..."</c> はそこから探す。
/// レンダラー (<c>rendering/renderer/rendering_method</c>) はシェーダーのコンパイルの結果を変える (<see cref="GodotRenderer"/>)。
/// </para>
/// <para>
/// 解析は複数のファイルを並列に読むので、探した結果は並行に使える辞書に覚える。
/// </para>
/// </remarks>
internal static class GodotProject
{
    /// <summary>プロジェクトのファイルの名前。</summary>
    public const string FileName = "project.godot";

    private static readonly ConcurrentDictionary<string, string?> Roots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (DateTime Written, GodotRenderer? Renderer)> Renderers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ファイルから上へ辿って、最初に見つけた <c>project.godot</c> のフォルダーを返す。</summary>
    /// <param name="filePath">ファイルかフォルダーのパス。</param>
    /// <returns>プロジェクトのフォルダー。見つからなければ <see langword="null"/>。</returns>
    public static string? FindRoot(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        string full;

        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string? directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        return directory is null ? null : FindRootFrom(directory);
    }

    /// <summary>プロジェクトのレンダラー (<c>rendering/renderer/rendering_method</c>) を読む。</summary>
    /// <param name="root">プロジェクトのフォルダー。</param>
    /// <returns>書いてあればそのレンダラー。書いていないか読めなければ <see langword="null"/> (Godot の既定は Forward+)。</returns>
    public static GodotRenderer? ReadRenderer(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        // 言語サーバーは長く動くので、project.godot を書き換えたら読み直す。
        string path = Path.Combine(root, FileName);
        DateTime written = File.GetLastWriteTimeUtc(path);

        if (Renderers.TryGetValue(path, out (DateTime Written, GodotRenderer? Renderer) cached) && cached.Written == written)
        {
            return cached.Renderer;
        }

        GodotRenderer? renderer = ReadRendererCore(path);
        Renderers[path] = (written, renderer);
        return renderer;
    }

    /// <summary><c>project.godot</c> の中身からレンダラーを読む。</summary>
    /// <param name="text"><c>project.godot</c> の中身。</param>
    /// <returns>書いてあればそのレンダラー。</returns>
    /// <remarks>
    /// Godot は設定を <c>[rendering]</c> の節に <c>renderer/rendering_method="mobile"</c> の形で書く。
    /// 既定 (Forward+) のときは書かない。<c>rendering_method.mobile</c> と <c>rendering_method.web</c> は、それぞれの書き出し先だけの設定なので読まない (エディターでは使わない)。
    /// </remarks>
    public static GodotRenderer? ParseRenderer(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string section = string.Empty;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);

            if (section != "rendering" || equals < 0 || line[..equals].Trim() != "renderer/rendering_method")
            {
                continue;
            }

            string value = line[(equals + 1)..].Trim().Trim('"');
            return GodotRenderers.TryParse(value, out GodotRenderer renderer) ? renderer : null;
        }

        return null;
    }

    private static string? FindRootFrom(string directory)
        => Roots.GetOrAdd(directory, static d =>
        {
            if (File.Exists(Path.Combine(d, FileName)))
            {
                return d;
            }

            string? parent = Path.GetDirectoryName(d);
            return parent is null ? null : FindRootFrom(parent);
        });

    private static GodotRenderer? ReadRendererCore(string path)
    {
        try
        {
            return File.Exists(path) ? ParseRenderer(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>#include "res://..."</c> を、プロジェクトのフォルダー (<see cref="GodotProject"/>) から解決する。
/// </summary>
/// <remarks>
/// <c>res://</c> で始まらないパスは、後に続く解決器 (取り込むファイルからの相対) に任せる。
/// 読んでよい範囲 (<see cref="IncludeBoundary"/>) は、後に続く解決器と同じものを使う。
/// </remarks>
/// <param name="projectRoot">設定で決めたプロジェクトのフォルダー。<see langword="null"/> なら、取り込むファイルから上へ辿って探す。</param>
/// <param name="boundary">読んでよい範囲。</param>
internal sealed class GodotResourceIncludeResolver(string? projectRoot, IncludeBoundary boundary) : IIncludeResolver
{
    /// <summary><c>res://</c> の接頭辞。</summary>
    public const string Prefix = "res://";

    private readonly ConcurrentDictionary<string, FileSystemIncludeResolver> _resolvers = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public bool TryResolve(string path, string includingFilePath, [NotNullWhen(true)] out SourceText? resolved)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!path.StartsWith(Prefix, StringComparison.Ordinal) || (projectRoot ?? GodotProject.FindRoot(includingFilePath)) is not { } root)
        {
            resolved = null;
            return false;
        }

        // プロジェクトのフォルダーだけを探すので、取り込むファイルからの相対の候補もプロジェクトのフォルダーにする。
        return _resolvers.GetOrAdd(root, r => new FileSystemIncludeResolver([r], boundary))
            .TryResolve(path[Prefix.Length..], Path.Combine(root, GodotProject.FileName), out resolved);
    }

    /// <inheritdoc/>
    public string? FindOutsideBoundary(string path, string includingFilePath)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.StartsWith(Prefix, StringComparison.Ordinal) && (projectRoot ?? GodotProject.FindRoot(includingFilePath)) is { } root
            ? _resolvers.GetOrAdd(root, r => new FileSystemIncludeResolver([r], boundary))
                .FindOutsideBoundary(path[Prefix.Length..], Path.Combine(root, GodotProject.FileName))
            : null;
    }
}
