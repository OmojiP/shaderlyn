using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.Hlsl.Preprocessing;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 取り込んではいけないヘッダを報告する。
/// </summary>
/// <remarks>
/// <para>
/// <b>利用者のファイルに書かれた <c>#include</c> だけを見る。</b>
/// このシェーダーと、利用者が書いて取り込んだヘッダ (共通の .hlsl) である。
/// Unity や外部パッケージのヘッダが取り込んだものは、利用者に直しようがない。
/// </para>
/// <para>
/// <b>同じ 1 行が複数のブロックに現れる。</b>
/// <c>HLSLINCLUDE</c> に書いた取り込みは Pass ごとの解析に入るので、
/// 位置で重複を落とさないと Pass の数だけ報告することになる。
/// </para>
/// </remarks>
public sealed class BannedIncludeAnalyzer(params string[] banned) : DiagnosticAnalyzer
{
    private readonly ImmutableHashSet<string> _banned = banned.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.BannedInclude];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterSyntaxTreeAction(c =>
        {
            if (c.Unit.GetModel<ShaderCompilation>() is not { } compilation)
            {
                return;
            }

            HashSet<(string FilePath, TextSpan Span)> reported = [];

            foreach (AnalyzedProgram program in compilation.Programs)
            {
                foreach (IncludeReference include in program.Tree.PreprocessResult.Includes)
                {
                    if (!compilation.IsUserFile(include.Location.FilePath)
                        || !CookbookIncludes.Matches(_banned, include.Path)
                        || !reported.Add((include.Location.FilePath, include.Location.Span)))
                    {
                        continue;
                    }

                    // このシェーダーの位置は元のテキストに付ける (位置の記録はマスクした複製の上にある)。
                    // ヘッダに書かれていれば、ヘッダを指す位置をそのまま使う。
                    if (string.Equals(include.Location.FilePath, compilation.Text.FilePath, StringComparison.Ordinal))
                    {
                        c.ReportDiagnostic(CookbookRules.BannedInclude, include.Location.Span, include.Path);
                    }
                    else
                    {
                        c.ReportDiagnostic(Diagnostic.Create(CookbookRules.BannedInclude, include.Location, include.Path));
                    }
                }
            }
        });
    }
}
