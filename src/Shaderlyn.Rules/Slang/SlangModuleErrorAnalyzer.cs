using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、構文の誤りのあるモジュールの <c>import</c> を検査する (SG0353)。
/// </summary>
/// <remarks>
/// <para>
/// 取り込んだモジュール (たどって取り込んだものも) に構文の誤りがあれば、slangc はそのモジュールを読めず、解析するファイルの <c>import</c> の行も誤りにする
/// (slangc 2026.19 の E39999「import failed due to compilation error」)。
/// </para>
/// <para>
/// モジュールの中の誤りそのものは、そのモジュールを解析するときに伝える (解析するファイルには出さない)。ここでは <c>import</c> の行に 1 つだけ伝える。
/// 意味の誤り (宣言されていない名前など) は、モジュールの本体を解析しないので見ない。
/// </para>
/// </remarks>
internal sealed class SlangModuleErrorAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.ModuleHasErrors];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            // 構文の誤りのあるファイル。
            HashSet<string> broken = new(StringComparer.OrdinalIgnoreCase);

            foreach (Diagnostic diagnostic in program.Tree.Diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Location.FilePath is { } path)
                {
                    broken.Add(Path.GetFullPath(path));
                }
            }

            if (broken.Count == 0)
            {
                continue;
            }

            foreach (IncludeReference module in program.Tree.PreprocessResult.Includes)
            {
                if (module.Directive != "import"
                    || module.ResolvedFilePath is not { } resolved
                    || !broken.Contains(Path.GetFullPath(resolved))
                    || !reported.Add((module.Location.FilePath, module.Location.Span.Start)))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ModuleHasErrors, module.Location,
                    $"取り込んだモジュール ({Path.GetFileName(resolved)}) に構文の誤りがあるので、取り込めません。"));
            }
        }
    }
}
