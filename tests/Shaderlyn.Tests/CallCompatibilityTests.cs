using Shaderlyn.Hlsl;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Tests;

/// <summary>
/// 呼び出しが宣言に合うかの小さな判定を検証する。
/// </summary>
/// <remarks>
/// ルール (HL0340〜HL0342) と <c>ResolveCall</c> の両方がこれを使う。
/// 振る舞いはそれぞれのテストでも確かめているが、公開 API としての約束はここで押さえる。
/// </remarks>
public sealed class CallCompatibilityTests
{
    private const string Source = """
        void f(float a, out float b, inout float c, float d = 1) { }

        void g()
        {
            float x;
            f(x, x, (x), 1.0 + x);
            f(1.0, g(), x * 2, (float)x);
        }
        """;

    private static readonly HlslSyntaxTree Tree = HlslHarness.Parse(Source);

    private static FunctionDeclarationSyntax Function(string name)
        => Tree.Root.DescendantNodesAndSelf().OfType<FunctionDeclarationSyntax>().First(f => f.Name == name);

    private static InvocationExpressionSyntax[] Calls()
        => [.. Tree.Root.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Where(i => i.TargetName == "f")];

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void 既定値のある仮引数は省ける(int count, bool expected)
        => Assert.Equal(expected, CallCompatibility.AcceptsArgumentCount(Function("f").ParameterList, count));

    [Fact]
    public void outとinoutだけを書き戻す仮引数とみなす()
    {
        bool[] writeback = [.. Function("f").ParameterList.Select(CallCompatibility.IsWriteback)];

        Assert.Equal([false, true, true, false], writeback);
    }

    [Fact]
    public void 書き戻せないと言い切れる式だけを書き戻せないとする()
    {
        InvocationExpressionSyntax[] calls = Calls();

        // 変数と括弧で包んだ変数は書き戻せる。演算の結果は書き戻せない。
        Assert.Equal(
            [true, true, true, false],
            calls[0].ArgumentExpressions.Select(CallCompatibility.IsAssignable));

        // リテラル、関数の戻り値、演算の結果、型変換の結果は書き戻せない。
        Assert.Equal(
            [false, false, false, false],
            calls[1].ArgumentExpressions.Select(CallCompatibility.IsAssignable));
    }
}
