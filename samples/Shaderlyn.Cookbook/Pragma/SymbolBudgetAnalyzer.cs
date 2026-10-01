using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 1 つのコードブロックが宣言するシンボルの数を制限する。
/// </summary>
/// <remarks>
/// <b>バリアントの数はシンボルの数の指数で増える。</b>
/// 10 個宣言すれば最大 1,024 通りがコンパイルされる。
/// ビルド時間とメモリを決めるのはここなので、上限はシンボルの数で決める。
/// </remarks>
public sealed class SymbolBudgetAnalyzer(int maxSymbols) : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.TooManySymbols];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterSyntaxTreeAction(c =>
        {
            if (c.Unit.GetModel<ShaderCompilation>() is not { } compilation)
            {
                return;
            }

            foreach (AnalyzedProgram program in compilation.Programs)
            {
                int declared = ShaderSymbols.CollectDeclared(program.Tree.Pragmas).Count;

                if (declared > maxSymbols)
                {
                    c.ReportDiagnostic(
                        CookbookRules.TooManySymbols,
                        CookbookProgramSpan.Of(program),
                        declared,
                        maxSymbols);
                }
            }
        });
    }
}
