using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>
/// WGSL の関数の再帰の呼び出しを検査する (WG0332)。
/// </summary>
/// <remarks>
/// 関数が、直接 (<c>f</c> の中で <c>f()</c>) か、別の関数を通して (<c>f</c> → <c>g</c> → <c>f</c>) 自分自身を呼べば誤りである
/// (naga 30 の「declaration of `f` is recursive」と「is cyclic」)。輪に入る関数ごとに、関数の名前の位置で報告する。
/// </remarks>
internal sealed class WgslRecursionAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesWgsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [WgslRuleDescriptors.RecursiveCall];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsWgsl)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            Dictionary<string, FunctionDeclarationSyntax> functions = new(StringComparer.Ordinal);

            foreach (FunctionDeclarationSyntax function in program.Tree.Root.Declarations.OfType<FunctionDeclarationSyntax>())
            {
                functions.TryAdd(function.Name, function);
            }

            Dictionary<string, string[]> calls = functions.ToDictionary(
                f => f.Key,
                f => f.Value.Body is { } body
                    ? body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                        .Select(i => i.TargetName).OfType<string>().Where(functions.ContainsKey).Distinct().ToArray()
                    : [],
                StringComparer.Ordinal);

            foreach ((string name, FunctionDeclarationSyntax function) in functions)
            {
                if (FindPath(calls, name) is not { } path || !compilation.IsReportable(function.NameToken) || function.NameToken.GetLocation() is not { } location)
                {
                    continue;
                }

                string message = path.Count == 1
                    ? $"関数 '{name}' が自分自身を呼んでいます。WGSL では再帰の呼び出しができません。"
                    : $"関数 '{name}' が、ほかの関数を通して自分自身を呼んでいます ({string.Join(" → ", [name, .. path])})。WGSL では再帰の呼び出しができません。";
                context.ReportDiagnostic(Diagnostic.Create(WgslRuleDescriptors.RecursiveCall, location, message));
            }
        }
    }

    /// <summary>関数から自分自身へ戻る呼び出しの道 (最初の関数を除き、最後が自分)。戻らなければ <see langword="null"/>。</summary>
    private static List<string>? FindPath(Dictionary<string, string[]> calls, string start)
    {
        Dictionary<string, string?> previous = new(StringComparer.Ordinal);
        Queue<string> queue = new();

        foreach (string next in calls[start])
        {
            if (previous.TryAdd(next, null))
            {
                queue.Enqueue(next);
            }
        }

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();

            if (current == start)
            {
                List<string> path = [];
                for (string? at = current; at is not null; at = previous[at])
                {
                    path.Insert(0, at);
                }

                return path;
            }

            foreach (string next in calls[current])
            {
                if (previous.TryAdd(next, current))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return null;
    }
}
