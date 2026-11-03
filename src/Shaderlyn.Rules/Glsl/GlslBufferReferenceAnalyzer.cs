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
/// バッファの参照 (<c>layout(buffer_reference) buffer T { … };</c> の型 <c>T</c>) の演算と修飾子を検査する (GL0301 / GL0101)。
/// glslang 16.6.0 の ParseHelper.cpp と Intermediate.cpp の参照の型の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 参照の型には <c>==</c> と <c>!=</c> を使えない。足し引き (<c>GL_EXT_buffer_reference2</c>) は、参照と整数の <c>+</c>、参照から整数の <c>-</c>、
/// 整数と参照の <c>+</c>、<c>+=</c> と <c>-=</c> の整数、参照どうしの <c>-</c> (差) だけで、参照どうしの <c>+</c> と <c>+=</c> / <c>-=</c>、整数から参照を引くものは書けない。
/// 大きさの決まっていない配列を含むブロックの参照は足し引きできない。
/// </para>
/// <para>参照の型の変数には <c>const</c> を書けない。</para>
/// </remarks>
internal sealed class GlslBufferReferenceAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [GlslRuleDescriptors.WrongOperandTypes, GlslRuleDescriptors.QualifierNotAllowed, GlslRuleDescriptors.NotAvailable, GlslRuleDescriptors.UnsizedArrayUse];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, (GlslRequirements Requirement, Dictionary<string, bool> References)?> checkers = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (BinaryExpressionSyntax or AssignmentExpressionSyntax or VariableDeclarationSyntax or ElementAccessExpressionSyntax or InvocationExpressionSyntax or CastExpressionSyntax))
            {
                continue;
            }

            if (!checkers.TryGetValue(program, out var checker))
            {
                checker = checkers[program] = GlslRequirements.Create(context, compilation, program, reported) is { } created
                    ? (created, CollectReferences(program))
                    : null;
            }

            if (checker is not { } found)
            {
                continue;
            }

            (GlslRequirements requirement, Dictionary<string, bool> references) = found;

            ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

            switch (node)
            {
                case VariableDeclarationSyntax declaration when references.ContainsKey(declaration.Type.Name) && declaration.HasModifier("const")
                                                              && declaration.ModifierTokens.FirstOrDefault(t => t.Text == "const") is { } constant:
                    requirement.Report(GlslRuleDescriptors.QualifierNotAllowed, constant, "参照の型の変数には const を書けません。");
                    break;

                // 別の参照の型の値での初期化。
                case VariableDeclarationSyntax initialized when references.ContainsKey(initialized.Type.Name)
                                                              && initialized.Variables.FirstOrDefault(v => v.Initializer is { } value && ReferenceOf(binder, references, value) is { } source && source != initialized.Type.Name) is { Initializer: { } other } variable:
                    requirement.Report(GlslRuleDescriptors.WrongOperandTypes, variable.NameToken,
                        $"参照の型 {ReferenceOf(binder, references, other)} の値で、別の参照の型 {initialized.Type.Name} の変数を初期化できません。{initialized.Type.Name}(…) と書いて変えてください。");
                    break;

                // 参照の型の値 (変数とメンバー。buffer_reference のブロックのインスタンスそのものは除く) の先のブロックの大きさの決まっていない配列には length() を使えない (glslang の「array must be declared with a size before using this method」)。
                case InvocationExpressionSyntax { Target: MemberAccessExpressionSyntax { Name: "length", Target: MemberAccessExpressionSyntax { Target: var holder } array } method }
                    when (holder is MemberAccessExpressionSyntax || holder is IdentifierExpressionSyntax local && binder.ResolveName(local).Candidates is [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax } }])
                         && ReferenceOf(binder, references, holder) is { } referenced && references[referenced] && IsUnsizedMember(program, referenced, array.Name):
                    requirement.Report(GlslRuleDescriptors.UnsizedArrayUse, method.NameToken, array.Name, $"length() で長さを求められません (参照 {referenced} の先のブロックの配列です)");
                    break;

                // GL_NV_explicit_typecast の型の変換 ((T) x)。構造体へは変換できず、uvec2 から参照への変換には GL_EXT_buffer_reference_uvec2 が要る。
                case CastExpressionSyntax cast when program.Structs.Any(t => t.Name == cast.Type.Name && t.Keyword.Text == "struct"):
                    requirement.Report(GlslRuleDescriptors.WrongOperandTypes, cast.OpenParen, $"型の変換 ((T) x) は、スカラー・ベクトル・行列・バッファの参照にだけ書けます (構造体 {cast.Type.Name} には書けません)。");
                    break;

                case CastExpressionSyntax cast when references.ContainsKey(cast.Type.Name) && binder.GetTypesByConfiguration(cast.Operand) is [{ Type: "uvec2" }]:
                    requirement.RequireExtensions(cast.OpenParen, ["GL_EXT_buffer_reference_uvec2"], "uvec2 から参照の型への変換");
                    break;

                case BinaryExpressionSyntax binary:
                    Check(requirement, binder, references, binary.OperatorToken, binary.Left, binary.Right, compound: false);
                    break;

                // 別の参照の型への代入 (参照の型は変換しない。値の作成 T(x) で変える)。
                case AssignmentExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsToken } assignment
                    when ReferenceOf(binder, references, assignment.Left) is { } target && ReferenceOf(binder, references, assignment.Right) is { } source && target != source:
                    requirement.Report(GlslRuleDescriptors.WrongOperandTypes, assignment.OperatorToken, $"参照の型 {source} を、別の参照の型 {target} に代入できません。{target}({source} の値) と書いて変えてください。");
                    break;

                // 参照の添字 (GL_EXT_buffer_reference2。大きさの決まっていない配列を含むブロックの参照は引けない)。
                case ElementAccessExpressionSyntax element when ReferenceOf(binder, references, element.Target) is { } indexed:
                    if (references[indexed])
                    {
                        requirement.Report(GlslRuleDescriptors.WrongOperandTypes, element.OpenBracket, $"大きさの決まっていない配列を含むブロックの参照 ({indexed}) には、[ ] を書けません。");
                    }
                    else
                    {
                        requirement.RequireExtensions(element.OpenBracket, ["GL_EXT_buffer_reference2"], "参照の [ ]");
                    }

                    break;

                case AssignmentExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken } assignment:
                    Check(requirement, binder, references, assignment.OperatorToken, assignment.Left, assignment.Right, compound: true);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>参照の型を含む演算。</summary>
    private static void Check(
        GlslRequirements requirement,
        ExpressionTypeBinder binder,
        Dictionary<string, bool> references,
        ShaderSyntaxToken op,
        ExpressionSyntax leftExpression,
        ExpressionSyntax rightExpression,
        bool compound)
    {
        string? left = ReferenceOf(binder, references, leftExpression);
        string? right = ReferenceOf(binder, references, rightExpression);

        if (left is null && right is null)
        {
            return;
        }

        string text = op.Text;

        switch (op.Kind)
        {
            case ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken:
                requirement.Report(GlslRuleDescriptors.WrongOperandTypes, op, $"参照の型には演算子 '{text}' を使えません。");
                return;

            case ShaderSyntaxKind.PlusToken or ShaderSyntaxKind.MinusToken or ShaderSyntaxKind.PlusEqualsToken or ShaderSyntaxKind.MinusEqualsToken:
                string reference = left ?? right!;

                // 参照どうしは、引き算 (差は int64_t) だけが書ける。
                if (left is not null && right is not null && (compound || op.Kind != ShaderSyntaxKind.MinusToken))
                {
                    requirement.Report(GlslRuleDescriptors.WrongOperandTypes, op, $"参照の型どうしに演算子 '{text}' は使えません。参照に足し引きできるのは整数です。");
                }
                else if (left is null && op.Kind == ShaderSyntaxKind.MinusToken && !compound)
                {
                    requirement.Report(GlslRuleDescriptors.WrongOperandTypes, op, "整数から参照を引けません。");
                }
                else if (references[reference])
                {
                    requirement.Report(GlslRuleDescriptors.WrongOperandTypes, op, $"大きさの決まっていない配列を含むブロックの参照 ({reference}) は足し引きできません。");
                }

                return;

            default:
                return;
        }
    }

    /// <summary>式が参照の型の名前を指すなら、その型の名前。</summary>
    private static string? ReferenceOf(ExpressionTypeBinder binder, Dictionary<string, bool> references, ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => ReferenceOf(binder, references, parenthesized.Expression),
        IdentifierExpressionSyntax identifier when binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax { IsArray: false, Parent: VariableDeclarationSyntax declaration } }]
            && references.ContainsKey(declaration.Type.Name) => declaration.Type.Name,
        _ => binder.GetTypesByConfiguration(expression) is [{ Type: { } name }] && references.ContainsKey(name) ? name : null,
    };

    /// <summary>参照の型のブロックの、その名前のメンバーが大きさを書かない配列か。</summary>
    private static bool IsUnsizedMember(AnalyzedProgram program, string reference, string member)
        => program.Tree.Root.DescendantNodesAndSelf()
            .Where(n => n is InterfaceBlockDeclarationSyntax { Name: var name } && name == reference || n is StructDeclarationSyntax { NameToken.Text: var structName } && structName == reference)
            .SelectMany(n => n.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>())
            .Any(v => v.Name == member && v.ArrayRankTokens is [.., { Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.CloseBracketToken }]);

    /// <summary>参照の型の名前と、そのブロックが大きさの決まっていない配列を含むか。</summary>
    private static Dictionary<string, bool> CollectReferences(AnalyzedProgram program)
    {
        Dictionary<string, bool> references = new(StringComparer.Ordinal);

        foreach (ShaderSyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case InterfaceBlockDeclarationSyntax block when block.Modifiers.Any(t => t.Text == "buffer_reference"):
                    references[block.Name] = block.Members.OfType<VariableDeclarationSyntax>().Any(HasUnsizedArray);
                    break;

                case StructDeclarationSyntax { NameToken: { } name } block when block.Keyword.Text != "struct" && block.Modifiers.Any(t => t.Text == "buffer_reference"):
                    references[name.Text] = block.Fields.Any(HasUnsizedArray);
                    break;

                default:
                    break;
            }
        }

        return references;

        static bool HasUnsizedArray(VariableDeclarationSyntax member)
            => member.Variables.Any(v => v.IsArray && !v.TryGetArrayLength(out _) && v.ArrayRankTokens is [.., { Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.CloseBracketToken }]);
    }
}
