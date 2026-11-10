using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の private のメンバーの使い方を検査する (SG0401)。
/// </summary>
/// <remarks>
/// <para>
/// 構造体の <c>private</c> のフィールドとメソッドは、その構造体の中 (中に定義した構造体を含む) でだけ使える
/// (slangc 2026.19 の「declaration not accessible」、E30600)。<c>s.member</c> の <c>s</c> の型がファイルの中の構造体のときだけ見る。
/// </para>
/// <para>
/// モジュールをまたぐ <c>internal</c> と、言語のバージョンで変わる既定の可視性はまだ見ない。
/// </para>
/// </remarks>
internal sealed class SlangVisibilityAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.NotAccessible];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        Dictionary<AnalyzedProgram, Dictionary<string, HashSet<string>>> privatesByProgram = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not MemberAccessExpressionSyntax { DotToken.Kind: ShaderSyntaxKind.DotToken } access)
            {
                continue;
            }

            if (!privatesByProgram.TryGetValue(program, out Dictionary<string, HashSet<string>>? privates))
            {
                privates = privatesByProgram[program] = CollectPrivates(program.Tree.Root);
            }

            if (privates.Count == 0
                || compilation.GetExpressionTypeBinder(program).GetEvaluatorFor(access.Target).Evaluate(access.Target) is not { } type
                || !privates.TryGetValue(type, out HashSet<string>? members)
                || !members.Contains(access.Name)
                || access.Ancestors().OfType<StructDeclarationSyntax>().Any(s => s.NameToken?.Text == type)
                || !compilation.IsReportable(access.NameToken)
                || access.NameToken.GetLocation() is not { } location)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.NotAccessible, location,
                $"'{access.Name}' は {type} の private のメンバーなので、{type} の外からは使えません。"));
        }
    }

    /// <summary>構造体ごとの private のメンバーの名前。同じ名前の public のメンバー (多重定義) があれば外す。</summary>
    private static Dictionary<string, HashSet<string>> CollectPrivates(CompilationUnitSyntax root)
    {
        Dictionary<string, HashSet<string>> privates = new(StringComparer.Ordinal);

        foreach (StructDeclarationSyntax structure in root.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>())
        {
            if (structure.NameToken is not { } name)
            {
                continue;
            }

            HashSet<string> hidden = new(StringComparer.Ordinal);
            HashSet<string> visible = new(StringComparer.Ordinal);

            foreach (DeclarationSyntax member in structure.Members)
            {
                (IEnumerable<string> names, ImmutableArray<ShaderSyntaxToken> modifiers) = member switch
                {
                    VariableDeclarationSyntax field => (field.Variables.Select(v => v.Name), field.ModifierTokens),
                    FunctionDeclarationSyntax method => ([method.Name], method.ModifierTokens),
                    _ => ([], []),
                };

                (modifiers.Any(t => t.Text == "private") ? hidden : visible).UnionWith(names);
            }

            hidden.ExceptWith(visible);

            if (hidden.Count > 0)
            {
                // 同じ名前の構造体が 2 つあれば (名前空間が違うなど)、どちらか決められない。
                if (!privates.TryAdd(name.Text, hidden))
                {
                    privates[name.Text] = [];
                }
            }
        }

        return privates;
    }
}
