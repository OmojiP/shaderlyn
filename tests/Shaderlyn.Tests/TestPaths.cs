using System.Runtime.CompilerServices;

namespace Shaderlyn.Tests;

/// <summary>試験に環境変数で渡す、コーパスや道具の場所を読む。</summary>
internal static class TestPaths
{
    /// <summary>
    /// 環境変数の値を、場所として読む。相対パスはリポジトリの最上位から見る。
    /// </summary>
    /// <param name="name">環境変数の名前。</param>
    /// <returns>場所。環境変数が無いか空なら <see langword="null"/>。</returns>
    /// <remarks>
    /// <c>dotnet test</c> の試験は、ビルドの出力のフォルダーで動く。そこから見ると、CONTRIBUTING.md に書いた
    /// <c>SHADERLYN_HLSL_CORPUS=.work/corpus</c> のような相対パスは見つからず、試験が何もせずに通ってしまう。
    /// </remarks>
    public static string? FromEnvironment(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? Path.GetFullPath(value, RepositoryRoot())
            : null;

    /// <summary>リポジトリの最上位のフォルダー。</summary>
    /// <param name="thisFile">このファイルの場所 (コンパイラが入れる)。</param>
    /// <returns>最上位のフォルダー。</returns>
    public static string RepositoryRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
