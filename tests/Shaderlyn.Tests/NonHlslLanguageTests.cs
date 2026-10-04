using System.Collections.Immutable;
using Shaderlyn.Cli;
using Shaderlyn.Cli.Environments;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Core.Text;
using Shaderlyn.Semantics;

namespace Shaderlyn.Tests;

/// <summary>
/// HLSL ではない言語の環境でも、CLI の実行から出力までが通ることの検証 (Godot の余地)。
/// </summary>
/// <remarks>
/// <para>
/// Godot のシェーダー言語は HLSL の字句解析・型を使えない。それでも、言語ごとの解析器が解析単位
/// (<see cref="AnalysisTarget"/>) を返せば、ファイルの探索・抑制コメント・ベースライン・出力・キャッシュは
/// HLSL の型に触れずに働くはずである。それを作り物の言語 (「Toy」) で確かめる。
/// </para>
/// <para>
/// Toy の決まり: 1 行が 1 つの文。<c>TODO</c> を含む行を TOY0001 として報告する。
/// </para>
/// </remarks>
public sealed class NonHlslLanguageTests : IDisposable
{
    private const string Source = """
        shader_type canvas_item;
        // TODO: 色を決める
        void fragment() { }
        // shaderlyn-disable-next-line TOY0001
        // TODO: 抑制した行
        """;

    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), "shaderlyn-toy-" + Guid.NewGuid().ToString("N"));

    public NonHlslLanguageTests()
    {
        Directory.CreateDirectory(_workDirectory);
        File.WriteAllText(Path.Combine(_workDirectory, ".shaderlyn.yaml"), "environment: toy\n");
        File.WriteAllText(Path.Combine(_workDirectory, "Sprite.toy"), Source);

        // 探す拡張子でないファイルは解析しない。
        File.WriteAllText(Path.Combine(_workDirectory, "Other.hlsl"), "float4 main() : SV_Target { return x; }");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない。
        }
    }

    [Fact]
    public async Task 作り物の言語でも解析から出力まで通る()
    {
        (ExitCode code, string output, string error) = await RunAsync(_workDirectory);

        Assert.True(error.Length == 0, error);
        Assert.Equal(ExitCode.DiagnosticsFound, code);
        Assert.Contains("Sprite.toy(2:4): warning TOY0001", output, StringComparison.Ordinal);
        Assert.Contains("1 ファイルを解析: エラー 0 件, 警告 1 件", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 作り物の言語でもSARIFとベースラインとキャッシュが働く()
    {
        string sarif = Path.Combine(_workDirectory, "out.sarif");
        string baseline = Path.Combine(_workDirectory, "baseline.json");
        string cache = Path.Combine(_workDirectory, "cache");

        Assert.Equal(ExitCode.DiagnosticsFound, (await RunAsync(_workDirectory, "--format", "sarif", "--output", sarif)).Code);
        Assert.Contains("TOY0001", File.ReadAllText(sarif), StringComparison.Ordinal);

        Assert.Equal(ExitCode.Success, (await RunAsync(_workDirectory, "--write-baseline", baseline)).Code);
        Assert.Equal(ExitCode.Success, (await RunAsync(_workDirectory, "--baseline", baseline)).Code);

        (ExitCode first, string firstOutput, _) = await RunAsync(_workDirectory, "--cache", cache);
        (ExitCode second, string secondOutput, _) = await RunAsync(_workDirectory, "--cache", cache);
        Assert.Equal(first, second);
        Assert.Equal(firstOutput, secondOutput);
    }

    [Fact]
    public async Task 作り物の言語でも環境を表示できる()
    {
        (ExitCode code, string output, _) = await RunAsync(_workDirectory, "--env");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("環境: Toy", output, StringComparison.Ordinal);
        Assert.DoesNotContain("言語のバージョン", output, StringComparison.Ordinal);
    }

    private static async Task<(ExitCode Code, string Output, string Error)> RunAsync(params string[] args)
    {
        HostEnvironment toy = new("toy", "Toy", [".toy"], () => throw new InvalidOperationException("Toy は HLSL ではない"))
        {
            CreateAnalysisEnvironment = () => new ToyEnvironment(),
        };

        StringWriter output = new();
        StringWriter error = new();
        ExitCode code = await Program.RunAsync(args, [new TodoAnalyzer()], [toy], output, error);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>Toy の環境。1 行を 1 つのノードにする。</summary>
    private sealed class ToyEnvironment : IAnalysisEnvironment
    {
        public string Name => "toy";

        public string DisplayName => "Toy";

        public ImmutableArray<string> DefaultExtensions => [".toy"];

        public Semantics.Environments.IHlslEnvironment? HlslEnvironment => null;

        public PreparedFile Prepare(SourceText text, SemanticsOptions? semantics, AnalysisTimings? timings)
        {
            List<ToyLine> lines = [];
            int start = 0;
            string content = text.ToString();

            while (start <= content.Length)
            {
                int end = content.IndexOf('\n', start);
                end = end < 0 ? content.Length : end;
                lines.Add(new ToyLine(TextSpan.FromBounds(start, end), content[start..end].TrimEnd('\r')));
                start = end + 1;
            }

            ToyRoot root = new(new TextSpan(0, content.Length), [.. lines]);

            foreach (ToyLine line in lines)
            {
                line.SetParent(root);
            }

            return new PreparedFile(new AnalysisTarget(text, root, []), [], null);
        }
    }

    private sealed class ToyRoot(TextSpan span, ImmutableArray<ToyLine> lines) : SyntaxNode
    {
        public override TextSpan FullSpan => span;

        public override TextSpan Span => span;

        public override IEnumerable<SyntaxNode> ChildNodes() => lines;
    }

    private sealed class ToyLine(TextSpan span, string text) : SyntaxNode
    {
        public string Text { get; } = text;

        public override TextSpan FullSpan => span;

        public override TextSpan Span => span;

        public override IEnumerable<SyntaxNode> ChildNodes() => [];
    }

    /// <summary><c>TODO</c> を含む行を報告する。</summary>
    private sealed class TodoAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "TOY0001", "TODO が残っています", "TODO が残っています。", "Usage", DiagnosticSeverity.Warning);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
            => context.RegisterNodeAction<ToyLine>(node =>
            {
                int column = node.Node.Text.IndexOf("TODO", StringComparison.Ordinal);

                if (column >= 0)
                {
                    node.ReportDiagnostic(Rule, new TextSpan(node.Node.Span.Start + column, 4));
                }
            });
    }
}
