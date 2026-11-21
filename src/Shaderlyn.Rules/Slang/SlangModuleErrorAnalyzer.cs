using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の、構文の誤りのあるモジュールの <c>import</c> (SG0353) と、実験中のモジュールの <c>import</c> (SG0362) を検査する。
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
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.ModuleHasErrors, SlangRuleDescriptors.ExperimentalModule, SlangRuleDescriptors.ModifierNotAllowed];

    /// <summary>実験中のモジュールか (slang.neural・slang.numerics とその中のモジュール・slang.functional・experimental.…)。</summary>
    private static bool IsExperimentalModule(string name)
        => name is "slang.neural" or "slang.numerics" or "slang.functional"
           || name.StartsWith("slang.neural.", StringComparison.Ordinal) || name.StartsWith("slang.numerics.", StringComparison.Ordinal)
           || name.StartsWith("slang.functional.", StringComparison.Ordinal) || name.StartsWith("experimental.", StringComparison.Ordinal);

    /// <summary>import slang.neural; (slang: experimental-features が無いときの、実験中のモジュールの import。E00104)。</summary>
    private static void CheckExperimentalImports(SyntaxTreeAnalysisContext context, ShaderCompilation compilation, AnalyzedProgram program)
    {
        if (compilation.Environment is Semantics.Environments.SlangEnvironment { ExperimentalFeatures: true } || compilation.Text.FilePath is not { } root)
        {
            return;
        }

        ImmutableArray<CFamily.Syntax.ShaderSyntaxToken> tokens = program.Tree.PreprocessResult.Tokens;

        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Text is not ("import" or "__import") || tokens[i].Kind != CFamily.Syntax.ShaderSyntaxKind.IdentifierToken
                || tokens[i].Source.FilePath is not { } path || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                || (i > 0 && tokens[i - 1].Kind is not (CFamily.Syntax.ShaderSyntaxKind.SemicolonToken or CFamily.Syntax.ShaderSyntaxKind.CloseBraceToken) && tokens[i - 1].Text is not ("__exported" or "export")))
            {
                continue;
            }

            List<string> parts = [];

            for (int j = i + 1; j < tokens.Length && tokens[j].Kind == CFamily.Syntax.ShaderSyntaxKind.IdentifierToken; j += 2)
            {
                parts.Add(tokens[j].Text);

                if (j + 1 >= tokens.Length || tokens[j + 1].Kind != CFamily.Syntax.ShaderSyntaxKind.DotToken)
                {
                    break;
                }
            }

            string name = string.Join('.', parts);

            // export import a; (import には export を書けない。取り込みをたどらせるなら __exported import。E31201)
            if (i > 0 && tokens[i - 1].Text == "export" && compilation.IsReportable(tokens[i - 1]) && tokens[i - 1].GetLocation() is { } exportLocation)
            {
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ModifierNotAllowed, exportLocation,
                    "import には export を書けません。取り込んだモジュールの宣言を公開する場合は __exported import と書いてください。"));
            }

            if (IsExperimentalModule(name) && Semantics.Symbols.SlangTypeSystem.Instance.IsStandardModule(name.Replace('.', '/')) && compilation.IsReportable(tokens[i + 1]) && tokens[i + 1].GetLocation() is { } location)
            {
                context.ReportDiagnostic(Diagnostic.Create(SlangRuleDescriptors.ExperimentalModule, location,
                    $"{name} は実験中のモジュールです。slangc に -experimental-feature を渡す場合は、設定ファイルの slang: experimental-features を true にしてください。"));
            }
        }
    }

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
            CheckExperimentalImports(context, compilation, program);

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
