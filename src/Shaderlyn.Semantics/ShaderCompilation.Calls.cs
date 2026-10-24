using System.Collections.Frozen;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Semantics.Conditional;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Semantics;

/// <summary>
/// 関数から呼ばれる関数をたどる。
/// </summary>
public sealed partial class ShaderCompilation
{
    /// <summary>木ごとの、エントリポイントから届く関数 (<see cref="GetReachableFunctions"/>)。</summary>
    private Dictionary<AnalyzedProgram, FrozenSet<FunctionDeclarationSyntax>?>? _reachableFunctions;

    /// <summary>
    /// 関数から呼ばれる関数を、呼び出しをたどって列挙する。
    /// </summary>
    /// <param name="function">たどり始める関数。</param>
    /// <param name="program">関数のある木。</param>
    /// <param name="maxDepth">たどる深さの上限。<paramref name="function"/> が直接呼ぶ関数が 1。</param>
    /// <returns>
    /// 呼ばれる関数と、その呼び出し、そこへ至る構成、深さ。同じ関数は最初に (浅いところで) 見つけた 1 件だけを返す。
    /// <paramref name="function"/> 自身は含まない。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 呼び出しは <see cref="ResolveCall"/> で解決する。多重定義が 1 つに決まらなければ、名前と引数の個数の合う候補をすべてたどる
    /// (届くかもしれない関数を落とさないため)。組み込み関数と、宣言の見つからない呼び出しはたどらない。
    /// </para>
    /// <para>
    /// 構成 (<see cref="CalledFunction.When"/>) は、たどった呼び出しのそれぞれの位置の条件と、呼ばれる関数の宣言の条件の積である。
    /// 木が表す構成は掛けていない。報告に使うときは <see cref="Analysis.ConditionalReports"/> を通すこと。
    /// 再帰 (HLSL では誤りだが書くことはできる) は、訪れた関数を覚えて止める。
    /// </para>
    /// </remarks>
    public IEnumerable<CalledFunction> EnumerateCallees(FunctionDeclarationSyntax function, AnalyzedProgram program, int maxDepth = 8)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(program);

        ConditionMap conditions = GetConditionMap();
        HashSet<FunctionDeclarationSyntax> visited = [function];
        Queue<(FunctionDeclarationSyntax Function, SymbolCondition When, int Depth)> pending = new();
        pending.Enqueue((function, SymbolCondition.Always, 0));

        while (pending.TryDequeue(out (FunctionDeclarationSyntax Function, SymbolCondition When, int Depth) current))
        {
            if (current.Depth >= maxDepth || current.Function.Body is null)
            {
                continue;
            }

            foreach (InvocationExpressionSyntax call in current.Function.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                OverloadResolution resolution = ResolveCall(call, program);
                IEnumerable<FunctionDeclarationSyntax> callees = resolution.Declaration is { } declaration
                    ? [declaration]
                    : resolution.Candidates;

                SymbolCondition reached = current.When.And(conditions.GetCondition(call));

                foreach (FunctionDeclarationSyntax callee in callees)
                {
                    if (!visited.Add(callee))
                    {
                        continue;
                    }

                    SymbolCondition when = conditions.Simplify(reached.And(conditions.GetCondition(callee)));
                    yield return new CalledFunction(callee, call, when, current.Depth + 1);
                    pending.Enqueue((callee, when, current.Depth + 1));
                }
            }
        }
    }

    /// <summary>
    /// 木のエントリポイントから届く関数を返す。エントリポイントが分からなければ <see langword="null"/>。
    /// </summary>
    /// <param name="program">対象の木。</param>
    /// <returns>エントリポイントと、そこから届く関数。</returns>
    internal FrozenSet<FunctionDeclarationSyntax>? GetReachableFunctions(AnalyzedProgram program)
    {
        _reachableFunctions ??= [];

        if (_reachableFunctions.TryGetValue(program, out FrozenSet<FunctionDeclarationSyntax>? cached))
        {
            return cached;
        }

        FrozenSet<FunctionDeclarationSyntax>? reachable = null;

        if (!program.EntryPoints.IsDefaultOrEmpty)
        {
            HashSet<string> names = [.. program.EntryPoints.Select(e => e.Name)];
            HashSet<FunctionDeclarationSyntax> found = [];

            foreach (FunctionDeclarationSyntax entry in program.Tree.Root.Declarations.OfType<FunctionDeclarationSyntax>().Where(f => names.Contains(f.Name)))
            {
                found.Add(entry);
                found.UnionWith(EnumerateCallees(entry, program, int.MaxValue).Select(c => c.Function));
            }

            reachable = found.Count == 0 ? null : found.ToFrozenSet();
        }

        _reachableFunctions[program] = reachable;
        return reachable;
    }
}

/// <summary>呼ばれる関数 1 つ (<see cref="ShaderCompilation.EnumerateCallees"/>)。</summary>
/// <param name="Function">呼ばれる関数の宣言。</param>
/// <param name="Call">それを呼んでいる呼び出し (たどった中で最初に見つけたもの)。</param>
/// <param name="When">そこへ至る構成。</param>
/// <param name="Depth">たどり始めた関数からの深さ。直接呼ぶ関数が 1。</param>
public sealed record CalledFunction(FunctionDeclarationSyntax Function, InvocationExpressionSyntax Call, SymbolCondition When, int Depth);
