using System.Collections.Immutable;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Analysis;

namespace Shaderlyn.Cookbook;

/// <summary>
/// 使ってはいけない演算子を報告する。
/// </summary>
/// <remarks>
/// <para>
/// <b>演算子は 4 つのノードに分かれて現れる。</b>
/// 二項演算・代入・前置・後置のどれかを忘れると、
/// 「<c>%</c> を禁じたのに <c>%=</c> が素通りする」という穴になる。
/// </para>
/// <para>
/// <b>複合代入は同じ演算として扱う。</b>
/// <c>%</c> を渡せば <c>%=</c> も報告する。
/// <b>末尾の等号を自分で落としてはいけない。</b>
/// <c>&lt;=</c> まで巻き込む。複合代入かどうかは
/// <see cref="HlslSyntaxFacts.IsCompoundAssignment"/> が答える。
/// </para>
/// </remarks>
public sealed class BannedOperatorAnalyzer(params string[] banned) : HlslRuleAnalyzer
{
    private readonly ImmutableHashSet<string> _banned = [.. banned];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CookbookRules.BannedOperator];

    protected override void InitializeHlsl(HlslAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterNodeAction<BinaryExpressionSyntax>(c => Check(c, c.Node.OperatorToken));
        context.RegisterNodeAction<AssignmentExpressionSyntax>(c => Check(c, c.Node.OperatorToken));
        context.RegisterNodeAction<PrefixUnaryExpressionSyntax>(c => Check(c, c.Node.OperatorToken));
        context.RegisterNodeAction<PostfixUnaryExpressionSyntax>(c => Check(c, c.Node.OperatorToken));
    }

    /// <summary>演算子のトークンを照合して報告する。</summary>
    /// <typeparam name="TNode">対象ノードの型。</typeparam>
    /// <param name="context">解析コンテキスト。</param>
    /// <param name="operatorToken">演算子のトークン。</param>
    private void Check<TNode>(HlslNodeAnalysisContext<TNode> context, HlslSyntaxToken operatorToken)
        where TNode : HlslSyntaxNode
    {
        string text = operatorToken.Text;

        // 複合代入なら、それが行う演算の綴りでも照合する。
        string root = HlslSyntaxFacts.IsCompoundAssignment(operatorToken.Kind) ? text[..^1] : text;

        if (!_banned.Contains(text) && !_banned.Contains(root))
        {
            return;
        }

        if (!context.Compilation.IsReportable(operatorToken))
        {
            return;
        }

        context.ReportDiagnostic(CookbookRules.BannedOperator, operatorToken.Span, text);
    }
}
