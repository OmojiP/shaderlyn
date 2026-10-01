using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.ShaderLab;

namespace Shaderlyn.Tests;

/// <summary>
/// 抑制コメントの検証。
/// </summary>
/// <remarks>
/// <b>抑制は「指摘が消える」機能なので、効きすぎる誤りに気づきにくい。</b>
/// 意図した範囲だけが抑制されること、意図しない範囲が抑制されないことを、
/// 同じ重みで検証する。
/// </remarks>
public sealed class SuppressionTests
{
    /// <summary>
    /// Pass の中身を指定してシェーダーを組み立てる。
    /// </summary>
    /// <param name="passLines">Pass の中に置く行。</param>
    /// <returns>組み立てたシェーダーのソース。</returns>
    /// <remarks>
    /// 文字列の置換ではなく組み立てにしているのは、
    /// 字下げの取り違えでテストが空振りするのを避けるためである。
    /// 抑制のテストが空振りすると「抑制できている」ように見えてしまう。
    /// </remarks>
    private static string Shader(params string[] passLines)
        => "Shader \"Test/Suppression\"\n"
           + "{\n"
           + "    SubShader\n"
           + "    {\n"
           + "        Pass\n"
           + "        {\n"
           + string.Join("\n", passLines.Select(line => "            " + line))
           + "\n        }\n"
           + "    }\n"
           + "}\n";

    /// <summary>指摘が 2 件出るシェーダー。</summary>
    /// <remarks>
    /// 不正なレンダーステート値 (SL1021) を 2 か所に置き、
    /// 片方だけを抑制できることを確かめられるようにしている。
    /// </remarks>
    private static string TwoProblems => Shader("Cull Sideways", "ZWrite Perhaps");

    private static ImmutableArray<Diagnostic> Analyze(string source)
    {
        SourceText text = SourceText.From(source, Path.Combine("Assets", $"{Guid.NewGuid():N}.shader"));
        ShaderLabSyntaxTree tree = ShaderLabSyntaxTree.Parse(text);
        AnalysisTarget unit = new(text, tree.Root, tree.Diagnostics);

        return new AnalyzerDriver(Cli.BuiltInAnalyzers.All).Analyze(unit);
    }

    private static int Count(string source, string ruleId)
        => Analyze(source).Count(d => d.Id == ruleId);

    [Fact]
    public void 抑制コメントが無ければ両方報告される()
        => Assert.Equal(2, Count(TwoProblems, "SL1021"));

    [Fact]
    public void 次の行だけを抑制する()
    {
        string source = Shader(
            "// shaderlyn-disable-next-line SL1021",
            "Cull Sideways",
            "ZWrite Perhaps");

        Assert.Equal(1, Count(source, "SL1021"));
    }

    [Fact]
    public void 行末のコメントでその行を抑制する()
    {
        string source = Shader(
            "Cull Sideways // shaderlyn-disable-line SL1021",
            "ZWrite Perhaps");

        Assert.Equal(1, Count(source, "SL1021"));
    }

    [Fact]
    public void 範囲を指定して抑制する()
    {
        string source = Shader(
            "// shaderlyn-disable SL1021",
            "Cull Sideways",
            "ZWrite Perhaps");

        Assert.Equal(0, Count(source, "SL1021"));
    }

    [Fact]
    public void 解除した後は再び報告する()
    {
        string source = Shader(
            "// shaderlyn-disable SL1021",
            "Cull Sideways",
            "// shaderlyn-enable SL1021",
            "ZWrite Perhaps");

        Assert.Equal(1, Count(source, "SL1021"));
    }

    [Fact]
    public void ファイル全体を抑制する()
    {
        string source = "// shaderlyn-disable-file SL1021\n" + TwoProblems;
        Assert.Equal(0, Count(source, "SL1021"));
    }

    [Fact]
    public void ファイル全体の抑制はコメントより前の行にも効く()
    {
        // disable-file はファイルのどこに書いても全体に効く。
        // 書く場所によって効き方が変わると、利用者が混乱する。
        string source = TwoProblems + "\n// shaderlyn-disable-file SL1021\n";
        Assert.Equal(0, Count(source, "SL1021"));
    }

    [Fact]
    public void ルールIDを省略するとすべてのルールが対象になる()
    {
        string source = "// shaderlyn-disable-file\n" + TwoProblems;
        Assert.Empty(Analyze(source));
    }

    [Fact]
    public void 指定していないルールは抑制しない()
    {
        string source = Shader(
            "// shaderlyn-disable-next-line SL1020",
            "Cull Sideways",
            "ZWrite Perhaps");

        Assert.Equal(2, Count(source, "SL1021"));
    }

    [Fact]
    public void 複数のルールIDを並べて指定できる()
    {
        string source = "// shaderlyn-disable-file SL1021, SL1040\n" + TwoProblems;
        Assert.Empty(Analyze(source));
    }

    [Fact]
    public void ルールIDの後ろに説明を書ける()
    {
        // 抑制の理由を書き残せないと、後から見た人が消してよいか判断できない。
        string source = Shader(
            "// shaderlyn-disable-next-line SL1021 独自の拡張で対応しているため",
            "Cull Sideways",
            "ZWrite Perhaps");

        Assert.Equal(1, Count(source, "SL1021"));
    }

    [Fact]
    public void 文字列の中の目印は抑制コメントとみなさない()
    {
        // 目印を含む文字列で指摘が消えると、消えたことに誰も気づけない。
        string source = """
            Shader "shaderlyn-disable-file SL1040 "
            {
                SubShader { Pass { Cull Sideways } }
            }
            """;

        Assert.Equal(1, Count(source, "SL1021"));
    }

    [Fact]
    public void 抑制コメントの索引を直接検証できる()
    {
        SourceText text = SourceText.From(
            """
            // shaderlyn-disable-next-line SL1001
            line1
            line2
            """,
            "test.shader");

        SuppressionLookup index = SuppressionLookup.GetOrCreate(text);

        Assert.False(index.IsEmpty);
        Assert.True(index.IsSuppressed("SL1001", 1));
        Assert.False(index.IsSuppressed("SL1001", 2));
        Assert.False(index.IsSuppressed("SL1002", 1));
    }

    [Fact]
    public void 抑制コメントが無いテキストの索引は空になる()
    {
        SourceText text = SourceText.From("Shader \"A\" { }", "test.shader");
        Assert.True(SuppressionLookup.GetOrCreate(text).IsEmpty);
    }
}
