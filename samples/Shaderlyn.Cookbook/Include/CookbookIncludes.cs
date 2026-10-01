using System.Collections.Immutable;

namespace Shaderlyn.Cookbook;

/// <summary>取り込みの照合に使う共通の判断。</summary>
internal static class CookbookIncludes
{
    /// <summary>書かれたパスが一覧のどれかに当たるかを判定する。</summary>
    /// <param name="names">パス、またはファイル名の一覧。</param>
    /// <param name="path">書かれていたパス。</param>
    /// <returns>当たれば <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>ファイル名だけでも照合する。</b>
    /// <c>UnityCG.cginc</c> を禁じたい人に、
    /// 書かれうるパスをすべて数え上げさせるのは現実的でない。
    /// </remarks>
    public static bool Matches(ImmutableHashSet<string> names, string path)
        => names.Contains(path) || names.Contains(FileName(path));

    /// <summary>パスの末尾のファイル名を返す。</summary>
    /// <param name="path">対象のパス。</param>
    /// <returns>ファイル名。</returns>
    private static string FileName(string path)
    {
        int separator = path.LastIndexOfAny(['/', '\\']);
        return separator < 0 ? path : path[(separator + 1)..];
    }
}
