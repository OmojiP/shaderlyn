using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;

namespace Shaderlyn.Tests;

/// <summary>
/// <c>#include</c> が読んでよい場所の検証。
/// </summary>
/// <remarks>
/// <b>シェーダーは信頼できる入力とは限らない。</b>
/// 制限が無いと、<c>#include</c> の 1 行で、解析を走らせた利用者が読めるファイルは
/// 何でも読めてしまう。
/// </remarks>
public sealed class IncludeBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shaderlyn-boundary-" + Guid.NewGuid().ToString("N"));

    /// <summary>試験用のフォルダーを作る。</summary>
    public IncludeBoundaryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "project", "Assets", "Shaders"));
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "float4 _Secret;\n");
        File.WriteAllText(Path.Combine(_root, "project", "Assets", "Common.hlsl"), "float4 _Shared;\n");
    }

    /// <summary>試験用のフォルダーを片付ける。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 消せなくても検証の結果は変わらない。
        }
    }

    [Fact]
    public void 根の外へ出る取り込みは解決しない()
    {
        string assets = Path.Combine(_root, "project", "Assets");
        FileSystemIncludeResolver resolver = new([assets], new IncludeBoundary([assets]));

        Assert.False(
            resolver.TryResolve(
                "../../secret.txt",
                Path.Combine(assets, "Shaders", "A.shader"),
                out SourceText? resolved));

        Assert.Null(resolved);
    }

    [Fact]
    public void 絶対パスで根の外を指す取り込みも解決しない()
    {
        // .. を使う必要すら無い。絶対パスで書けば済む。
        string assets = Path.Combine(_root, "project", "Assets");
        FileSystemIncludeResolver resolver = new([assets], new IncludeBoundary([assets]));

        Assert.False(
            resolver.TryResolve(
                Path.Combine(_root, "secret.txt"),
                Path.Combine(assets, "Shaders", "A.shader"),
                out _));
    }

    [Fact]
    public void 根の中なら上のフォルダーへ戻る取り込みは解決する()
    {
        // Assets/Shaders/A.shader から "../Common.hlsl" と書くのは正当である。
        // 取り込み元のフォルダーで区切ると、この普通の書き方が壊れる。
        string assets = Path.Combine(_root, "project", "Assets");
        FileSystemIncludeResolver resolver = new([assets], new IncludeBoundary([assets]));

        Assert.True(
            resolver.TryResolve(
                "../Common.hlsl",
                Path.Combine(assets, "Shaders", "A.shader"),
                out SourceText? resolved));

        Assert.Contains("_Shared", resolved!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void 根を与えなければ制限しない()
    {
        // ライブラリとして組み込んでいる側の挙動は変えない。
        string assets = Path.Combine(_root, "project", "Assets");
        FileSystemIncludeResolver resolver = new([assets]);

        Assert.True(
            resolver.TryResolve(
                "../../secret.txt",
                Path.Combine(assets, "Shaders", "A.shader"),
                out _));
    }

    [Theory]
    [InlineData(@"\\attacker.example\share\x.hlsl")]
    [InlineData("//attacker.example/share/x.hlsl")]
    public void 別のホストは根を与えていなくても断る(string path)
    {
        // 読みにいくこと自体が通信になる。
        Assert.False(IncludeBoundary.Unrestricted.Allows(path));
    }

    [Fact]
    public void 名前が前方一致するだけの別フォルダーは通さない()
    {
        // C:/Proj が C:/Project を通してはならない。
        IncludeBoundary boundary = new([Path.Combine(_root, "proj")]);

        Assert.False(boundary.Allows(Path.Combine(_root, "project", "Assets", "A.hlsl")));
        Assert.True(boundary.Allows(Path.Combine(_root, "proj", "A.hlsl")));
    }
}
