using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;

namespace Shaderlyn.Tests;

/// <summary>解析ドライバの検証に使うルール定義。</summary>
internal static class TestDescriptors
{
    public static DiagnosticDescriptor Declaration { get; } = new(
        "TEST0001", "宣言を検出", "宣言 '{0}' を検出しました", "Test", DiagnosticSeverity.Warning);

    public static DiagnosticDescriptor Expression { get; } = new(
        "TEST0002", "式を検出", "式ノードを検出しました", "Test", DiagnosticSeverity.Info);

    public static DiagnosticDescriptor Unused { get; } = new(
        "TEST0003", "未参照の宣言", "宣言 '{0}' はどこからも参照されていません", "Test", DiagnosticSeverity.Warning);

    public static DiagnosticDescriptor WholeFile { get; } = new(
        "TEST0004", "ファイル全体の検査", "ファイル全体を検査しました", "Test", DiagnosticSeverity.Info);
}

/// <summary>具象ノード型に登録し、宣言ごとに 1 件報告するアナライザ。</summary>
internal sealed class DeclarationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [TestDescriptors.Declaration];

    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<TestDeclarationNode>(
            c => c.ReportDiagnostic(TestDescriptors.Declaration, c.Node.Name));
}

/// <summary>基底ノード型に登録し、派生ノードすべてで発火することを確認するためのアナライザ。</summary>
internal sealed class ExpressionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [TestDescriptors.Expression];

    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<TestExpressionNode>(c => c.ReportDiagnostic(TestDescriptors.Expression));
}

/// <summary>
/// ファイル単位の状態を使い、走査後にまとめて判定するアナライザ。
/// 「どこからも参照されていない宣言」という集約型ルールの雛形。
/// </summary>
internal sealed class UnusedDeclarationAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [TestDescriptors.Unused];

    public override void Initialize(AnalysisContext context)
        => context.RegisterAnalysisStartAction(start =>
        {
            Dictionary<string, TestDeclarationNode> declared = [];
            HashSet<string> referenced = [];

            start.RegisterNodeAction<TestDeclarationNode>(c => declared[c.Node.Name] = c.Node);
            start.RegisterNodeAction<TestIdentifierNode>(c => referenced.Add(c.Node.Name));

            start.RegisterAnalysisEndAction(end =>
            {
                foreach ((string name, TestDeclarationNode node) in declared.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!referenced.Contains(name))
                    {
                        end.ReportDiagnostic(TestDescriptors.Unused, node.Span, name);
                    }
                }
            });
        });
}

/// <summary>ファイル全体に対して 1 回だけ報告するアナライザ。</summary>
internal sealed class WholeFileAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [TestDescriptors.WholeFile];

    public override void Initialize(AnalysisContext context)
        => context.RegisterSyntaxTreeAction(
            c => c.ReportDiagnostic(TestDescriptors.WholeFile, new Core.Text.TextSpan(0, 0)));
}

/// <summary>必ず例外を投げるアナライザ。ドライバの例外隔離を検証するために使う。</summary>
internal sealed class ThrowingAnalyzer : DiagnosticAnalyzer
{
    public const string FailureMessage = "意図的な失敗";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [TestDescriptors.Declaration];

    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<TestDeclarationNode>(_ => throw new InvalidOperationException(FailureMessage));
}
