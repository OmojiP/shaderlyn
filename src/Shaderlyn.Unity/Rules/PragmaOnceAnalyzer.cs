using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Rules;
using Shaderlyn.Semantics;

namespace Shaderlyn.Unity.Rules;

/// <summary>
/// Unity では効かない <c>#pragma once</c> を報告する (UN0101)。
/// </summary>
/// <remarks>
/// <para>
/// Unity 2022.3 / 6.3 / 6.6 の実測では、<c>#pragma once</c> を書いたファイルを 2 度取り込むと、再定義のエラーになる
/// (<c>#include</c> でも <c>#include_with_pragmas</c> でも同じ)。Unity の環境のプリプロセッサも効かせない
/// (<see cref="PreprocessorOptions.HonorPragmaOnce"/>)。
/// </para>
/// <para>
/// 利用者が書いたファイルのものだけを報告する。Unity のパッケージにも書いたものがあるが、利用者には直せない。
/// </para>
/// </remarks>
internal sealed class PragmaOnceAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [UnityRuleDescriptors.PragmaOnceIgnored];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (compilation.Environment is not UnityHlslEnvironment)
        {
            return;
        }

        // 同じファイルが Pass ごとに取り込まれる。位置で 1 つにまとめる。
        HashSet<(string File, int Start)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            // #include で取り込んだファイルの #pragma は使われない (2023.2 以降) が、#pragma once が効かないことは同じである。
            foreach (PragmaDirective pragma in program.Tree.Pragmas.Concat(program.Tree.PreprocessResult.IgnoredPragmas.IsDefault ? [] : program.Tree.PreprocessResult.IgnoredPragmas))
            {
                if (pragma.Name != "once"
                    || pragma.NameToken.IsFromMacroExpansion
                    || !compilation.IsUserFile(pragma.NameToken.Source.FilePath)
                    || pragma.NameToken.GetLocation() is not { } location
                    || !reported.Add((location.FilePath, location.Span.Start)))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(UnityRuleDescriptors.PragmaOnceIgnored, location));
            }
        }
    }
}
