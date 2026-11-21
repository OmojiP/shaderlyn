using Shaderlyn.LanguageServer;

namespace Shaderlyn.Tests;

/// <summary>
/// Markdown をプレーンテキストに書き換える処理 (<see cref="MarkdownPlainText"/>) の試験。
/// </summary>
public class MarkdownPlainTextTests
{
    [Fact]
    public void コードブロックは囲みを外して中身を残す()
    {
        string converted = MarkdownPlainText.Convert("```hlsl\nstruct V\n{\n    float4 pos;\n}\n```\n\n構造体");

        Assert.Equal("struct V\n{\n    float4 pos;\n}\n\n構造体", converted);
    }

    [Fact]
    public void 行内のコードと太字は記号を外す()
    {
        string converted = MarkdownPlainText.Convert("- `SHADER_DEBUG` のとき **`float4`**");

        Assert.Equal("- SHADER_DEBUG のとき float4", converted);
    }

    [Fact]
    public void コードブロックの中は書き換えない()
    {
        string converted = MarkdownPlainText.Convert("```hlsl\n#define SQ(x) (x)**2 // `x`\n```");

        Assert.Equal("#define SQ(x) (x)**2 // `x`", converted);
    }
}
