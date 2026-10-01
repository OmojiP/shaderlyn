using System.Collections.Immutable;

namespace Shaderlyn.Cli;

/// <summary>
/// 指定されたパスから解析対象のシェーダーファイルを列挙する。
/// </summary>
/// <remarks>
/// ファイルパスが直接指定された場合は拡張子を問わず対象とする。
/// 利用者が明示的に指定したものを勝手に除外すると、
/// 「指定したのに解析されない」という分かりにくい挙動になるためである。
/// フォルダーを指定した場合のみ拡張子と除外規則でふるいにかける。
/// </remarks>
internal static class ShaderFileDiscovery
{
    /// <summary>
    /// どの階層にあっても走査から除外するフォルダー名。
    /// </summary>
    /// <remarks>
    /// いずれもビルド生成物やバージョン管理の内部データであり、
    /// 人が書いたシェーダーが置かれることはない。
    /// </remarks>
    private static readonly ImmutableHashSet<string> AlwaysPrunedDirectories =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "obj", "bin", ".git", "node_modules");

    /// <summary>
    /// Unity プロジェクトルート直下にある場合にのみ走査から除外するフォルダー名。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Library/PackageCache</c> には URP など Unity 公式パッケージのシェーダーが大量にあり、
    /// 利用者が書いたコードではないため指摘しても直しようがない。
    /// また数万ファイル規模になるため、走査するだけで解析時間が桁違いに伸びる。
    /// これらは include の解決先としては参照するが、解析の起点にはしない。
    /// </para>
    /// <para>
    /// <b>フォルダー名だけで判定してはならない。</b>
    /// パスのどこかに <c>Temp</c> や <c>Library</c> という語が含まれるだけで除外すると、
    /// 利用者が同名のフォルダーに置いたシェーダーが、何も伝えられずにスキップされる。
    /// 「検査したつもりで何も検査していない」状態はリンタとして最悪の失敗であるため、
    /// 兄弟に <c>Assets</c> が存在するか (= Unity プロジェクトルート直下か) を確認する。
    /// </para>
    /// </remarks>
    private static readonly ImmutableHashSet<string> UnityGeneratedDirectories =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "Library", "Temp", "Logs", "obj");

    /// <summary>
    /// 解析対象のファイルを列挙する。
    /// </summary>
    /// <param name="paths">ファイルまたはフォルダーのパス。</param>
    /// <param name="extensions">フォルダーを走査する際に対象とする拡張子。</param>
    /// <param name="missingPaths">存在しなかったパス。</param>
    /// <returns>重複を除き、パス順に整列した対象ファイルの絶対パス。</returns>
    /// <remarks>
    /// 出力順を決定的にするためにパス順で整列する。
    /// ファイルシステムの列挙順は環境によって異なり、そのままでは
    /// 同じ入力に対して診断の並びが変わってしまうためである。
    /// </remarks>
    public static ImmutableArray<string> Discover(
        IEnumerable<string> paths,
        ImmutableArray<string> extensions,
        out ImmutableArray<string> missingPaths)
        => Discover(paths, extensions, out missingPaths, out _);

    /// <summary>
    /// 現在は解析しない、シェーダーに関わるファイルの拡張子。
    /// </summary>
    /// <remarks>
    /// 見つけたことだけを伝える。黙って飛ばすと、検査して問題が無かったのと区別が付かない。
    /// </remarks>
    public static ImmutableArray<string> UnsupportedExtensions { get; } = [".shadergraph", ".shadersubgraph", ".raytrace"];

    /// <summary>
    /// 解析対象のファイルを列挙し、あわせて解析しないファイルを集める。
    /// </summary>
    /// <param name="paths">ファイルまたはフォルダーのパス。</param>
    /// <param name="extensions">フォルダーを走査する際に対象とする拡張子。</param>
    /// <param name="missingPaths">存在しなかったパス。</param>
    /// <param name="unsupported">
    /// 現在は解析しないファイル (<see cref="UnsupportedExtensions"/>)。
    /// 直接指定されたものは解析の対象から外してここへ入れる。
    /// </param>
    /// <returns>重複を除き、パス順に整列した対象ファイルの絶対パス。</returns>
    public static ImmutableArray<string> Discover(
        IEnumerable<string> paths,
        ImmutableArray<string> extensions,
        out ImmutableArray<string> missingPaths,
        out ImmutableArray<string> unsupported)
    {
        ArgumentNullException.ThrowIfNull(paths);

        SortedSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> skipped = new(StringComparer.OrdinalIgnoreCase);
        ImmutableArray<string>.Builder missing = ImmutableArray.CreateBuilder<string>();

        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                (IsUnsupported(path) ? skipped : found).Add(Path.GetFullPath(path));
            }
            else if (Directory.Exists(path))
            {
                foreach (string file in EnumerateShaderFiles(path, [.. extensions, .. UnsupportedExtensions]))
                {
                    (IsUnsupported(file) ? skipped : found).Add(file);
                }
            }
            else
            {
                missing.Add(path);
            }
        }

        missingPaths = missing.ToImmutable();
        unsupported = [.. skipped];
        return [.. found];
    }

    /// <summary>現在は解析しないファイルかを判定する。</summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>解析しないなら <see langword="true"/>。</returns>
    private static bool IsUnsupported(string path)
        => UnsupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// フォルダーを再帰的に走査し、対象拡張子のファイルを列挙する。
    /// </summary>
    /// <param name="rootDirectory">走査の起点となるフォルダー。</param>
    /// <param name="extensions">対象とする拡張子。</param>
    /// <returns>見つかったファイルの絶対パス。</returns>
    /// <remarks>
    /// <para>
    /// <see cref="Directory.EnumerateFiles(string,string,EnumerationOptions)"/> の再帰指定ではなく
    /// 明示的なスタックで走査しているのは、除外対象のフォルダーを
    /// <b>入る前に</b> 枝刈りするためである。
    /// 再帰指定に任せると <c>Library/PackageCache</c> 配下の数万ファイルを
    /// すべて列挙してから捨てることになり、実プロジェクトでは待ち時間が体感できるほど伸びる。
    /// </para>
    /// <para>
    /// 再帰呼び出しではなくスタックを使うのは、シンボリックリンクなどで
    /// 深い階層ができていてもスタックオーバーフローを起こさないためである。
    /// </para>
    /// </remarks>
    private static IEnumerable<string> EnumerateShaderFiles(string rootDirectory, ImmutableArray<string> extensions)
    {
        Stack<string> pending = new();
        pending.Push(Path.GetFullPath(rootDirectory));

        while (pending.Count > 0)
        {
            string current = pending.Pop();

            foreach (string file in SafeEnumerate(() => Directory.EnumerateFiles(current)))
            {
                if (extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    yield return Path.GetFullPath(file);
                }
            }

            foreach (string subdirectory in SafeEnumerate(() => Directory.EnumerateDirectories(current)))
            {
                if (!ShouldPrune(subdirectory))
                {
                    pending.Push(subdirectory);
                }
            }
        }
    }

    /// <summary>
    /// フォルダーを走査対象から除外すべきかを判定する。
    /// </summary>
    /// <param name="directory">判定するフォルダーのパス。</param>
    /// <returns>除外すべき場合は <see langword="true"/>。</returns>
    /// <remarks>
    /// Unity が生成するフォルダーは名前だけでは判別できないため、
    /// 兄弟に <c>Assets</c> フォルダーがあるかどうかで
    /// 「Unity プロジェクトルートの直下にあるか」を確認する。
    /// これは Unity のプロジェクト構造がルート直下に
    /// <c>Assets</c> / <c>Library</c> / <c>Packages</c> / <c>ProjectSettings</c> を並べることに基づく。
    /// </remarks>
    private static bool ShouldPrune(string directory)
    {
        string name = Path.GetFileName(directory);

        if (AlwaysPrunedDirectories.Contains(name))
        {
            return true;
        }

        if (!UnityGeneratedDirectories.Contains(name))
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(directory);
        return parent is not null && Directory.Exists(Path.Combine(parent, "Assets"));
    }

    /// <summary>
    /// 列挙時の入出力例外を握り潰して空列に倒す。
    /// </summary>
    /// <param name="enumerate">列挙処理。</param>
    /// <returns>列挙結果。失敗した場合は空。</returns>
    /// <remarks>
    /// アクセス権の無いフォルダーが 1 つあるだけで解析全体が止まるのは避けたい。
    /// 列挙は遅延評価なので、例外は最初の要素を取り出す時点で発生する。
    /// そのため <c>yield return</c> と <c>try/catch</c> を同居させられず、
    /// ここで一度配列へ実体化している。
    /// </remarks>
    private static IReadOnlyList<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try
        {
            return [.. enumerate()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
