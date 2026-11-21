using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules;

/// <summary>
/// 既定値のある仮引数の後ろに、既定値の無い仮引数があるかを検査する (CF0316)。
/// </summary>
/// <remarks>
/// FXC (X3044 <c>missing default value for parameter</c>)・DXC 1.8 (<c>missing default argument on parameter</c>)・
/// slangc 2026.19 (E30704) がエラーにする。構造体のメソッドと、名前空間の中の関数も見る。
/// </remarks>
internal sealed class DefaultParameterOrderAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [HlslRuleDescriptors.MissingDefaultValue];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // 共通コード片の関数は複数のコードブロックに現れるので、同じ位置に重ねて報告しない。
        HashSet<(string, int)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (FunctionDeclarationSyntax function in Functions(program.Tree.Root))
            {
                ParameterSyntax? withDefault = null;

                foreach (ParameterSyntax parameter in function.ParameterList)
                {
                    if (parameter.DefaultValue is not null)
                    {
                        withDefault = parameter;
                        continue;
                    }

                    if (withDefault is null)
                    {
                        continue;
                    }

                    Location location = parameter.NameToken.GetLocation();

                    if (reported.Add((location.FilePath, location.Span.Start)))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(HlslRuleDescriptors.MissingDefaultValue, location, parameter.Name, withDefault.Name));
                    }

                    break;
                }
            }
        }
    }

    /// <summary>関数の宣言を列挙する。関数の本体の中には入らない。</summary>
    private static IEnumerable<FunctionDeclarationSyntax> Functions(ShaderSyntaxNode node)
    {
        foreach (ShaderSyntaxNode child in node.ChildNodes())
        {
            if (child is FunctionDeclarationSyntax function)
            {
                yield return function;
            }
            else if (child is DeclarationSyntax)
            {
                foreach (FunctionDeclarationSyntax inner in Functions(child))
                {
                    yield return inner;
                }
            }
        }
    }
}
