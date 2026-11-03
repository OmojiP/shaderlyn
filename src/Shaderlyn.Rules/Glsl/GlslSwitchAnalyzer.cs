using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// <c>switch</c> の文の組み立てと、<c>break</c> / <c>continue</c> の置き場所を検査する (GL0321)。
/// glslang 16.6.0 の glslang.y の <c>case_label</c> と <c>jump_statement</c>、ParseHelper.cpp の <c>wrapupSwitchSubsequence</c> と <c>addSwitch</c> を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// <c>case</c> / <c>default</c> は、<c>switch</c> の波括弧の中にそのまま書く。波括弧や <c>if</c> の中に入れたものと、<c>switch</c> の外のものは誤り。
/// 同じ値の <c>case</c> と 2 つ目の <c>default</c> も誤り (値は両方が定数に決まるときだけ比べる)。
/// </para>
/// <para>
/// 最初のラベルより前の文と、最後のラベルの後に文が無い <c>switch</c> も誤り。glslang はコードを作らない文 (初期値の無い宣言、空の文) を数えない。
/// 最後のラベルの後に文が無いものは、ES の 3.00 まで・3.20 から、デスクトップの 4.30 まで・4.60 からの誤りで、ほかのバージョンでは警告 (ここでは伝えない)。
/// 誤報告を避けるため、前の文はコードを作ると決まる文だけを数え、後ろの文はコードを作らないと決まる文だけのときに伝える。
/// </para>
/// <para>
/// <c>break</c> はループか <c>switch</c> の中、<c>continue</c> はループの中にだけ書ける。
/// </para>
/// </remarks>
internal sealed class GlslSwitchAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.SwitchStatement];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, (GlslRequirements Requirement, GlslConstantEvaluator Evaluator)?> checkers = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (SwitchStatementSyntax or SwitchLabelStatementSyntax or JumpStatementSyntax))
            {
                continue;
            }

            if (!checkers.TryGetValue(program, out var checker))
            {
                checker = checkers[program] =
                    GlslRequirements.Create(context, compilation, program, reported) is { } created
                    && compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types
                        ? (created, new GlslConstantEvaluator(compilation.GetExpressionTypeBinder(program), types.Builtins))
                        : null;
            }

            if (checker is not { } found)
            {
                continue;
            }

            (GlslRequirements requirement, GlslConstantEvaluator evaluator) = found;

            switch (node)
            {
                case SwitchStatementSyntax statement:
                    CheckSwitch(requirement, evaluator, statement);
                    break;

                case SwitchLabelStatementSyntax label when label.Parent is not SwitchStatementSyntax:
                    requirement.Report(
                        GlslRuleDescriptors.SwitchStatement,
                        label.Keyword,
                        HasAncestor<SwitchStatementSyntax>(label)
                            ? $"'{label.Keyword.Text}' は switch の波括弧の中にそのまま書かなければなりません。波括弧や if などの中には書けません。"
                            : $"'{label.Keyword.Text}' は switch の外には書けません。");
                    break;

                case JumpStatementSyntax { Keyword.Text: "break" } jump when !HasAncestor<ForStatementSyntax, WhileStatementSyntax, DoWhileStatementSyntax, SwitchStatementSyntax>(jump):
                    requirement.Report(GlslRuleDescriptors.SwitchStatement, jump.Keyword, "break はループか switch の中にだけ書けます。");
                    break;

                case JumpStatementSyntax { Keyword.Text: "continue" } jump when !HasAncestor<ForStatementSyntax, WhileStatementSyntax, DoWhileStatementSyntax, ForStatementSyntax>(jump):
                    requirement.Report(GlslRuleDescriptors.SwitchStatement, jump.Keyword, "continue はループの中にだけ書けます。");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary><c>switch</c> の中のラベルの重複と、最初のラベルの前・最後のラベルの後の文を見る。</summary>
    private static void CheckSwitch(GlslRequirements requirement, GlslConstantEvaluator evaluator, SwitchStatementSyntax statement)
    {
        bool labelSeen = false;
        bool defaultSeen = false;
        HashSet<double> values = [];
        int lastLabel = -1;

        for (int i = 0; i < statement.Statements.Length; i++)
        {
            StatementSyntax child = statement.Statements[i];

            if (child is not SwitchLabelStatementSyntax label)
            {
                if (!labelSeen && CertainlyMakesCode(child) && child.FirstToken is { } first)
                {
                    requirement.Report(GlslRuleDescriptors.SwitchStatement, first, "switch の最初の case / default より前に文は書けません。");
                    labelSeen = true;
                }

                continue;
            }

            labelSeen = true;
            lastLabel = i;

            if (label.Value is null)
            {
                if (!defaultSeen)
                {
                    defaultSeen = true;
                }
                else
                {
                    requirement.Report(GlslRuleDescriptors.SwitchStatement, label.Keyword, "1 つの switch に default は 1 つしか書けません。");
                }
            }
            else if (evaluator.Evaluate(label.Value) is { Kind: GlslConstantKind.Int or GlslConstantKind.Uint } value && !values.Add(value.Value))
            {
                requirement.Report(GlslRuleDescriptors.SwitchStatement, label.Keyword, $"case の値 {value.Integer} が重なっています。");
            }
        }

        bool error = requirement.IsEs
            ? requirement.Version <= 300 || requirement.Version >= 320
            : requirement.Version <= 430 || requirement.Version >= 460;

        if (lastLabel >= 0 && error && statement.Statements.Skip(lastLabel + 1).All(CertainlyMakesNoCode))
        {
            requirement.Report(GlslRuleDescriptors.SwitchStatement, statement.SwitchKeyword, "switch の最後の case / default の後に文がありません。");
        }
    }

    /// <summary>コードを作ると決まる文か。宣言は、const でない変数に初期値があるものだけ。</summary>
    private static bool CertainlyMakesCode(StatementSyntax statement) => statement switch
    {
        ExpressionStatementSyntax or ReturnStatementSyntax or JumpStatementSyntax
            or IfStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoWhileStatementSyntax or SwitchStatementSyntax => true,
        LocalDeclarationStatementSyntax { Declaration: VariableDeclarationSyntax declaration } =>
            !declaration.HasModifier("const") && declaration.Variables.Any(d => d.Initializer is not null),
        _ => false,
    };

    /// <summary>コードを作らないと決まる文か (空の文と、初期値の無い変数の宣言)。</summary>
    private static bool CertainlyMakesNoCode(StatementSyntax statement) => statement switch
    {
        EmptyStatementSyntax => true,
        LocalDeclarationStatementSyntax { Declaration: VariableDeclarationSyntax declaration } => declaration.Variables.All(d => d.Initializer is null),
        _ => false,
    };

    /// <summary>関数の中で、その種類の文の中にあるか。</summary>
    private static bool HasAncestor<T>(ShaderSyntaxNode node)
        where T : ShaderSyntaxNode
        => HasAncestor<T, T, T, T>(node);

    /// <summary>関数の中で、どれかの種類の文の中にあるか。</summary>
    private static bool HasAncestor<T1, T2, T3, T4>(ShaderSyntaxNode node)
        where T1 : ShaderSyntaxNode
        where T2 : ShaderSyntaxNode
        where T3 : ShaderSyntaxNode
        where T4 : ShaderSyntaxNode
    {
        for (ShaderSyntaxNode? current = node.Parent as ShaderSyntaxNode; current is not null; current = current.Parent as ShaderSyntaxNode)
        {
            if (current is T1 or T2 or T3 or T4)
            {
                return true;
            }

            if (current is FunctionDeclarationSyntax)
            {
                return false;
            }
        }

        return false;
    }
}
