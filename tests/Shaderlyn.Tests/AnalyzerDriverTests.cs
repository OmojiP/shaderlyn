using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;

namespace Shaderlyn.Tests;

public sealed class AnalyzerDriverTests
{
    /// <summary>
    /// 検証用の木を組み立てる。
    /// 宣言 "A" は識別子から参照され、宣言 "B" はどこからも参照されない。
    /// </summary>
    private static AnalysisTarget CreateUnit(string content = "declare A; declare B; use A;")
    {
        SourceText text = SourceText.From(content, "test.shader");

        TestRootNode root = new(
            new TextSpan(0, content.Length),
            new TestDeclarationNode(new TextSpan(0, 9), "A"),
            new TestDeclarationNode(new TextSpan(11, 9), "B"),
            new TestIdentifierNode(new TextSpan(22, 5), "A"),
            new TestLiteralNode(new TextSpan(22, 1)));

        return new AnalysisTarget(text, root, []);
    }

    [Fact]
    public void 具象ノード型に登録したアクションがそのノードだけで発火する()
    {
        AnalyzerDriver driver = new([new DeclarationAnalyzer()]);

        ImmutableArray<Diagnostic> diagnostics = driver.Analyze(CreateUnit());

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("TEST0001", d.Id));
        Assert.Equal(["宣言 'A' を検出しました", "宣言 'B' を検出しました"], diagnostics.Select(d => d.GetMessage()));
    }

    [Fact]
    public void 基底ノード型に登録したアクションが派生ノードすべてで発火する()
    {
        AnalyzerDriver driver = new([new ExpressionAnalyzer()]);

        ImmutableArray<Diagnostic> diagnostics = driver.Analyze(CreateUnit());

        // TestIdentifierNode と TestLiteralNode の 2 つが TestExpressionNode を継承している。
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, d => Assert.Equal("TEST0002", d.Id));
    }

    [Fact]
    public void ファイル単位の状態を使う集約ルールが走査後に判定できる()
    {
        AnalyzerDriver driver = new([new UnusedDeclarationAnalyzer()]);

        ImmutableArray<Diagnostic> diagnostics = driver.Analyze(CreateUnit());

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("TEST0003", diagnostic.Id);
        Assert.Equal("宣言 'B' はどこからも参照されていません", diagnostic.GetMessage());
    }

    [Fact]
    public void ファイル単位の状態が次のファイルへ持ち越されない()
    {
        // 同じドライバで 2 ファイルを続けて解析しても、1 ファイル目の宣言が
        // 2 ファイル目の判定に混入しないことを確認する。
        AnalyzerDriver driver = new([new UnusedDeclarationAnalyzer()]);

        ImmutableArray<Diagnostic> first = driver.Analyze(CreateUnit());
        ImmutableArray<Diagnostic> second = driver.Analyze(CreateUnit());

        Assert.Equal(first.Select(d => d.GetMessage()), second.Select(d => d.GetMessage()));
        Assert.Single(second);
    }

    [Fact]
    public void 設定で無効化されたルールのアナライザは実行対象から除外される()
    {
        AnalyzerOptions options = new(ImmutableDictionary<string, DiagnosticSeverity>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase)
            .Add("TEST0001", DiagnosticSeverity.None));

        AnalyzerDriver driver = new([new DeclarationAnalyzer()], options);

        Assert.Empty(driver.Analyzers);
        Assert.Empty(driver.Analyze(CreateUnit()));
    }

    [Fact]
    public void 設定による重要度の上書きが診断へ反映される()
    {
        AnalyzerOptions options = new(ImmutableDictionary<string, DiagnosticSeverity>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase)
            .Add("test0001", DiagnosticSeverity.Error));

        AnalyzerDriver driver = new([new DeclarationAnalyzer()], options);

        ImmutableArray<Diagnostic> diagnostics = driver.Analyze(CreateUnit());

        // ルール ID の大文字小文字を問わず上書きが効くこと。
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Equal(DiagnosticSeverity.Warning, TestDescriptors.Declaration.DefaultSeverity);
    }

    [Fact]
    public void アナライザが投げた例外は隔離され他のアナライザは実行され続ける()
    {
        AnalyzerDriver driver = new([new ThrowingAnalyzer(), new ExpressionAnalyzer()]);

        ImmutableArray<Diagnostic> diagnostics = driver.Analyze(CreateUnit());

        // 例外は TOOL0001 として報告される。宣言ノードは 2 つあるので 2 件。
        ImmutableArray<Diagnostic> toolDiagnostics = [.. diagnostics.Where(d => d.Id == "TOOL0001")];
        Assert.Equal(2, toolDiagnostics.Length);
        Assert.All(toolDiagnostics, d => Assert.Contains(ThrowingAnalyzer.FailureMessage, d.GetMessage(), StringComparison.Ordinal));
        Assert.All(toolDiagnostics, d => Assert.Contains(nameof(ThrowingAnalyzer), d.GetMessage(), StringComparison.Ordinal));

        // 巻き添えで他のアナライザが止まっていないこと。
        Assert.Equal(2, diagnostics.Count(d => d.Id == "TEST0002"));
    }

    [Fact]
    public void 構文木が無くてもファイル全体アクションは実行される()
    {
        AnalysisTarget unit = new(SourceText.From("", "empty.shader"), root: null, []);
        AnalyzerDriver driver = new([new WholeFileAnalyzer(), new DeclarationAnalyzer()]);

        ImmutableArray<Diagnostic> diagnostics = driver.Analyze(unit);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("TEST0004", diagnostic.Id);
    }

    [Fact]
    public void 字句構文解析段階の診断も結果に含まれる()
    {
        SourceText text = SourceText.From("broken", "broken.shader");
        Diagnostic syntaxDiagnostic = Diagnostic.Create(
            TestDescriptors.Declaration, Location.Create(text, new TextSpan(0, 6)), "X");

        AnalysisTarget unit = new(text, root: null, [syntaxDiagnostic]);
        AnalyzerDriver driver = new([]);

        Diagnostic result = Assert.Single(driver.Analyze(unit));
        Assert.Equal("TEST0001", result.Id);
    }

    [Fact]
    public void 診断はソースコード上の出現順に整列される()
    {
        // 木の構築順とは無関係に、位置の昇順で並ぶことを確認する。
        AnalyzerDriver driver = new([new DeclarationAnalyzer(), new ExpressionAnalyzer()]);

        ImmutableArray<Diagnostic> diagnostics = driver.Analyze(CreateUnit());

        int[] starts = [.. diagnostics.Select(d => d.Location.Span.Start)];
        Assert.Equal(starts.OrderBy(s => s), starts);
    }

    [Fact]
    public void キャンセル要求は隔離されず呼び出し元へ伝播する()
    {
        // OperationCanceledException を TOOL0001 に変換してしまうと
        // タイムアウトや中断が効かなくなるため、これだけは再スローする必要がある。
        using CancellationTokenSource cts = new();
        cts.Cancel();

        AnalyzerDriver driver = new([new DeclarationAnalyzer()]);

        Assert.Throws<OperationCanceledException>(() => driver.Analyze(CreateUnit(), cts.Token));
    }
}
