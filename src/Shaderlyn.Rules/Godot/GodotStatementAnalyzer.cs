using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;

namespace Shaderlyn.Rules.Godot;

/// <summary>
/// Godot シェーダー言語の <c>switch</c> の文の組み立てと、<c>break</c> / <c>continue</c> の置き場所を検査する (GD0321)。
/// </summary>
/// <remarks>
/// <para>
/// Godot 4.7.2 の <c>ShaderLanguage::_parse_block</c> の <c>switch</c> / <c>case</c> / <c>default</c> / <c>break</c> / <c>continue</c> を写した。
/// <list type="bullet">
/// <item><c>switch</c> の式は <c>int</c> か <c>uint</c></item>
/// <item><c>case</c> / <c>default</c> は <c>switch</c> の波括弧の中にそのまま書く。<c>default</c> は 1 つだけ</item>
/// <item><c>case</c> の値は、<c>switch</c> の式と同じ型の整数のリテラル (<c>int</c> なら符号を付けられる) か、同じ型の定数の名前。同じ値は 2 度書けない</item>
/// <item><c>break</c> はループか <c>switch</c> の中にだけ書ける</item>
/// <item><c>continue</c> はループの中にだけ書ける。ループの中の <c>switch</c> の中にも書けない (Godot は <c>case</c> の中で <c>continue</c> を許さない)</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class GodotStatementAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGodot => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GodotRuleDescriptors.SwitchStatement];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGodot)
        {
            return;
        }

        HashSet<(string? File, int Start)> reported = [];
        Dictionary<AnalyzedProgram, ExpressionTypeBinder> binders = [];

        void Report(ShaderSyntaxToken token, string message)
        {
            if (compilation.IsReportable(token)
                && token.GetLocation() is { } location
                && reported.Add((location.FilePath, location.Span.Start)))
            {
                context.ReportDiagnostic(Diagnostic.Create(GodotRuleDescriptors.SwitchStatement, location, message));
            }
        }

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            switch (node)
            {
                case SwitchStatementSyntax statement:
                    if (!binders.TryGetValue(program, out ExpressionTypeBinder? binder))
                    {
                        binder = binders[program] = compilation.GetExpressionTypeBinder(program);
                    }

                    CheckSwitch(statement, binder, program.Tree.Root, Report);
                    break;

                case SwitchLabelStatementSyntax label when label.Parent is not SwitchStatementSyntax:
                    Report(label.Keyword, $"'{label.Keyword.Text}' は switch の波括弧の中にそのまま書きます。");
                    break;

                case JumpStatementSyntax { Keyword.Text: "break" } jump
                    when !jump.Ancestors().Any(a => a is SwitchStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoWhileStatementSyntax):
                    Report(jump.Keyword, "break はループか switch の中にだけ書けます。");
                    break;

                case JumpStatementSyntax { Keyword.Text: "continue" } jump
                    when jump.Ancestors().FirstOrDefault(a => a is SwitchStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoWhileStatementSyntax)
                        is null or SwitchStatementSyntax:
                    Report(jump.Keyword, "continue はループの中にだけ書けます (ループの中の switch の中にも書けません)。");
                    break;
            }
        }
    }

    /// <summary>switch の式の型と、case / default のラベルを調べる。</summary>
    private static void CheckSwitch(SwitchStatementSyntax statement, ExpressionTypeBinder binder, CompilationUnitSyntax root, Action<ShaderSyntaxToken, string> report)
    {
        string? type = binder.GetTypesByConfiguration(statement.Expression) is [{ Type: { } name }] ? name : null;

        if (type is not null and not ("int" or "uint"))
        {
            if (statement.Expression.FirstToken is { } first)
            {
                report(first, $"switch の式は int か uint でなければなりません (今は {type})。");
            }

            return;
        }

        bool hasDefault = false;
        HashSet<long> values = [];

        foreach (SwitchLabelStatementSyntax label in statement.Statements.OfType<SwitchLabelStatementSyntax>())
        {
            if (label.Value is null)
            {
                if (hasDefault)
                {
                    report(label.Keyword, "default は 1 つだけ書けます。");
                    return;
                }

                hasDefault = true;
                continue;
            }

            if (type is null)
            {
                continue;
            }

            if (!TryGetCaseValue(label.Value, type, binder, statement, root, out long? value))
            {
                if (label.Value.FirstToken is { } first)
                {
                    report(first, type == "uint"
                        ? "case の値は uint の定数 (1u など) でなければなりません。"
                        : "case の値は int の定数でなければなりません。");
                }

                return;
            }

            if (value is { } known && !values.Add(known))
            {
                if (label.Value.FirstToken is { } first)
                {
                    report(first, $"case の値 {known} を 2 度書いています。");
                }

                return;
            }
        }
    }

    /// <summary>case の値が Godot の書ける形かを調べ、分かれば値を返す。</summary>
    /// <returns>書ける形なら <see langword="true"/>。値は、定数の値が分からなければ <see langword="null"/>。</returns>
    private static bool TryGetCaseValue(ExpressionSyntax value, string type, ExpressionTypeBinder binder, SwitchStatementSyntax statement, CompilationUnitSyntax root, out long? result)
    {
        result = null;
        bool negative = false;

        // Godot は - を 1 つだけ読む (uint では符号を捨てる)。
        if (value is PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.MinusToken, Operand: LiteralExpressionSyntax } minus)
        {
            negative = true;
            value = minus.Operand;
        }

        switch (value)
        {
            case LiteralExpressionSyntax literal:
                if (literal.Token.Kind != ShaderSyntaxKind.NumericLiteralToken
                    || binder.GetTypesByConfiguration(literal) is not [{ Type: { } literalType }]
                    || literalType != type)
                {
                    return false;
                }

                if (GodotLiterals.TryParseInteger(literal.Token.Text, out long number))
                {
                    result = type == "int" && negative ? -number : number;
                }

                return true;

            case IdentifierExpressionSyntax identifier when !negative:
                if (binder.GetTypesByConfiguration(identifier) is not [{ Type: { } identifierType }]
                    || identifierType != type
                    || FindConstant(identifier.Name, statement, root) is not { } constant)
                {
                    return false;
                }

                if (constant.Initializer is { } initializer
                    && TryGetCaseValue(initializer, type, binder, statement, root, out long? constantValue))
                {
                    result = constantValue;
                }

                return true;

            default:
                return false;
        }
    }

    /// <summary>その名前の const の変数を、switch の外側の関数の中と、関数の外から探す。</summary>
    private static VariableDeclaratorSyntax? FindConstant(string name, SwitchStatementSyntax statement, CompilationUnitSyntax root)
    {
        IEnumerable<VariableDeclarationSyntax> locals = statement.Ancestors().OfType<FunctionDeclarationSyntax>().FirstOrDefault() is { Body: { } body }
            ? body.DescendantNodesAndSelf().OfType<LocalDeclarationStatementSyntax>().Select(l => l.Declaration).OfType<VariableDeclarationSyntax>()
            : [];

        return locals.Concat(root.Declarations.OfType<VariableDeclarationSyntax>())
            .Where(d => d.HasModifier("const"))
            .SelectMany(d => d.Variables)
            .FirstOrDefault(v => v.Name == name);
    }
}
