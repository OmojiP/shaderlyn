using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Unity.Rules;

namespace Shaderlyn.Tests;

/// <summary>
/// URP0001 の修正 (外の宣言をマテリアルの定数バッファの中へ移す) を作る条件の試験 (<see cref="MaterialBufferFix"/>)。
/// </summary>
public class MaterialBufferFixTests
{
    [Fact]
    public void 後ろのバッファへ移す()
    {
        const string text = "HLSLPROGRAM\nhalf _A;\nCBUFFER_START(UnityPerMaterial)\n    half _B;\nCBUFFER_END\nENDHLSL\n";

        Assert.Equal("HLSLPROGRAM\nCBUFFER_START(UnityPerMaterial)\n    half _B;\n    half _A;\nCBUFFER_END\nENDHLSL\n", Apply(text, "_A"));
    }

    [Fact]
    public void 前のバッファへ移す()
    {
        const string text = "HLSLPROGRAM\nCBUFFER_START(UnityPerMaterial)\n\thalf _B;\nCBUFFER_END\nhalf _A;\nhalf4 f() { return _A; }\nENDHLSL\n";

        Assert.Equal("HLSLPROGRAM\nCBUFFER_START(UnityPerMaterial)\n\thalf _B;\n\thalf _A;\nCBUFFER_END\nhalf4 f() { return _A; }\nENDHLSL\n", Apply(text, "_A"));
    }

    [Fact]
    public void 共通コード片のバッファへは別のPassからも移す()
    {
        const string text = "HLSLINCLUDE\nCBUFFER_START(UnityPerMaterial)\n    half _B;\nCBUFFER_END\nENDHLSL\nPass { HLSLPROGRAM\nhalf _A;\nENDHLSL }\n";

        Assert.NotNull(MaterialBufferFix.Create(text, "_A", "UnityPerMaterial"));
    }

    [Theory]
    [InlineData("HLSLPROGRAM\nCBUFFER_START(UnityPerMaterial)\nCBUFFER_END\nENDHLSL\nHLSLPROGRAM\nhalf _A;\nENDHLSL\n")] // 別の Pass のブロック
    [InlineData("HLSLPROGRAM\nhalf _A;\nCBUFFER_START(UnityPerMaterial)\nCBUFFER_END\nCBUFFER_START(UnityPerMaterial)\nCBUFFER_END\nENDHLSL\n")] // バッファが 2 つ
    [InlineData("HLSLPROGRAM\n#ifdef X\nhalf _A;\n#endif\nCBUFFER_START(UnityPerMaterial)\nCBUFFER_END\nENDHLSL\n")] // 条件の中の宣言
    [InlineData("HLSLPROGRAM\nhalf _A;\nhalf g() { return _A; }\nCBUFFER_START(UnityPerMaterial)\nCBUFFER_END\nENDHLSL\n")] // 移す先より前で使っている
    [InlineData("HLSLPROGRAM\nhalf _A;\nhalf _A;\nCBUFFER_START(UnityPerMaterial)\nCBUFFER_END\nENDHLSL\n")] // 外の宣言が 2 つ
    [InlineData("HLSLPROGRAM\nhalf _A, _C;\nCBUFFER_START(UnityPerMaterial)\nCBUFFER_END\nENDHLSL\n")] // 1 行に複数の宣言
    [InlineData("HLSLPROGRAM\nhalf _A;\nENDHLSL\n")] // バッファが無い
    public void 移すと意味が変わりうる場合は修正を作らない(string text)
    {
        Assert.Null(MaterialBufferFix.Create(text, "_A", "UnityPerMaterial"));
    }

    private static string Apply(string text, string name)
    {
        DiagnosticFix fix = MaterialBufferFix.Create(text, name, "UnityPerMaterial") ?? throw new InvalidOperationException("修正が作られません。");

        foreach (TextReplacement replacement in fix.Replacements.OrderByDescending(r => r.Span.Start))
        {
            text = text[..replacement.Span.Start] + replacement.NewText + text[replacement.Span.End..];
        }

        return text;
    }
}
