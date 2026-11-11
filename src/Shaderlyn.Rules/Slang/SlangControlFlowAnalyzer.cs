using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Slang;

/// <summary>
/// Slang の switch と break / continue の書き方 (SG0321) と、条件の中の代入 (SG0343) を検査する。
/// </summary>
/// <remarks>
/// <para>
/// SG0321: <c>break</c> はループか <c>switch</c> の中 (E30003)、<c>continue</c> はループの中 (E30004) にだけ書ける。
/// 1 つの <c>switch</c> の 2 つ目の <c>default</c> (E30602) と、値の同じ <c>case</c> (E30605) は誤り。値は、両方が整数の定数に決まるときだけ比べる
/// (列挙子は値で比べるので、<c>F.X</c> と <c>F.Y</c> が同じ値なら重なる)。<c>switch</c> の値は整数か列挙型でなければならない (E30607)。
/// </para>
/// <para>
/// SG0343: <c>if</c>・<c>while</c>・<c>for</c>・<c>do</c> の条件に、括弧で包まない代入 (<c>if (a = 0)</c>) は書けない (E30057)。
/// <c>if ((a = 0))</c> と <c>?:</c> の条件は通る。
/// </para>
/// </remarks>
internal sealed class SlangControlFlowAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesSlang => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [SlangRuleDescriptors.SwitchStatement, SlangRuleDescriptors.AssignmentInCondition];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsSlang)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            SlangConstants constants = SlangConstants.Of(program.Tree.Root);
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            void Report(DiagnosticDescriptor descriptor, ShaderSyntaxToken at, string message)
            {
                if (compilation.IsReportable(at) && at.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(descriptor, location, message));
                }
            }

            foreach (ShaderSyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                ExpressionSyntax? condition = node switch
                {
                    IfStatementSyntax statement => statement.Condition,
                    WhileStatementSyntax statement => statement.Condition,
                    DoWhileStatementSyntax statement => statement.Condition,
                    ForStatementSyntax statement => statement.Condition,
                    _ => null,
                };

                if (condition is AssignmentExpressionSyntax assignment)
                {
                    Report(SlangRuleDescriptors.AssignmentInCondition, assignment.OperatorToken,
                        "条件に代入は書けません。比べるなら '==' を、代入した値を使うなら括弧で包んでください ((a = b))。");
                }

                switch (node)
                {
                    case JumpStatementSyntax { Keyword.Text: "break" } jump when !Inside(jump, loopOrSwitch: true):
                        Report(SlangRuleDescriptors.SwitchStatement, jump.Keyword, "break はループか switch の中にだけ書けます。");
                        break;

                    case JumpStatementSyntax { Keyword.Text: "continue" } jump when !Inside(jump, loopOrSwitch: false):
                        Report(SlangRuleDescriptors.SwitchStatement, jump.Keyword, "continue はループの中にだけ書けます。");
                        break;

                    case SwitchStatementSyntax statement:
                        CheckSwitch(statement, constants, binder, Report);
                        break;
                }
            }
        }
    }

    private static void CheckSwitch(SwitchStatementSyntax statement, SlangConstants constants, ExpressionTypeBinder binder, Action<DiagnosticDescriptor, ShaderSyntaxToken, string> report)
    {
        if (binder.GetTypesByConfiguration(statement.Expression) is [{ Type: { } type }]
            && binder.TypeSystem.TryDescribeNumeric(type, out ShaderNumericShape shape)
            && (shape.Kind != ShaderNumericKind.Scalar || shape.BaseName is "float" or "double" or "half" or "min16float" or "min10float"))
        {
            report(SlangRuleDescriptors.SwitchStatement, statement.SwitchKeyword,
                $"switch の値は整数か列挙型でなければなりません (今は {(shape.Kind == ShaderNumericKind.Scalar ? shape.BaseName : type)})。");
        }

        bool defaultSeen = false;
        HashSet<long> values = [];

        foreach (SwitchLabelStatementSyntax label in statement.Statements.OfType<SwitchLabelStatementSyntax>())
        {
            if (label.Value is null)
            {
                if (defaultSeen)
                {
                    report(SlangRuleDescriptors.SwitchStatement, label.Keyword, "1 つの switch に default は 1 つしか書けません。");
                }

                defaultSeen = true;
            }
            else if (constants.Evaluate(label.Value) is { } value && !values.Add(value))
            {
                report(SlangRuleDescriptors.SwitchStatement, label.Keyword, $"case の値 {value} が、同じ switch の前の case と重なっています。");
            }
        }
    }

    /// <summary>関数の中で、ループ (と、<paramref name="loopOrSwitch"/> なら switch) の中にあるか。分からない書き方の中にあれば、中にあるものとする。</summary>
    private static bool Inside(ShaderSyntaxNode node, bool loopOrSwitch)
    {
        for (ShaderSyntaxNode? current = node.Parent as ShaderSyntaxNode; current is not null; current = current.Parent as ShaderSyntaxNode)
        {
            switch (current)
            {
                case ForStatementSyntax or WhileStatementSyntax or DoWhileStatementSyntax:
                    return true;

                case SwitchStatementSyntax when loopOrSwitch:
                    return true;

                case IncompleteStatementSyntax or IncompleteExpressionSyntax or IncompleteDeclarationSyntax:
                    return true;

                case FunctionDeclarationSyntax:
                    return false;
            }
        }

        return true;
    }
}
