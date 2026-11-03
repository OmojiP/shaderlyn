using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 文字列 (<c>GL_EXT_debug_printf</c> の <c>debugPrintfEXT</c>、<c>GL_EXT_shader_abort</c> の <c>abortEXT</c>) の実引数とリテラルを検査する (GL0306 / GL0315 / GL0302)。
/// glslang 16.6.0 の ParseHelper.cpp と Scan.cpp の文字列の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// <c>debugPrintfEXT</c> と <c>abortEXT</c> の 1 つ目の実引数は文字列のリテラルでなければならない (括弧で囲んだものも、定数の変数も書けない)。
/// <c>abortEXT</c> の残りの実引数には、サンプラーなどの不透明な型、大きさの決まっていない配列、bool を含む構造体と配列、文字列を書けない。
/// </para>
/// <para>
/// 文字列のリテラルのエスケープは、<c>\x</c> の後に 16 進数の桁が要り、<c>\8</c> と <c>\9</c> のような知らないエスケープは書けない。
/// 数の型の値の作成 (<c>uint("foo")</c>) には文字列を渡せない。
/// </para>
/// </remarks>
internal sealed class GlslStringArgumentAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [GlslRuleDescriptors.VariableArgumentRequired, GlslRuleDescriptors.LiteralOutOfRange, GlslRuleDescriptors.NoMatchingBuiltin];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl || !compilation.CodeTokens.Any(t => t.Kind == ShaderSyntaxKind.StringLiteralToken || t.Text is "debugPrintfEXT" or "abortEXT"))
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (InvocationExpressionSyntax or LiteralExpressionSyntax))
            {
                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is null)
            {
                continue;
            }

            switch (node)
            {
                case LiteralExpressionSyntax { Token: { Kind: ShaderSyntaxKind.StringLiteralToken } token } when DescribeEscapeError(token.Text) is { } escape:
                    requirement.Report(GlslRuleDescriptors.LiteralOutOfRange, token, token.Text, escape);
                    break;

                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax { Name: "debugPrintfEXT" or "abortEXT" } callee } call
                    when compilation.GetFunctionDeclarations(callee.Name, program).IsEmpty:
                    CheckFormat(requirement, compilation.GetExpressionTypeBinder(program), program, callee, [.. call.ArgumentExpressions]);
                    break;

                // 数の型の値の作成に文字列は渡せない (glslang の「cannot convert a string」)。
                case InvocationExpressionSyntax { Target: IdentifierExpressionSyntax callee } constructor
                    when compilation.TypeSystem.ForTree(program.Tree) is GlslTypeSystem types && types.TryDescribeNumeric(callee.Name, out _)
                         && constructor.ArgumentExpressions.Any(a => a is LiteralExpressionSyntax { Token.Kind: ShaderSyntaxKind.StringLiteralToken }):
                    requirement.Report(GlslRuleDescriptors.NoMatchingBuiltin, callee.Identifier, callee.Name, "文字列");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>debugPrintfEXT と abortEXT の実引数。</summary>
    private static void CheckFormat(GlslRequirements requirement, ExpressionTypeBinder binder, AnalyzedProgram program, IdentifierExpressionSyntax callee, ExpressionSyntax[] arguments)
    {
        // abortEXT() は実引数を省ける。
        if (arguments is not [LiteralExpressionSyntax { Token.Kind: ShaderSyntaxKind.StringLiteralToken }, ..] && !(callee.Name == "abortEXT" && arguments.Length == 0))
        {
            requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, callee.Identifier, callee.Name, 1, "文字列のリテラルでなければなりません (括弧で囲んだものと、定数の変数は書けません)");
            return;
        }

        if (callee.Name != "abortEXT")
        {
            return;
        }

        for (int i = 1; i < arguments.Length; i++)
        {
            string? problem = arguments[i] switch
            {
                LiteralExpressionSyntax { Token.Kind: ShaderSyntaxKind.StringLiteralToken } => "文字列にできません。文字列は 1 つ目の実引数にだけ書けます",
                _ when TypeOf(binder, arguments[i]) is { } type && IsOpaque(type) => $"不透明な型 ({type}) にできません",
                _ when IsUnsizedArray(arguments[i]) => "大きさの決まっていない配列にできません",
                _ when ContainsBool(binder, program, arguments[i]) => "bool を含む構造体と配列にできません",
                _ => null,
            };

            if (problem is not null)
            {
                requirement.Report(GlslRuleDescriptors.VariableArgumentRequired, callee.Identifier, callee.Name, i + 1, problem);
            }
        }
    }

    /// <summary>文字列のリテラルのエスケープの誤り。</summary>
    private static string? DescribeEscapeError(string literal)
    {
        for (int i = 1; i < literal.Length - 1; i++)
        {
            if (literal[i] != '\\')
            {
                continue;
            }

            char next = literal[i + 1];

            if (next == 'x')
            {
                if (i + 2 >= literal.Length - 1 || !char.IsAsciiHexDigit(literal[i + 2]))
                {
                    return "\\x の後には 16 進数の桁が要ります";
                }
            }
            else if (next is not ('\\' or '"' or '\'' or '?' or 'a' or 'b' or 'f' or 'n' or 'r' or 't' or 'v') && next is not (>= '0' and <= '7'))
            {
                return $"知らないエスケープ (\\{next}) です";
            }

            i++;
        }

        return null;
    }

    private static string? TypeOf(ExpressionTypeBinder binder, ExpressionSyntax expression)
        => binder.GetTypesByConfiguration(expression) is [{ Type: { } name }] ? name : null;

    private static bool IsOpaque(string type)
        => Shaderlyn.Glsl.Parsing.GlslKeywords.IsBuiltInType(type)
           && (type == "atomic_uint" || type.Contains("sampler", StringComparison.Ordinal) || type.Contains("image", StringComparison.Ordinal) || type.Contains("texture", StringComparison.Ordinal));

    /// <summary>大きさの決まっていない配列 (ブロックの最後のメンバー) か。</summary>
    private static bool IsUnsizedArray(ExpressionSyntax expression)
        => expression is MemberAccessExpressionSyntax member
           && FindMember(member) is { IsArray: true } declarator
           && !declarator.TryGetArrayLength(out _)
           && declarator.ArrayRankTokens is [.., { Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.CloseBracketToken }];

    /// <summary>メンバーの宣言子を、同じ名前のブロックのメンバーから探す。</summary>
    private static VariableDeclaratorSyntax? FindMember(MemberAccessExpressionSyntax member)
        => member.Ancestors().OfType<CompilationUnitSyntax>().FirstOrDefault()?.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Name == member.NameToken.Text && v.Parent?.Parent is StructDeclarationSyntax { Keyword.Text: not "struct" } or Shaderlyn.Glsl.Syntax.InterfaceBlockDeclarationSyntax);

    /// <summary>bool を含む構造体か、bool の配列か。</summary>
    private static bool ContainsBool(ExpressionTypeBinder binder, AnalyzedProgram program, ExpressionSyntax expression)
    {
        if (expression is IdentifierExpressionSyntax identifier
            && binder.ResolveName(identifier).Candidates is [{ Declarator: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } declarator }])
        {
            string type = declaration.Type.Name;

            return declarator.IsArray && type is "bool" or "bvec2" or "bvec3" or "bvec4"
                   || program.Structs.FirstOrDefault(s => s.Name == type && s.Keyword.Text == "struct") is { } structure
                      && structure.Fields.Any(f => f.Type.Name is "bool" or "bvec2" or "bvec3" or "bvec4");
        }

        return false;
    }
}
