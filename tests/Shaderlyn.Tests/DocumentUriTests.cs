using Shaderlyn.LanguageServer;

namespace Shaderlyn.Tests;

/// <summary>
/// LSP の URI とファイルパスの変換の検証。
/// </summary>
/// <remarks>
/// <para>
/// <b>同じファイルを指す URI の書き方は 1 つではない。</b>
/// ここを取り違えると、開いているファイルへ飛んだつもりが
/// 実在しないパスの新しいタブを開くことになる。
/// 画面には「ファイルが見つからなかったため、エディターを開くことができませんでした」とだけ出て、
/// 原因はどこにも表示されない。
/// </para>
/// <para>
/// <b>ドライブ文字の検証は Windows でだけ行う。</b>
/// <c>c:\Users\...</c> は Unix では相対パスであり、
/// <see cref="Path.GetFullPath(string)"/> が現在のフォルダーを前に付けてしまう。
/// 検証したい挙動とは無関係な理由で落ちる。
/// Unix 側の経路は <see cref="UnixOnlyFactAttribute"/> の付いたテストで確かめる。
/// </para>
/// </remarks>
public sealed class DocumentUriTests
{
    /// <summary>区切り文字の違いを吸収して比べられる形にする。</summary>
    /// <param name="path">対象のパス。</param>
    /// <returns>区切りをスラッシュに揃えたパス。</returns>
    /// <remarks>
    /// <see cref="DocumentUri.ToFilePath(string?)"/> は区切りを正規化しない。
    /// <see cref="Uri.LocalPath"/> が返したものを、先頭のスラッシュだけ落として返す。
    /// 正規化まで求めると、OS ごとに違う結果を期待することになる。
    /// </remarks>
    private static string Normalize(string path) => path.Replace('\\', '/');

    [Theory]
    // VS Code は Windows のドライブ文字のコロンを符号化して送ってくる。
    [InlineData("file:///c%3A/Users/alice/a.shader")]
    [InlineData("file:///C%3A/Users/alice/a.shader")]
    // 符号化していない書き方でも同じ場所を指す。
    [InlineData("file:///c:/Users/alice/a.shader")]
    public void どの書き方でも同じパスになる(string uri)
    {
        // Uri.LocalPath は c%3A をドライブとして認識せず、
        // 復号だけして "/c:/Users/..." を返す。先頭のスラッシュが残る。
        // これを Path.GetFullPath に渡すと "C:\c:\Users\..." になり、存在しようがない。
        string? path = DocumentUri.ToFilePath(uri);

        Assert.NotNull(path);
        Assert.Equal("c:/Users/alice/a.shader", Normalize(path), ignoreCase: true);
    }

    [Fact]
    public void 日本語を含むパスを解釈できる()
    {
        string? path = DocumentUri.ToFilePath(
            "file:///c%3A/Users/alice/Docs/%E3%83%89%E3%82%AD/a.shader");

        Assert.NotNull(path);
        Assert.Equal("c:/Users/alice/Docs/ドキ/a.shader", Normalize(path), ignoreCase: true);
    }

    [UnixOnlyFact]
    public void ドライブ文字を持たないパスも解釈できる()
    {
        // Unix の URI には先頭のスラッシュを落とす処理が働いてはいけない。
        // 落とすと絶対パスが相対パスになる。
        string? path = DocumentUri.ToFilePath("file:///home/alice/a.shader");

        Assert.Equal("/home/alice/a.shader", path);
    }

    [Fact]
    public void ファイル以外のURIは受け付けない()
    {
        Assert.Null(DocumentUri.ToFilePath("untitled:Untitled-1"));
        Assert.Null(DocumentUri.ToFilePath(null));
    }

    [WindowsOnlyFact]
    public void 相手が符号化しているなら符号化して返す()
    {
        // こちらの流儀を押し付けない。
        // 同じファイルを指しているのに文字列が違うと、
        // エディタはそれを別のスクリプトとして開こうとする。
        Assert.StartsWith(
            "file:///c%3A/",
            DocumentUri.FromFilePath(@"c:\Users\alice\a.shader", "file:///c%3A/Users/alice/b.shader"),
            StringComparison.Ordinal);
    }

    [WindowsOnlyFact]
    public void 相手が符号化していないならそのまま返す()
    {
        Assert.StartsWith(
            "file:///c:/",
            DocumentUri.FromFilePath(@"c:\Users\alice\a.shader", "file:///c:/Users/alice/b.shader"),
            StringComparison.Ordinal);
    }

    [WindowsOnlyFact]
    public void 見本が無ければ符号化しない()
    {
        Assert.StartsWith(
            "file:///c:/",
            DocumentUri.FromFilePath(@"c:\Users\alice\a.shader", likeUri: null),
            StringComparison.Ordinal);
    }

    [WindowsOnlyFact]
    public void 符号化した書き方も元のパスへ戻せる()
    {
        // 往復して元に戻らないと、飛び先の判定が壊れる。
        const string Original = @"c:\Users\alice\Docs\ドキュメント\a.shader";

        string encoded = DocumentUri.FromFilePath(Original, "file:///c%3A/x");

        Assert.Equal(Original, Path.GetFullPath(DocumentUri.ToFilePath(encoded)!), ignoreCase: true);
    }

    [UnixOnlyFact]
    public void ドライブ文字を持たないパスも往復できる()
    {
        const string Original = "/home/alice/Docs/ドキュメント/a.shader";

        string uri = DocumentUri.FromFilePath(Original, likeUri: null);

        Assert.StartsWith("file:///home/", uri, StringComparison.Ordinal);
        Assert.Equal(Original, DocumentUri.ToFilePath(uri));
    }
}
