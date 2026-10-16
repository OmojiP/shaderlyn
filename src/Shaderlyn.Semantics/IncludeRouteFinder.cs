using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Semantics;

/// <summary>
/// ヘッダの中の指摘に、解析したファイルからそのヘッダを取り込んだ経路 (<see cref="IncludeRoute"/>) を添える。
/// </summary>
/// <remarks>
/// <para>
/// 経路は、コードブロックごとの展開の結果に残った <c>#include</c> (<see cref="PreprocessResult.Includes"/>) をたどって求める。
/// 書いた位置と、解決したファイルが分かるので、解析したファイルから指摘のあるヘッダまでの最短の並びを探す。
/// </para>
/// <para>
/// コードブロック (Pass) ごとに 1 本とし、同じ並びになるものは 1 つにまとめる。
/// どのコードブロックでもヘッダに届かなければ (ルールが別の経路で位置を作ったなど)、経路は付けない。
/// </para>
/// </remarks>
public static class IncludeRouteFinder
{
    /// <summary>
    /// 解析したファイルの外にある指摘に、経路を添える。
    /// </summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="diagnostics">指摘。</param>
    /// <returns>経路を添えた指摘。解析したファイルの中の指摘はそのまま。</returns>
    public static ImmutableArray<Diagnostic> Attach(ShaderCompilation compilation, ImmutableArray<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        string root = compilation.Text.FilePath;

        if (diagnostics.IsDefaultOrEmpty
            || diagnostics.All(d => d.Location.FilePath.Length == 0 || SamePath(d.Location.FilePath, root)))
        {
            return diagnostics;
        }

        Dictionary<string, ImmutableArray<IncludeRoute>> byHeader = new(StringComparer.OrdinalIgnoreCase);
        ImmutableArray<Diagnostic>.Builder result = ImmutableArray.CreateBuilder<Diagnostic>(diagnostics.Length);

        foreach (Diagnostic diagnostic in diagnostics)
        {
            string header = diagnostic.Location.FilePath;

            if (header.Length == 0 || SamePath(header, root))
            {
                result.Add(diagnostic);
                continue;
            }

            if (!byHeader.TryGetValue(header, out ImmutableArray<IncludeRoute> routes))
            {
                routes = Find(compilation, header);
                byHeader[header] = routes;
            }

            result.Add(routes.IsEmpty ? diagnostic : diagnostic.WithIncludeRoutes(routes));
        }

        return result.MoveToImmutable();
    }

    /// <summary>解析したファイルから、ヘッダまでの経路をコードブロックごとに求める。</summary>
    /// <param name="compilation">対象シェーダーのセマンティックモデル。</param>
    /// <param name="header">指摘のあるヘッダ。</param>
    /// <returns>経路。同じ並びは 1 つにまとめる。</returns>
    public static ImmutableArray<IncludeRoute> Find(ShaderCompilation compilation, string header)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(header);

        string root = compilation.Text.FilePath;
        List<IncludeRoute> routes = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            if (FindPath(program.Tree.PreprocessResult.Includes, root, header) is not { } path)
            {
                continue;
            }

            IncludeRoute route = new(root, program.Unit.DisplayName, [.. path.Select(i => i.Location)]);

            if (seen.Add(route.Key))
            {
                routes.Add(route);
            }
        }

        return [.. routes];
    }

    /// <summary>取り込みの記録から、<paramref name="root"/> から <paramref name="target"/> までの最短の並びを探す。</summary>
    private static List<IncludeReference>? FindPath(ImmutableArray<IncludeReference> includes, string root, string target)
    {
        Dictionary<string, List<IncludeReference>> outgoing = new(StringComparer.OrdinalIgnoreCase);

        foreach (IncludeReference include in includes)
        {
            if (include.ResolvedFilePath is null)
            {
                continue;
            }

            if (!outgoing.TryGetValue(Normalize(include.Location.FilePath), out List<IncludeReference>? list))
            {
                list = [];
                outgoing[Normalize(include.Location.FilePath)] = list;
            }

            list.Add(include);
        }

        Dictionary<string, IncludeReference> cameFrom = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { Normalize(root) };
        Queue<string> queue = new([Normalize(root)]);

        while (queue.TryDequeue(out string? current))
        {
            if (!outgoing.TryGetValue(current, out List<IncludeReference>? edges))
            {
                continue;
            }

            foreach (IncludeReference edge in edges)
            {
                string next = Normalize(edge.ResolvedFilePath!);

                if (!visited.Add(next))
                {
                    continue;
                }

                cameFrom[next] = edge;

                if (SamePath(next, target))
                {
                    List<IncludeReference> path = [];

                    for (string step = next; cameFrom.TryGetValue(step, out IncludeReference via); step = Normalize(via.Location.FilePath))
                    {
                        path.Add(via);

                        if (SamePath(via.Location.FilePath, root))
                        {
                            break;
                        }
                    }

                    path.Reverse();
                    return path;
                }

                queue.Enqueue(next);
            }
        }

        return null;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static bool SamePath(string left, string right)
        => string.Equals(
            Normalize(left),
            Normalize(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
