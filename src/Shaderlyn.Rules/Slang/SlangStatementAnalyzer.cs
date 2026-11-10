using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の switch の札 (SG0321) と、static const の初期値の循環 (SG0317) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// SG0321: <c>case</c> と <c>default</c> は、<c>switch</c> の本体の直下にだけ書ける。<c>switch</c> の外と、<c>case</c> の中の <c>{ }</c> の中は誤り
/// (slangc 2026.19 の「'case' is not allowed outside a 'switch' body」)。HLSL の DXC は入れ子のブロックの中の <c>case</c> も読むが、Slang は読まない。
/// </para>
/// <para>
/// SG0317: <c>static const</c> の初期値が、直接か、同じ入れ物 (ファイルの直下か同じ構造体) のほかの <c>static const</c> を通して自分自身を使えば誤り
/// (「the initial-value expression for variable depends on the value of the variable itself」)。
/// </para>
/// </remarks>
internal sealed class SlangStatementAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.SwitchLabelOutsideSwitch, SlangRuleDescriptors.CyclicConstant];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach ((ShaderSyntaxNode node, AnalyzedProgram _) in compilation.EnumerateRuleNodes())
        {
            if (node is SwitchLabelStatementSyntax label && label.Parent is not SwitchStatementSyntax
                && compilation.IsReportable(label.Keyword) && label.Keyword.GetLocation() is { } location)
            {
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.SwitchLabelOutsideSwitch, location,
                    $"{label.Keyword.Text} は switch の本体の直下にだけ書けます。"));
            }
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            CheckConstants(context, compilation, program.Tree.Root.Declarations);

            foreach (StructDeclarationSyntax structure in program.Tree.Root.Declarations.OfType<StructDeclarationSyntax>())
            {
                CheckConstants(context, compilation, structure.Members);
            }
        }
    }

    /// <summary>同じ入れ物の static const の初期値の依存をたどり、自分自身に戻るものを報告する。</summary>
    private static void CheckConstants(SyntaxTreeAnalysisContext context, ShaderCompilation compilation, ImmutableArray<DeclarationSyntax> declarations)
    {
        Dictionary<string, VariableDeclaratorSyntax> constants = new(StringComparer.Ordinal);

        foreach (VariableDeclarationSyntax declaration in declarations.OfType<VariableDeclarationSyntax>())
        {
            if (declaration.ModifierTokens.Any(t => t.Text == "static") && declaration.ModifierTokens.Any(t => t.Text == "const"))
            {
                foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
                {
                    if (declarator.Initializer is not null)
                    {
                        constants.TryAdd(declarator.Name, declarator);
                    }
                }
            }
        }

        if (constants.Count == 0)
        {
            return;
        }

        Dictionary<string, string[]> uses = constants.ToDictionary(
            c => c.Key,
            c => c.Value.Initializer!.DescendantNodesAndSelf().OfType<IdentifierExpressionSyntax>()
                .Where(i => i.Parent is not MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access || !ReferenceEquals(access.NameToken, i.Identifier))
                .Select(i => i.Name).Where(constants.ContainsKey).Distinct().ToArray(),
            StringComparer.Ordinal);

        foreach ((string name, VariableDeclaratorSyntax declarator) in constants)
        {
            if (!ReachesSelf(uses, name) || !compilation.IsReportable(declarator.NameToken) || declarator.NameToken.GetLocation() is not { } location)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.CyclicConstant, location,
                $"static const の '{name}' の初期値が、自分自身の値に依っています。"));
        }
    }

    private static bool ReachesSelf(Dictionary<string, string[]> uses, string start)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> pending = new(uses[start]);

        while (pending.Count > 0)
        {
            string current = pending.Pop();

            if (current == start)
            {
                return true;
            }

            if (seen.Add(current))
            {
                foreach (string next in uses[current])
                {
                    pending.Push(next);
                }
            }
        }

        return false;
    }
}
