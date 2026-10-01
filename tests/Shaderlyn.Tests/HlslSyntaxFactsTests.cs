using System.Collections.Immutable;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Tests;

/// <summary>
/// リテラルの読み解きと、構文の上の事実を答える仕組みの検証。
/// </summary>
/// <remarks>
/// <b>ここが答えられないものは、ルールの側で文字列を切り刻むことになる。</b>
/// 接尾辞・16 進・8 進は、書き写されるたびに片方だけが取りこぼす。
/// </remarks>
public sealed class HlslSyntaxFactsTests
{
    /// <summary>1 つの式を字句解析して、最初のトークンを返す。</summary>
    /// <param name="text">リテラルの表記。</param>
    /// <returns>最初のトークン。</returns>
    private static HlslSyntaxToken Lex(string text)
    {
        ImmutableArray<HlslSyntaxToken> tokens = new HlslLexer(SourceText.From(text, "Assets/Test.hlsl")).Lex(out _);

        return tokens[0];
    }

    [Theory]
    [InlineData("1", "int")]
    [InlineData("0", "int")]
    [InlineData("42", "int")]
    [InlineData("0.5", "float")]
    [InlineData("1.0", "float")]
    [InlineData("1e5", "float")]
    [InlineData("1.0f", "float")]
    [InlineData("1.0F", "float")]
    [InlineData("2h", "half")]
    [InlineData("3u", "uint")]
    [InlineData("3U", "uint")]
    [InlineData("0x1F", "int")]
    [InlineData("0xFFu", "uint")]
    [InlineData("010", "int")]
    [InlineData("1.0l", "double")]

    // 文字リテラルは C の int ではなく uint である。fxc も DXC も uint と答える。
    [InlineData("'r'", "uint")]
    [InlineData("':'", "uint")]
    public void リテラルの型を答える(string text, string expected)
        => Assert.Equal(expected, HlslLiteral.GetTypeName(Lex(text)));

    [Theory]
    [InlineData("10", 10)]
    [InlineData("0x10", 16)]
    [InlineData("0xff", 255)]
    [InlineData("010", 8)]
    [InlineData("7u", 7)]
    [InlineData("7ul", 7)]
    public void 整数リテラルの値を読む(string text, long expected)
    {
        Assert.True(HlslLiteral.TryGetInt64(Lex(text), out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1.0f")]
    [InlineData("1e3")]
    public void 浮動小数は整数として読まない(string text)
        => Assert.False(HlslLiteral.TryGetInt64(Lex(text), out _));

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("1.5f", 1.5)]
    [InlineData("2", 2)]
    [InlineData("0x10", 16)]
    [InlineData("1e3", 1000)]
    public void 数として読む(string text, double expected)
    {
        Assert.True(HlslLiteral.TryGetDouble(Lex(text), out double value));
        Assert.Equal(expected, value);
    }

    /// <summary>
    /// 16 進の桁を接尾辞や指数と読み違えないことの確認。
    /// </summary>
    /// <remarks>
    /// <c>0x1e</c> の <c>e</c> を指数と読むと、値も型も求まらなくなる。
    /// <c>0xff</c> の <c>f</c> を <c>float</c> の接尾辞と読むのも同じ間違いである。
    /// </remarks>
    [Theory]
    [InlineData("0x1e", 30)]
    [InlineData("0xff", 255)]
    public void 十六進の桁を接尾辞と読み違えない(string text, long expected)
    {
        Assert.Equal(HlslLiteralKind.Hexadecimal, HlslLiteral.GetKind(Lex(text)));
        Assert.Equal("int", HlslLiteral.GetTypeName(Lex(text)));
        Assert.True(HlslLiteral.TryGetInt64(Lex(text), out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("1", HlslLiteralKind.Decimal)]
    [InlineData("0x1F", HlslLiteralKind.Hexadecimal)]
    [InlineData("017", HlslLiteralKind.Octal)]
    [InlineData("1.5", HlslLiteralKind.Floating)]
    [InlineData("1e5", HlslLiteralKind.Floating)]
    public void 書き方を答える(string text, HlslLiteralKind expected)
        => Assert.Equal(expected, HlslLiteral.GetKind(Lex(text)));

    [Fact]
    public void 数値でないトークンには答えない()
    {
        Assert.Null(HlslLiteral.GetTypeName(Lex("name")));
        Assert.Equal(HlslLiteralKind.None, HlslLiteral.GetKind(Lex("name")));
        Assert.False(HlslLiteral.TryGetInt64(Lex("name"), out _));
    }

    [Theory]
    [InlineData(HlslSyntaxKind.PercentEqualsToken, HlslSyntaxKind.PercentToken)]
    [InlineData(HlslSyntaxKind.PlusEqualsToken, HlslSyntaxKind.PlusToken)]
    [InlineData(HlslSyntaxKind.LessThanLessThanEqualsToken, HlslSyntaxKind.LessThanLessThanToken)]
    public void 複合代入が行う演算を答える(HlslSyntaxKind compound, HlslSyntaxKind expected)
    {
        Assert.True(HlslSyntaxFacts.IsCompoundAssignment(compound));
        Assert.Equal(expected, HlslSyntaxFacts.GetCompoundAssignmentOperand(compound));
    }

    /// <summary>
    /// 比較演算子を複合代入として扱わないことの確認。
    /// </summary>
    /// <remarks>末尾の等号を落とす形で自前に求めると、ここを取り違える。</remarks>
    [Theory]
    [InlineData(HlslSyntaxKind.LessThanEqualsToken)]
    [InlineData(HlslSyntaxKind.EqualsEqualsToken)]
    [InlineData(HlslSyntaxKind.ExclamationEqualsToken)]
    public void 比較演算子は複合代入ではない(HlslSyntaxKind kind)
    {
        Assert.False(HlslSyntaxFacts.IsCompoundAssignment(kind));
        Assert.Null(HlslSyntaxFacts.GetCompoundAssignmentOperand(kind));
    }

    [Fact]
    public void 単純な代入も代入演算子である()
    {
        Assert.True(HlslSyntaxFacts.IsAssignmentOperator(HlslSyntaxKind.EqualsToken));
        Assert.False(HlslSyntaxFacts.IsCompoundAssignment(HlslSyntaxKind.EqualsToken));
    }

    /// <summary>角括弧のトークンを字句解析で作る。</summary>
    /// <param name="text">角括弧の並び。</param>
    /// <returns>トークン。終端は含まない。</returns>
    private static ImmutableArray<HlslSyntaxToken> LexRank(string text)
    {
        ImmutableArray<HlslSyntaxToken> tokens =
            new HlslLexer(SourceText.From(text, "Assets/Test.hlsl")).Lex(out _);

        return [.. tokens.Where(t => t.Kind != HlslSyntaxKind.EndOfFileToken)];
    }

    [Theory]
    [InlineData("[8]", 8)]
    [InlineData("[2][3]", 6)]
    [InlineData("[0x10]", 16)]
    public void 配列の要素数を答える(string text, int expected)
    {
        Assert.True(HlslSyntaxFacts.TryGetArrayLength(LexRank(text), out int length));
        Assert.Equal(expected, length);
    }

    [Theory]
    [InlineData("[COUNT]")]
    [InlineData("[]")]
    [InlineData("[0]")]
    public void 数で書かれていない要素数は答えない(string text)
        => Assert.False(HlslSyntaxFacts.TryGetArrayLength(LexRank(text), out _));
}
