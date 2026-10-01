using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Text;
using Shaderlyn.ShaderLab.Syntax;

namespace Shaderlyn.Rules;

/// <summary>
/// 解析の対象外のコードブロックがあることを伝える (SL0005)。
/// </summary>
/// <remarks>
/// <b>GLSLPROGRAM / GLSLINCLUDE の中は読まない。</b>
/// 何も伝えないと、検査して問題が無かったのと区別が付かない。
/// </remarks>
internal sealed class UnanalyzedBlockAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ShaderLabRuleDescriptors.UnanalyzedBlock];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
        => context.RegisterNodeAction<ProgramBlockSyntax>(AnalyzeBlock);

    /// <summary>コードブロック 1 つを見る。</summary>
    /// <param name="context">解析コンテキスト。</param>
    private static void AnalyzeBlock(NodeAnalysisContext<ProgramBlockSyntax> context)
    {
        ProgramBlockSyntax block = context.Node;

        if (block.Delimiter is not { Language: ProgramBlockLanguage.Glsl } delimiter)
        {
            return;
        }

        // 開始キーワードだけを指す。ブロック全体を指すと、エディタで中身がまるごと波線になる。
        context.ReportDiagnostic(
            ShaderLabRuleDescriptors.UnanalyzedBlock,
            new TextSpan(block.Token.Span.Start, delimiter.StartKeyword.Length),
            delimiter.StartKeyword);
    }
}
