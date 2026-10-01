using System.Collections.Immutable;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 使ってはいけないマクロを報告する。
/// </summary>
/// <remarks>
/// <b>マクロは展開後の構文木には残らない。</b>
/// <c>UNITY_MATRIX_MVP</c> は展開されて跡形も無く消えるので、
/// 構文木を歩いても見つからない。展開前のトークン列を見る。
/// </remarks>
public sealed class BannedMacroAnalyzer(params string[] banned) : DiagnosticAnalyzer
{
    private readonly ImmutableHashSet<string> _banned = [.. banned];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.BannedMacro];

    public override void Initialize(AnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterSyntaxTreeAction(c =>
        {
            if (c.Unit.GetModel<ShaderCompilation>() is not { } compilation)
            {
                return;
            }

            foreach (HlslSyntaxToken token in compilation.CodeTokens)
            {
                if (token.Kind == HlslSyntaxKind.IdentifierToken && _banned.Contains(token.Text))
                {
                    c.ReportDiagnostic(CookbookRules.BannedMacro, token.Span, token.Text);
                }
            }

            // 利用者が書いて取り込んだヘッダ (共通の .hlsl) も、展開前のトークンを見る。
            // これらのトークンはヘッダを指すので、位置はトークンから作る。
            foreach (HlslSyntaxToken token in compilation.UserIncludeCodeTokens)
            {
                if (token.Kind == HlslSyntaxKind.IdentifierToken && _banned.Contains(token.Text))
                {
                    c.ReportDiagnostic(Diagnostic.Create(CookbookRules.BannedMacro, token.GetLocation(), token.Text));
                }
            }
        });
    }
}
