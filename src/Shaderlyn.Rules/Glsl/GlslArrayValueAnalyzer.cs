using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 配列の値の使い方を検査する (GL0301 / GL0403)。glslang 16.6.0 の ParseHelper.cpp の
/// <c>specializationCheck</c> (「can't use with types containing arrays sized with a specialization constant」) と、
/// <c>executeInitializer</c> の「array initializer must be sized」「null initializers can't size unsized arrays」を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 特殊化の定数 (<c>layout(constant_id = …) const</c>) で大きさを決めた配列は、初期値にも、代入 (<c>=</c>) にも、比較 (<c>==</c> / <c>!=</c>) にも使えない。
/// </para>
/// <para>
/// 大きさを書かない配列の変数の初期値は、大きさの決まった配列でなければならない。大きさを書かない配列 (と実行時に大きさの決まるブロックのメンバー) と、
/// 空の初期化子 (<c>{ }</c>) では大きさを決められない。
/// </para>
/// </remarks>
internal sealed class GlslArrayValueAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.WrongOperandTypes, GlslRuleDescriptors.TypeNotAllowedHere];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, (GlslRequirements Requirement, HashSet<string> Specs)?> states = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (VariableDeclaratorSyntax { Initializer: not null } or AssignmentExpressionSyntax or BinaryExpressionSyntax))
            {
                continue;
            }

            if (!states.TryGetValue(program, out var state))
            {
                state = states[program] = GlslRequirements.Create(context, compilation, program, reported) is { } created
                    ? (created, CollectSpecConstants(program))
                    : null;
            }

            if (state is not { } found)
            {
                continue;
            }

            (GlslRequirements requirement, HashSet<string> specs) = found;
            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            switch (node)
            {
                case VariableDeclaratorSyntax { Initializer: { } initializer } declarator:
                    if (specs.Count > 0 && (IsSpecSized(declarator, specs) || IsSpecSizedValue(binder, initializer, specs)))
                    {
                        requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, declarator.NameToken, declarator.Name, "特殊化の定数で大きさを決めた配列は、初期値に使えません。");
                    }
                    else if (IsOuterUnsized(declarator) && initializer is InitializerListExpressionSyntax { Elements.IsEmpty: true })
                    {
                        requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, declarator.NameToken, declarator.Name, "空の初期化子 ({ }) では、大きさを書かない配列の大きさを決められません。");
                    }
                    else if (IsOuterUnsized(declarator) && IsUnsizedValue(binder, initializer))
                    {
                        requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, declarator.NameToken, declarator.Name, "大きさを書かない配列の初期値は、大きさの決まった配列でなければなりません。");
                    }

                    break;

                case AssignmentExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsToken } assignment
                    when specs.Count > 0 && (IsSpecSizedValue(binder, assignment.Left, specs) || IsSpecSizedValue(binder, assignment.Right, specs)):
                    requirement.Report(GlslRuleDescriptors.WrongOperandTypes, assignment.OperatorToken, "特殊化の定数で大きさを決めた配列は、代入できません。");
                    break;

                case BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken } binary
                    when specs.Count > 0 && (IsSpecSizedValue(binder, binary.Left, specs) || IsSpecSizedValue(binder, binary.Right, specs)):
                    requirement.Report(GlslRuleDescriptors.WrongOperandTypes, binary.OperatorToken, $"特殊化の定数で大きさを決めた配列は、'{binary.OperatorToken.Text}' で比べられません。");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>特殊化の定数の名前。</summary>
    private static HashSet<string> CollectSpecConstants(AnalyzedProgram program)
        => [.. program.Tree.Root.ChildNodes().OfType<VariableDeclarationSyntax>()
            .Where(d => GlslLayoutQualifier.Read(d.ModifierTokens).Any(e => e.Name.Text == "constant_id"))
            .SelectMany(d => d.Variables.Select(v => v.Name))];

    /// <summary>配列の大きさに特殊化の定数を書いた宣言か。</summary>
    private static bool IsSpecSized(VariableDeclaratorSyntax declarator, HashSet<string> specs)
        => declarator.ArrayRankTokens.Any(t => t.Kind == ShaderSyntaxKind.IdentifierToken && specs.Contains(t.Text))
           || declarator.Parent is VariableDeclarationSyntax { Type.ArrayRanks: var ranks } && ranks.Any(t => t.Kind == ShaderSyntaxKind.IdentifierToken && specs.Contains(t.Text));

    /// <summary>添字を付けない、大きさに特殊化の定数を書いた配列の変数か。</summary>
    private static bool IsSpecSizedValue(ExpressionTypeBinder binder, ExpressionSyntax expression, HashSet<string> specs) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsSpecSizedValue(binder, parenthesized.Expression, specs),
        IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax declarator }] && IsSpecSized(declarator, specs),
        _ => false,
    };

    /// <summary>一番外の次元の大きさを書かない配列の宣言か。</summary>
    private static bool IsOuterUnsized(VariableDeclaratorSyntax declarator)
        => declarator.ArrayRankTokens is [{ Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.CloseBracketToken }, ..];

    /// <summary>大きさを書かない配列の変数か、ブロックの大きさを書かない配列のメンバーか (添字を付けないもの)。</summary>
    private static bool IsUnsizedValue(ExpressionTypeBinder binder, ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => IsUnsizedValue(binder, parenthesized.Expression),
        IdentifierExpressionSyntax identifier => binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax declarator }]
                                                 && IsOuterUnsized(declarator) && declarator.Initializer is null && !IsResized(declarator),
        MemberAccessExpressionSyntax member => FindBlockMember(member) is { } declarator && IsOuterUnsized(declarator),
        _ => false,
    };

    /// <summary>同じ範囲で、大きさを書いて宣言し直しているか。</summary>
    private static bool IsResized(VariableDeclaratorSyntax declarator)
        => declarator.Parent?.Parent is { } scope
           && scope.ChildNodes().OfType<VariableDeclarationSyntax>().SelectMany(d => d.Variables).Any(v => v != declarator && v.Name == declarator.Name);

    /// <summary>名前のあるブロックのインスタンスのメンバーの宣言子。</summary>
    private static VariableDeclaratorSyntax? FindBlockMember(MemberAccessExpressionSyntax member)
    {
        if (member.Target is not IdentifierExpressionSyntax instance
            || member.Ancestors().OfType<CompilationUnitSyntax>().FirstOrDefault() is not { } unit)
        {
            return null;
        }

        VariableDeclaratorSyntax[] found =
        [
            .. unit.ChildNodes()
                .Where(n => n is StructDeclarationSyntax { Keyword.Text: not "struct" } or InterfaceBlockDeclarationSyntax
                            && n.ChildNodes().OfType<VariableDeclaratorSyntax>().Any(v => v.Name == instance.Name))
                .SelectMany(n => n.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>())
                .Where(v => v.Name == member.Name && v.Parent is VariableDeclarationSyntax),
        ];

        return found is [var only] ? only : null;
    }
}
