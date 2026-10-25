using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Core.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// あるシンボルが定義されている構成でのみ禁じられる関数を報告する。
/// </summary>
/// <remarks>
/// <para>
/// <b>構成の条件の掛け方は API に任せる。</b>
/// <see cref="NodeActionOptions.When"/> で、その構成に存在しうる呼び出しだけを受け取る。
/// <c>#ifdef</c> の外の呼び出しも、その構成で実行されるので報告する。<c>#ifndef</c> の中の呼び出しは受け取らない。
/// </para>
/// <para>
/// 報告は <see cref="ShaderNodeAnalysisContext{TNode}.ReportWhen(SymbolCondition, DiagnosticDescriptor, TextSpan, object?[])"/> で出す。
/// 木の構成を掛け、ありえない構成を除き、同じ位置の報告を 1 件にまとめて、メッセージの <c>{0}</c> に構成を入れる。
/// </para>
/// </remarks>
public sealed class BannedUnderSymbolAnalyzer(string keyword, string function) : ShaderRuleAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [CookbookRules.BannedUnderSymbol];

    protected override void InitializeShader(ShaderAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<InvocationExpressionSyntax>(
            c =>
            {
                if (c.Node.Target is IdentifierExpressionSyntax name
                    && string.Equals(name.Name, function, StringComparison.Ordinal))
                {
                    // 構成：<keyword> のとき、関数 '<function>' は使えません。
                    c.ReportWhen(SymbolCondition.Always, CookbookRules.BannedUnderSymbol, name.Span, function);
                }
            },
            new NodeActionOptions { When = $"defined({keyword})" });
    }
}
