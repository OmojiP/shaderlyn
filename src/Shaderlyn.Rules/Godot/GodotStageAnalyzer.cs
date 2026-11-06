using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Godot;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の、ステージで使えない組み込み関数 (GD0002) と、ステージ関数の形 (GD0322) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// フラグメントだけの組み込み関数 (<c>dFdx</c> など、<see cref="GodotBuiltins.FragmentOnlyFunctions"/>) は、
/// <c>spatial</c> / <c>canvas_item</c> / <c>sky</c> でだけ使える。ほかの種類では、どこで使っても誤りである。
/// 使える種類でも、ステージ関数の中でだけ使える組み込み関数 (<c>texture_sdf</c> など) と同じく、
/// <c>vertex</c> の中と、<c>vertex</c> から (間接的にでも) 呼ぶ利用者の関数の中では使えない。その関数の中の最初の使用で報告する。
/// Godot 4.7.2 の <c>_validate_restricted_func</c> と同じで、Godot に読ませて確かめた。
/// </para>
/// <para>
/// <c>discard</c> は <c>spatial</c> / <c>canvas_item</c> でだけ使え、<c>vertex</c> の中と <c>vertex</c> から呼ぶ利用者の関数の中では使えない (dFdx などと同じ扱い)。
/// </para>
/// <para>
/// ステージ関数 (そのシェーダーの種類の <c>vertex</c> / <c>fragment</c> など) は、仮引数を持てず、戻り値は <c>void</c> である。
/// ほかの種類のステージ関数の名前 (<c>particles</c> の <c>fragment</c>) は、ただの利用者の関数である。
/// </para>
/// </remarks>
internal sealed class GodotStageAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>フラグメントだけの組み込み関数を使えるシェーダーの種類 (<c>is_supported_frag_only_funcs</c>)。</summary>
    private static readonly ImmutableHashSet<string> FragmentFunctionShaderTypes = ["spatial", "canvas_item", "sky"];

    /// <summary><c>discard</c> を使えるシェーダーの種類 (<c>is_discard_supported</c>)。</summary>
    private static readonly ImmutableHashSet<string> DiscardShaderTypes = ["spatial", "canvas_item"];

    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GodotRuleDescriptors.NotAvailableInStage, GodotRuleDescriptors.StageFunctionSignature];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGodot)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];

        void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken token, params object[] arguments)
        {
            if (compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start)))
            {
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, arguments));
            }
        }

        foreach (Semantics.AnalyzedProgram program in compilation.Programs)
        {
            if (compilation.TypeSystem.ForTree(program.Tree) is not GodotTypeSystem types
                || program.Tree.Language is not GodotLanguageOptions { ShaderType: { } shaderType })
            {
                continue;
            }

            ImmutableArray<FunctionDeclarationSyntax> functions = [.. program.Tree.Root.Declarations.OfType<FunctionDeclarationSyntax>().Where(f => f.Body is not null)];
            HashSet<string> userFunctions = [.. functions.Select(f => f.Name)];

            CheckSignatures(functions, types, Report);

            HashSet<string> stageOnly = [.. GodotBuiltins.Instance.StageFunctions.Where(f => f.ShaderType == shaderType).Select(f => f.Function.Name)];
            bool fragmentFunctions = FragmentFunctionShaderTypes.Contains(shaderType);

            // 関数ごとの、呼んでいる利用者の関数と、最初に使った「vertex で使えないもの」。
            Dictionary<string, HashSet<string>> calls = new(StringComparer.Ordinal);
            Dictionary<string, (ShaderSyntaxToken Token, string Name)> firstRestricted = new(StringComparer.Ordinal);
            bool discard = DiscardShaderTypes.Contains(shaderType);

            foreach (FunctionDeclarationSyntax function in functions)
            {
                HashSet<string> callees = calls[function.Name] = new(StringComparer.Ordinal);

                foreach (ShaderSyntaxNode node in function.Body!.DescendantNodesAndSelf().OfType<ShaderSyntaxNode>())
                {
                    if (node is JumpStatementSyntax { Keyword.Text: "discard" } jump)
                    {
                        if (!discard)
                        {
                            Report(GodotRuleDescriptors.NotAvailableInStage, jump.Keyword, $"discard は、{shaderType} のシェーダーでは使えません。");
                        }
                        else
                        {
                            firstRestricted.TryAdd(function.Name, (jump.Keyword, "discard"));
                        }

                        continue;
                    }

                    if (node is not InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee })
                    {
                        continue;
                    }

                    if (userFunctions.Contains(callee.Name))
                    {
                        callees.Add(callee.Name);
                        continue;
                    }

                    bool fragmentOnly = GodotBuiltins.Instance.FragmentOnlyFunctions.Contains(callee.Name);

                    // フラグメントだけの組み込み関数を使えない種類では、どこで使っても誤り。
                    if (fragmentOnly && !fragmentFunctions)
                    {
                        Report(GodotRuleDescriptors.NotAvailableInStage, callee.Identifier,
                            $"組み込み関数 '{callee.Name}' は、{shaderType} のシェーダーでは使えません。");
                        continue;
                    }

                    if (fragmentOnly || stageOnly.Contains(callee.Name))
                    {
                        firstRestricted.TryAdd(function.Name, (callee.Identifier, callee.Name));
                    }
                }
            }

            if (!types.IsStageFunction("vertex") || !calls.ContainsKey("vertex"))
            {
                continue;
            }

            // vertex の中と、vertex から呼ぶ利用者の関数の中。
            if (firstRestricted.TryGetValue("vertex", out var inVertex))
            {
                Report(GodotRuleDescriptors.NotAvailableInStage, inVertex.Token,
                    $"'{inVertex.Name}' は vertex 関数の中では使えません。");
            }

            HashSet<string> visited = new(StringComparer.Ordinal) { "vertex" };
            Queue<string> pending = new(calls["vertex"]);

            while (pending.TryDequeue(out string? name))
            {
                if (!visited.Add(name))
                {
                    continue;
                }

                if (firstRestricted.TryGetValue(name, out var used))
                {
                    Report(GodotRuleDescriptors.NotAvailableInStage, used.Token,
                        $"'{used.Name}' はここでは使えません。関数 '{name}' は vertex 関数から呼ばれています。");
                }

                foreach (string callee in calls.GetValueOrDefault(name, []))
                {
                    pending.Enqueue(callee);
                }
            }
        }
    }

    /// <summary>ステージ関数の仮引数と戻り値を調べる。</summary>
    private static void CheckSignatures(
        ImmutableArray<FunctionDeclarationSyntax> functions,
        GodotTypeSystem types,
        Action<DiagnosticDescriptor, ShaderSyntaxToken, object[]> report)
    {
        foreach (FunctionDeclarationSyntax function in functions.Where(f => types.IsStageFunction(f.Name)))
        {
            if (function.ParameterList.Any())
            {
                report(GodotRuleDescriptors.StageFunctionSignature, function.NameToken, [$"ステージ関数 '{function.Name}' は仮引数を持てません。"]);
            }
            else if (function.ReturnType.Name != "void")
            {
                report(GodotRuleDescriptors.StageFunctionSignature, function.NameToken, [$"ステージ関数 '{function.Name}' の戻り値は void でなければなりません。"]);
            }
        }
    }
}
