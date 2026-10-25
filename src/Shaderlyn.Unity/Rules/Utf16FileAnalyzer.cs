using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Rules;
using Shaderlyn.Semantics;

namespace Shaderlyn.Unity.Rules;

/// <summary>
/// Unity が読めない UTF-16 で保存したファイルを報告する (UN0001)。
/// </summary>
/// <remarks>
/// <para>
/// Unity 2022.3 / 6.3 / 6.6 の実測では、UTF-16 のファイルは BOM があっても読めない。
/// Shaderlyn は UTF-16 も読める (DXC は読めるので、汎用の HLSL の環境では読めなければならない) ので、
/// 読めたうえで、Unity の環境ではこの規則で伝える。ほかの検査は続ける。
/// </para>
/// <para>
/// 解析しているファイルそのものは先頭に、取り込んだヘッダは <c>#include</c> の行に報告する。
/// ヘッダを直すのは利用者でも、どのシェーダーが壊れるかは取り込む側で分かる。
/// 利用者が書いたファイルの <c>#include</c> だけを報告する。
/// </para>
/// </remarks>
internal sealed class Utf16FileAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [UnityRuleDescriptors.Utf16File];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (compilation.Environment is not UnityHlslEnvironment)
        {
            return;
        }

        if (compilation.Text.IsUtf16)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UnityRuleDescriptors.Utf16File,
                Location.Create(compilation.Text, new TextSpan(0, 0)),
                "このファイルは UTF-16 で保存されています。Unity は UTF-16 のファイルを読めず (BOM があっても)、シェーダーがエラーになります。"
                + "UTF-8 で保存し直してください。"));
        }

        // 同じ #include の行が Pass ごとに現れる。位置で 1 つにまとめる。
        HashSet<(string File, int Start)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (IncludeReference include in program.Tree.PreprocessResult.Includes)
            {
                if (!include.IsUtf16
                    || !compilation.IsUserFile(include.Location.FilePath)
                    || !reported.Add((include.Location.FilePath, include.Location.Span.Start)))
                {
                    continue;
                }

                string name = Path.GetFileName(include.ResolvedFilePath ?? include.Path);

                context.ReportDiagnostic(Diagnostic.Create(
                    UnityRuleDescriptors.Utf16File,
                    include.Location,
                    $"取り込む '{name}' は UTF-16 で保存されています。Unity は UTF-16 のファイルを読めず (BOM があっても)、このシェーダーがエラーになります。"
                    + $"'{name}' を UTF-8 で保存し直してください。"));
            }
        }
    }
}
