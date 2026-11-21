using Shaderlyn.LanguageServer;

namespace Shaderlyn.Tests;

/// <summary>
/// ホバーのコードブロックを行内のコードに書き換える処理 (<see cref="HoverInlineCode"/>) の試験。
/// </summary>
public class HoverInlineCodeTests
{
    [Fact]
    public void 一行のコードブロックを行内のコードにする()
    {
        string converted = HoverInlineCode.Convert("```hlsl\nfloat _Amount\n```\n\nuniform 変数");

        Assert.Equal("`float _Amount`\n\nuniform 変数", converted);
    }

    [Fact]
    public void 複数行のコードブロックは一行ずつ行内のコードにして改行でつなぐ()
    {
        string converted = HoverInlineCode.Convert("```hlsl\nstruct V\n{\n    float4 pos;\n}\n```");

        Assert.Equal("`struct V`  \n`{`  \n`    float4 pos;`  \n`}`", converted);
    }

    [Fact]
    public void バッククォートを含む行はより長い並びで囲む()
    {
        string converted = HoverInlineCode.Convert("```shaderlab\nName \"a`b\"\n```");

        Assert.Equal("`` Name \"a`b\" ``", converted);
    }

    [Fact]
    public void コードブロックが無ければ元のまま返す()
    {
        const string markdown = "宣言が見つからない。`x` は使われていない。";

        Assert.Same(markdown, HoverInlineCode.Convert(markdown));
    }
}
