using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Unity.Rules;

/// <summary>
/// .shader の先頭に重なった BOM を報告する (UN0002)。
/// </summary>
/// <remarks>
/// 1 つ目の BOM は読み込みで取り除いているので、テキストの先頭に残っている U+FEFF が重なった分である。
/// 構文解析は、最初のトークンより前の U+FEFF を空白として読む (Unity と同じく、構文エラーにはしない)。
/// </remarks>
internal sealed class DuplicatedByteOrderMarkAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ShaderLabRuleDescriptors.DuplicatedByteOrderMark];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<ShaderLabCompilationUnitSyntax>(Analyze);

    /// <summary>ファイルの先頭を見る。</summary>
    /// <param name="context">解析コンテキスト。</param>
    private static void Analyze(NodeAnalysisContext<ShaderLabCompilationUnitSyntax> context)
    {
        SourceText text = context.Unit.Text;
        int count = 0;

        while (count < text.Length && text[count] == '﻿')
        {
            count++;
        }

        if (count > 0)
        {
            // 取り除いた 1 つ目も数える。残っている分を消せば、BOM は 1 つになる (Unity が読める形)。
            TextSpan span = new(0, count);
            context.ReportDiagnostic(
                Diagnostic.Create(ShaderLabRuleDescriptors.DuplicatedByteOrderMark, Location.Create(text, span), count + 1)
                    .WithFix(DiagnosticFix.Replace("重なった BOM を消す", span, string.Empty)));
        }
    }
}
