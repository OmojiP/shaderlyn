using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;

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
    private static ShaderSyntaxToken Lex(string text)
    {
        ImmutableArray<ShaderSyntaxToken> tokens = new ShaderLexer(SourceText.From(text, "Assets/Test.hlsl")).Lex(out _);

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
        => Assert.Equal(expected, ShaderLiteral.GetTypeName(Lex(text)));

    [Theory]
    [InlineData("10", 10)]
    [InlineData("0x10", 16)]
    [InlineData("0xff", 255)]
    [InlineData("010", 8)]
    [InlineData("7u", 7)]
    [InlineData("7ul", 7)]
    public void 整数リテラルの値を読む(string text, long expected)
    {
        Assert.True(ShaderLiteral.TryGetInt64(Lex(text), out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1.0f")]
    [InlineData("1e3")]
    public void 浮動小数は整数として読まない(string text)
        => Assert.False(ShaderLiteral.TryGetInt64(Lex(text), out _));

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("1.5f", 1.5)]
    [InlineData("2", 2)]
    [InlineData("0x10", 16)]
    [InlineData("1e3", 1000)]
    public void 数として読む(string text, double expected)
    {
        Assert.True(ShaderLiteral.TryGetDouble(Lex(text), out double value));
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
        Assert.Equal(ShaderLiteralKind.Hexadecimal, ShaderLiteral.GetKind(Lex(text)));
        Assert.Equal("int", ShaderLiteral.GetTypeName(Lex(text)));
        Assert.True(ShaderLiteral.TryGetInt64(Lex(text), out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("1", ShaderLiteralKind.Decimal)]
    [InlineData("0x1F", ShaderLiteralKind.Hexadecimal)]
    [InlineData("017", ShaderLiteralKind.Octal)]
    [InlineData("1.5", ShaderLiteralKind.Floating)]
    [InlineData("1e5", ShaderLiteralKind.Floating)]
    public void 書き方を答える(string text, ShaderLiteralKind expected)
        => Assert.Equal(expected, ShaderLiteral.GetKind(Lex(text)));

    [Fact]
    public void 数値でないトークンには答えない()
    {
        Assert.Null(ShaderLiteral.GetTypeName(Lex("name")));
        Assert.Equal(ShaderLiteralKind.None, ShaderLiteral.GetKind(Lex("name")));
        Assert.False(ShaderLiteral.TryGetInt64(Lex("name"), out _));
    }

    [Theory]
    [InlineData(ShaderSyntaxKind.PercentEqualsToken, ShaderSyntaxKind.PercentToken)]
    [InlineData(ShaderSyntaxKind.PlusEqualsToken, ShaderSyntaxKind.PlusToken)]
    [InlineData(ShaderSyntaxKind.LessThanLessThanEqualsToken, ShaderSyntaxKind.LessThanLessThanToken)]
    public void 複合代入が行う演算を答える(ShaderSyntaxKind compound, ShaderSyntaxKind expected)
    {
        Assert.True(ShaderSyntaxFacts.IsCompoundAssignment(compound));
        Assert.Equal(expected, ShaderSyntaxFacts.GetCompoundAssignmentOperand(compound));
    }

    /// <summary>
    /// 比較演算子を複合代入として扱わないことの確認。
    /// </summary>
    /// <remarks>末尾の等号を落とす形で自前に求めると、ここを取り違える。</remarks>
    [Theory]
    [InlineData(ShaderSyntaxKind.LessThanEqualsToken)]
    [InlineData(ShaderSyntaxKind.EqualsEqualsToken)]
    [InlineData(ShaderSyntaxKind.ExclamationEqualsToken)]
    public void 比較演算子は複合代入ではない(ShaderSyntaxKind kind)
    {
        Assert.False(ShaderSyntaxFacts.IsCompoundAssignment(kind));
        Assert.Null(ShaderSyntaxFacts.GetCompoundAssignmentOperand(kind));
    }

    [Fact]
    public void 単純な代入も代入演算子である()
    {
        Assert.True(ShaderSyntaxFacts.IsAssignmentOperator(ShaderSyntaxKind.EqualsToken));
        Assert.False(ShaderSyntaxFacts.IsCompoundAssignment(ShaderSyntaxKind.EqualsToken));
    }

    /// <summary>角括弧のトークンを字句解析で作る。</summary>
    /// <param name="text">角括弧の並び。</param>
    /// <returns>トークン。終端は含まない。</returns>
    private static ImmutableArray<ShaderSyntaxToken> LexRank(string text)
    {
        ImmutableArray<ShaderSyntaxToken> tokens =
            new ShaderLexer(SourceText.From(text, "Assets/Test.hlsl")).Lex(out _);

        return [.. tokens.Where(t => t.Kind != ShaderSyntaxKind.EndOfFileToken)];
    }

    [Theory]
    [InlineData("[8]", 8)]
    [InlineData("[2][3]", 6)]
    [InlineData("[0x10]", 16)]
    public void 配列の要素数を答える(string text, int expected)
    {
        Assert.True(ShaderSyntaxFacts.TryGetArrayLength(LexRank(text), out int length));
        Assert.Equal(expected, length);
    }

    [Theory]
    [InlineData("[COUNT]")]
    [InlineData("[]")]
    [InlineData("[0]")]
    public void 数で書かれていない要素数は答えない(string text)
        => Assert.False(ShaderSyntaxFacts.TryGetArrayLength(LexRank(text), out _));
}
