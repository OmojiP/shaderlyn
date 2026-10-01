using System.Collections.Immutable;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Parsing;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Hlsl.Syntax;

namespace Shaderlyn.Tests;

/// <summary>
/// 条件領域を並べてよいかの判定の検証。
/// </summary>
/// <remarks>
/// <b>誤って「並べてよい」と言うと構文木が壊れる。</b>
/// その先のすべての判断が狂うので、判定できないものは
/// 並べない側へ倒っていることを確かめる。
/// </remarks>
public sealed class ConditionalRegionScannerTests
{
    /// <summary>
    /// <c>#if</c> の行から始まるコードを走査する。
    /// </summary>
    /// <param name="code">走査するコード。</param>
    /// <returns>判定結果。</returns>
    private static ConditionalRegionLayout Scan(string code)
    {
        ImmutableArray<HlslSyntaxToken> tokens =
            new HlslLexer(SourceText.From(code, "test.hlsl")).Lex(out _);

        // #if の行の次から始める。
        int start = 0;

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Kind == HlslSyntaxKind.HashToken && tokens[i].IsAtLineStart)
            {
                for (int j = i + 1; j < tokens.Length; j++)
                {
                    if (tokens[j].IsAtLineStart)
                    {
                        start = j;
                        break;
                    }
                }

                break;
            }
        }

        return ConditionalRegionScanner.Scan(tokens, start);
    }

    [Fact]
    public void 文が並んでいるだけなら並べてよい()
    {
        Assert.Equal(
            ConditionalRegionLayout.Mergeable,
            Scan("""
                #ifdef A
                    return 1;
                #else
                    return 2;
                #endif
                """));
    }

    [Fact]
    public void 宣言が並んでいるだけなら並べてよい()
    {
        Assert.Equal(
            ConditionalRegionLayout.Mergeable,
            Scan("""
                #ifdef A
                    float4 x;
                    float4 y;
                #endif
                """));
    }

    [Fact]
    public void 関数の定義で終わっていれば並べてよい()
    {
        Assert.Equal(
            ConditionalRegionLayout.Mergeable,
            Scan("""
                #ifdef A
                    float4 f(float2 uv)
                    {
                        return 0;
                    }
                #endif
                """));
    }

    [Fact]
    public void 空の分岐があっても並べてよい()
    {
        Assert.Equal(
            ConditionalRegionLayout.Mergeable,
            Scan("""
                #ifdef A
                    float4 x;
                #else
                #endif
                """));
    }

    [Fact]
    public void 文の途中で切れていたら並べない()
    {
        // 並べると "x = 1 x = 2 ;" になり、構文として通らない。
        Assert.Equal(
            ConditionalRegionLayout.NotSelfContained,
            Scan("""
                #if A
                    x = 1
                #else
                    x = 2
                #endif
                """));
    }

    [Fact]
    public void 括弧が釣り合っていなければ並べない()
    {
        // 実際のシェーダーから採取した形。if の条件と本体が分断されている。
        Assert.Equal(
            ConditionalRegionLayout.NotSelfContained,
            Scan("""
                #if A
                    if (a < 1)
                #else
                    if (b < 1)
                #endif
                """));
    }

    [Fact]
    public void 仮引数の途中は並べない()
    {
        // ", out float2 extra : SV_Target1" は括弧は釣り合うが、
        // 文の切れ目で終わっていない。
        Assert.Equal(
            ConditionalRegionLayout.NotSelfContained,
            Scan("""
                #ifdef A
                    , out float2 extra : SV_Target1
                #endif
                """));
    }

    [Theory]
    [InlineData("#define B 1", ConditionalRegionLayout.DefinesMacros)]
    [InlineData("#undef B", ConditionalRegionLayout.DefinesMacros)]
    [InlineData("#include \"other.hlsl\"", ConditionalRegionLayout.SwitchesIncludes)]
    public void マクロや取り込みを切り替えている分岐を見分ける(string directive, object expected)
    {
        // 取り込みは展開結果そのものが変わる。定義は、条件でしか使われていなければ並べられる。
        Assert.Equal(
            (ConditionalRegionLayout)expected,
            Scan($"""
                #ifdef A
                {directive}
                #endif
                """));
    }

    [Fact]
    public void 入れ子も中が並べられる形なら並べる()
    {
        // 内側の 2 つの分岐を連ねたものは、括弧が釣り合い文の切れ目で終わる。
        // 外側から見れば、内側は 1 つのまとまったコードでしかない。
        Assert.Equal(
            ConditionalRegionLayout.Mergeable,
            Scan("""
                #ifdef A
                    float4 x;
                #ifdef B
                    float4 y;
                #else
                    float4 z;
                #endif
                    float4 w;
                #else
                    float4 v;
                #endif
                """));
    }

    [Fact]
    public void 入れ子の中が並べられない形なら並べない()
    {
        // 内側が文の切れ目で終わっていない。外側もそのままでは並べられない。
        Assert.Equal(
            ConditionalRegionLayout.NotSelfContained,
            Scan("""
                #ifdef A
                #ifdef B
                    float4 x = 1
                #endif
                    ;
                #endif
                """));
    }

    [Fact]
    public void 入れ子がマクロを定義していたら外側もそう見なす()
    {
        Assert.Equal(
            ConditionalRegionLayout.DefinesMacros,
            Scan("""
                #ifdef A
                #ifdef B
                #define C 1
                #endif
                #endif
                """));
    }

    [Fact]
    public void 分岐が3つ以上でも形が整っていれば並べる()
    {
        // 条件は「それまでの分岐がすべて外れたとき」を掛け合わせて表す。
        // 形の判定はどの分岐も同じで、分岐の数は関係しない。
        Assert.Equal(
            ConditionalRegionLayout.Mergeable,
            Scan("""
                #ifdef A
                    float4 x;
                #elif defined(B)
                    float4 y;
                #else
                    float4 z;
                #endif
                """));
    }

    [Fact]
    public void 分岐が3つ以上でも閉じていなければ並べない()
    {
        // 分岐が増えても、見るのは 1 つずつである。
        Assert.Equal(
            ConditionalRegionLayout.NotSelfContained,
            Scan("""
                #ifdef A
                    float4 x;
                #elif defined(B)
                    float4 y = 1
                #endif
                """));
    }

    [Fact]
    public void 閉じていない条件は並べない()
    {
        Assert.Equal(
            ConditionalRegionLayout.Unterminated,
            Scan("""
                #ifdef A
                    float4 x;
                """));
    }
}
