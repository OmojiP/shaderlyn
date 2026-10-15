using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Rules;
using Shaderlyn.Semantics;

namespace Shaderlyn.Unity.Rules;

/// <summary>
/// ヘッダの中の取り込みを、ヘッダからではなく解析しているシェーダーからの相対で見つけたことを報告する (UN1058)。
/// </summary>
/// <remarks>
/// <para>
/// 取り込みは見つかっているので、解析はそのまま続ける (Unity と同じく、そのファイルを読む)。
/// </para>
/// <para>
/// 直し方は一意に決まる。ヘッダから、見つけたファイルへの相対パスである。<see cref="Diagnostic.SuggestedReplacement"/> に添える。
/// 利用者が書いたヘッダの <c>#include</c> だけを報告する (Unity や外部パッケージのヘッダは直せない)。
/// </para>
/// </remarks>
internal sealed class FallbackIncludeAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [UnityRuleDescriptors.IncludeResolvedFromShader];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        // シェーダーから引き直すのは Unity だけである (UnityHlslEnvironment.PrepareUnit)。
        if (compilation.Environment is not UnityHlslEnvironment)
        {
            return;
        }

        // 同じ #include が Pass ごとに現れる。位置で 1 つにまとめる。
        HashSet<(string File, TextSpan Span)> reported = [];

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            foreach (IncludeReference include in program.Tree.PreprocessResult.Includes)
            {
                if (!include.IsResolvedFromFallback
                    || include.ResolvedFilePath is not { } resolved
                    || !compilation.IsUserFile(include.Location.FilePath)
                    || !reported.Add((include.Location.FilePath, include.Location.Span)))
                {
                    continue;
                }

                string relative = RelativePath(include.Location.FilePath, resolved);

                context.ReportDiagnostic(
                    Diagnostic.Create(UnityRuleDescriptors.IncludeResolvedFromShader, include.Location, include.Path, relative)
                        .WithSuggestedReplacement($"\"{relative}\""));
            }
        }
    }

    /// <summary>取り込みを書いたファイルから、見つけたファイルへの相対パス (区切りは <c>/</c>)。</summary>
    private static string RelativePath(string includingFile, string resolved)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(includingFile)) ?? string.Empty;
        return Path.GetRelativePath(directory, Path.GetFullPath(resolved)).Replace('\\', '/');
    }
}
