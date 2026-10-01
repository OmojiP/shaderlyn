using System.Collections.Immutable;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// <c>#include</c> が読んでよい場所を決める。
/// </summary>
/// <remarks>
/// <para>
/// <b>シェーダーは信頼できる入力とは限らない。</b>
/// <c>#include</c> に書かれたパスをそのまま読むと、
/// <c>#include "C:/Users/.../.ssh/id_rsa"</c> の 1 行で、
/// 解析を走らせた利用者が読めるファイルは何でも読めてしまう。
/// 読んだ中身は解析に使われ、<c>--inspect</c> の出力にも現れる。
/// </para>
/// <para>
/// <b>判定は「取り込み元のフォルダー」ではなく「許可された根」で行う。</b>
/// <c>Assets/Shaders/A.shader</c> から <c>#include "../Common.hlsl"</c> と書くのは正当で、
/// これは取り込み元のフォルダーの外に出る。根で見れば、これは通り、
/// プロジェクトの外へ出るものだけが止まる。
/// </para>
/// <para>
/// 根を 1 つも与えなければ制限しない。
/// ライブラリとして組み込んでいる側の挙動を変えないためである。
/// </para>
/// </remarks>
internal sealed class IncludeBoundary
{
    /// <summary>制限しない境界。</summary>
    public static IncludeBoundary Unrestricted { get; } = new([]);

    private readonly ImmutableArray<string> _roots;

    /// <summary>
    /// 境界を生成する。
    /// </summary>
    /// <param name="allowedRoots">読み込みを許すフォルダー。空なら制限しない。</param>
    public IncludeBoundary(IEnumerable<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);

        _roots =
        [
            .. allowedRoots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(NormalizeRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>制限を掛けているかどうか。</summary>
    public bool IsRestricted => !_roots.IsEmpty;

    /// <summary>
    /// そのパスを読んでよいかを判定する。
    /// </summary>
    /// <param name="fullPath">解決した絶対パス。</param>
    /// <returns>読んでよければ <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>別のホストは常に断る。</b>
    /// <c>\\host\share\...</c> は探索パスの外にある別の計算機であり、
    /// 読みにいくこと自体が通信になる。根を与えていない場合でも断る。
    /// </remarks>
    public bool Allows(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);

        if (IsRemote(fullPath))
        {
            return false;
        }

        if (_roots.IsEmpty)
        {
            return true;
        }

        string normalized = NormalizeRoot(fullPath);

        foreach (string root in _roots)
        {
            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 区切りまで見る。見ないと C:/Proj が C:/Project を通してしまう。
            if (normalized.Length > root.Length
                && normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && normalized[root.Length] == Path.DirectorySeparatorChar)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>そのパスが別のホストを指しているかを判定する。</summary>
    /// <param name="path">判定するパス。</param>
    /// <returns>別のホストを指していれば <see langword="true"/>。</returns>
    private static bool IsRemote(string path)
    {
        string normalized = path.Replace('/', '\\');

        return normalized.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>比較できる形にそろえる。</summary>
    /// <param name="path">そろえるパス。</param>
    /// <returns>絶対パスにし、末尾の区切りを外したもの。</returns>
    private static string NormalizeRoot(string path)
    {
        string full = Path.GetFullPath(path);

        return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }
}
