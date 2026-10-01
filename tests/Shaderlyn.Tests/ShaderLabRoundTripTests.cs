using System.Collections.Immutable;
using Shaderlyn.Core.Text;
using Shaderlyn.ShaderLab;
using Shaderlyn.ShaderLab.Parsing;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Tests;

/// <summary>
/// fixture ファイルへのアクセスを提供する。
/// </summary>
internal static class Fixtures
{
    /// <summary>fixture が置かれたフォルダー。</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "fixtures", "shaderlab");

    /// <summary>すべての ShaderLab fixture のパスを返す。</summary>
    public static IEnumerable<string> AllShaderLabFiles()
        => System.IO.Directory.EnumerateFiles(Directory, "*.shader").OrderBy(p => p, StringComparer.Ordinal);

    /// <summary>xUnit の Theory へ渡すための fixture 名の一覧。</summary>
    public static TheoryData<string> ShaderLabFileNames()
    {
        TheoryData<string> data = [];
        foreach (string path in AllShaderLabFiles())
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    /// <summary>fixture を読み込む。</summary>
    public static SourceText Load(string fileName)
    {
        string path = Path.Combine(Directory, fileName);
        return SourceText.From(File.ReadAllText(path), path);
    }
}

public sealed class ShaderLabRoundTripTests
{
    /// <summary>
    /// 構文木から元のテキストを 1 文字も欠けずに復元できることを検証する。
    /// </summary>
    /// <remarks>
    /// これは M1 における最も重要な不変条件である。
    /// 構文ノードの <c>ChildNodesAndTokens</c> でトークンを 1 つでも列挙し忘れると、
    /// このテストが失敗する。将来のフォーマッタや自動修正はこの性質の上に成り立つ。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Fixtures.ShaderLabFileNames), MemberType = typeof(Fixtures))]
    public void 構文木から元のテキストを完全に復元できる(string fileName)
    {
        SourceText text = Fixtures.Load(fileName);
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        Assert.Equal(text.Content, tree.ToFullString());
    }

    /// <summary>
    /// 字句解析が生成したトークンが、ファイル全体を隙間なく覆っていることを検証する。
    /// </summary>
    /// <remarks>
    /// ラウンドトリップが成立しても、それは「構文木が字句解析の結果を落としていない」ことしか示さない。
    /// 字句解析の段階で文字を取りこぼしていないことは、こちらで別に確認する必要がある。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Fixtures.ShaderLabFileNames), MemberType = typeof(Fixtures))]
    public void トークン列がファイル全体を隙間なく覆う(string fileName)
    {
        SourceText text = Fixtures.Load(fileName);
        ShaderLabLexer lexer = new(text);
        ImmutableArray<SyntaxToken> tokens = lexer.Lex(out _);

        int expectedStart = 0;
        foreach (SyntaxToken token in tokens)
        {
            Assert.Equal(expectedStart, token.FullSpan.Start);
            expectedStart = token.FullSpan.End;
        }

        Assert.Equal(text.Length, expectedStart);
        Assert.Equal(SyntaxKind.EndOfFileToken, tokens[^1].Kind);
    }

    [Theory]
    [MemberData(nameof(Fixtures.ShaderLabFileNames), MemberType = typeof(Fixtures))]
    public void 解析は例外を投げない(string fileName)
    {
        // 壊れた入力を与えられるのがリンタの日常であり、
        // 例外で止まることは決してあってはならない。
        SourceText text = Fixtures.Load(fileName);
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        Assert.NotNull(tree.Root);
    }

    [Theory]
    [MemberData(nameof(Fixtures.ShaderLabFileNames), MemberType = typeof(Fixtures))]
    public void すべてのノードに親が親の設定されている(string fileName)
    {
        SourceText text = Fixtures.Load(fileName);
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        foreach (Core.Syntax.SyntaxNode node in tree.Root.DescendantNodesAndSelf())
        {
            if (ReferenceEquals(node, tree.Root))
            {
                continue;
            }

            Assert.NotNull(node.Parent);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    [InlineData("// コメントだけ")]
    [InlineData("/* 閉じられていない")]
    [InlineData("Shader")]
    [InlineData("Shader \"名前\"")]
    [InlineData("Shader \"名前\" {")]
    [InlineData("Shader \"閉じていない文字列 {}")]
    [InlineData("{}{}{}")]
    [InlineData("@@@@")]
    [InlineData("CGPROGRAM")]
    [InlineData("Shader \"X\" { SubShader { Pass { CGPROGRAM")]
    public void 極端に壊れた入力でも例外を投げずラウンドトリップが成立する(string content)
    {
        SourceText text = SourceText.From(content, "broken.shader");
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        Assert.Equal(content, tree.ToFullString());
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    public void 改行コードが混在していてもラウンドトリップが成立する(string suffix)
    {
        string content = $"Shader \"X\"{suffix}{{{suffix}}}";
        SourceText text = SourceText.From(content, "crlf.shader");
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);

        Assert.Equal(content, tree.ToFullString());
    }
}
