using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>
/// WGSL の宣言の決まりを検査する。
/// </summary>
/// <remarks>
/// 構造体のメンバーの名前の重複 (CF0314)。naga 30 は「redefinition」の誤りにする。
/// </remarks>
internal sealed class WgslDeclarationAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesWgsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.Redeclaration];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsWgsl)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (StructDeclarationSyntax structure in program.Tree.Root.Declarations.OfType<StructDeclarationSyntax>())
            {
                Dictionary<string, ShaderSyntaxToken> members = new(StringComparer.Ordinal);

                foreach (VariableDeclaratorSyntax member in structure.Members.OfType<VariableDeclarationSyntax>().SelectMany(m => m.Variables))
                {
                    if (member.NameToken.IsMissing
                        || members.TryAdd(member.Name, member.NameToken)
                        || !compilation.IsReportable(member.NameToken)
                        || members[member.Name].GetLocation() is not { } first
                        || member.NameToken.GetLocation() is not { } location)
                    {
                        continue;
                    }

                    context.ReportDiagnostic(Diagnostic.Create(
                        HlslRuleDescriptors.Redeclaration, location, member.Name, (first.LineSpan.Start.Line + 1).ToString(CultureInfo.InvariantCulture)));
                }
            }
        }
    }
}
