using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;

namespace Shaderlyn.Tests;

/// <summary>
/// 取り込まれる前提の断片 (<c>.hlsl</c> など) を単体で解析したときの検証。
/// </summary>
/// <remarks>
/// <para>
/// <b>断片だけでは判断できないことを報告しない。</b>
/// シェーダーステージの <c>#pragma</c> やシンボルの宣言は、取り込む側の <c>.shader</c> に書かれる。
/// </para>
/// <para>
/// <c>.compute</c> は 1 ファイルで完結するので、今までどおり検査する。
/// 断片と同じ扱いにすると、コンピュートシェーダーの宣言漏れが見えなくなる。
/// </para>
/// </remarks>
public sealed class StandaloneHlslRuleTests
{
    /// <summary>ステージの指定も、シンボルの宣言も持たない HLSL。</summary>
    private const string Fragment = """
        #ifdef _FOO
        float4 InFoo() { return 1; }
        #endif

        float4 Always() { return 0; }
        """;

    /// <summary>HLSL 単体として解析して診断を得る。</summary>
    /// <param name="source">解析するソース。</param>
    /// <param name="fileName">ファイル名。拡張子で断片かどうかが決まる。</param>
    /// <returns>検出した診断。</returns>
    private static ImmutableArray<Diagnostic> Analyze(string source, string fileName)
    {
        SourceText text = SourceText.From(source, Path.Combine("Assets", fileName));
        AnalysisTarget unit = ShaderCompilation.CreateForHlsl(text, new SemanticsOptions()).CreateAnalysisTarget();

        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(unit);
    }

    [Theory]
    [InlineData("Common.hlsl")]
    [InlineData("Common.cginc")]
    [InlineData("Common.hlslinc")]
    public void 断片ではステージの指定が無いことを報告しない(string fileName)
        => Assert.DoesNotContain(Analyze(Fragment, fileName), d => d.Id == "HL0302");

    [Theory]
    [InlineData("Common.hlsl")]
    [InlineData("Common.cginc")]
    [InlineData("Common.hlslinc")]
    public void 断片では宣言されていないシンボルを報告しない(string fileName)
        => Assert.DoesNotContain(Analyze(Fragment, fileName), d => d.Id == "HL0330");

    [Fact]
    public void computeではステージの指定が無いことを報告する()
        => Assert.Contains(Analyze(Fragment, "Kernel.compute"), d => d.Id == "HL0302");

    [Fact]
    public void computeでは宣言されていないシンボルを報告する()
        => Assert.Contains(Analyze(Fragment, "Kernel.compute"), d => d.Id == "HL0330");
}
