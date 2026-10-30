using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 式の演算子とリテラルを、使える版と拡張で検査する (GL0001)。
/// </summary>
/// <remarks>
/// glslang.y の式の規則の動作を移したもの。
/// <list type="bullet">
///   <item><description>整数の演算子 (<c>%</c>、<c>&lt;&lt;</c>、<c>&amp;</c>、<c>~</c>、<c>%=</c> など) と <c>uint</c> のリテラルは、デスクトップの 1.30・ES の 3.00 から (fullIntegerCheck)</description></item>
///   <item><description>配列 (と配列を含む構造体) の比較と代入は、デスクトップの 1.20・ES の 3.00 から (arrayObjectCheck)</description></item>
///   <item><description>64 ビット・16 ビットの整数、double、half のリテラルに要る拡張と版</description></item>
/// </list>
/// </remarks>
internal sealed partial class GlslExpressionRequirementAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.NotAvailable];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        // 行の継続 (行末の \)。プリプロセッサの指令の行も含めて、書かれたとおりのトークンで探す (glslang の lineContinuationCheck)。
        if (compilation.Programs.FirstOrDefault() is { } first
            && GlslRequirements.Create(context, compilation, first, reported) is { } fileRequirement)
        {
            foreach (ShaderSyntaxToken token in compilation.CodeTokens)
            {
                if (token.TrailingTrivia.Concat(token.LeadingTrivia).Any(t => t.Kind == ShaderSyntaxKind.LineContinuationTrivia))
                {
                    fileRequirement.ProfileRequires(token, GlslProfileMask.Es, 300, [], "行の継続 (行末の \\)");
                    fileRequirement.ProfileRequires(token, GlslProfileMask.NotEs, 420, ["GL_ARB_shading_language_420pack"], "行の継続 (行末の \\)");
                }
            }
        }

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
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
                case LiteralExpressionSyntax literal:
                    AnalyzeLiteral(requirement, literal.Token);
                    break;

                case PrefixUnaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.TildeToken } unary:
                    requirement.FullIntegerCheck(unary.OperatorToken, "bitwise not");
                    break;

                case BinaryExpressionSyntax binary when IntegerOperation(binary.OperatorToken.Kind) is { } feature:
                    requirement.FullIntegerCheck(binary.OperatorToken, feature);
                    break;

                case BinaryExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsEqualsToken or ShaderSyntaxKind.ExclamationEqualsToken } comparison
                    when ContainsArray(compilation, program, comparison.Left):
                    requirement.ArrayObjectCheck(comparison.OperatorToken, "array comparison");
                    break;

                case AssignmentExpressionSyntax assignment when IntegerOperation(assignment.OperatorToken.Kind) is { } feature:
                    requirement.FullIntegerCheck(assignment.OperatorToken, feature);
                    break;

                case AssignmentExpressionSyntax { OperatorToken.Kind: ShaderSyntaxKind.EqualsToken } assignment
                    when ContainsArray(compilation, program, assignment.Left):
                    requirement.ArrayObjectCheck(assignment.OperatorToken, "array assignment");
                    break;

                case IdentifierExpressionSyntax { Name: "gl_PointSize" } pointSize
                    when compilation.GetExpressionTypeBinder(program).ResolveName(pointSize).Kind == DeclaredNameKind.NotFound:
                    AnalyzePointSize(requirement, pointSize.Identifier);
                    break;

                case MemberAccessExpressionSyntax { NameToken.Text: "gl_PointSize" } member
                    when RootName(member.Target) is "gl_in" or "gl_out":
                    AnalyzePointSize(requirement, member.NameToken);
                    break;

                case ElementAccessExpressionSyntax { Target: IdentifierExpressionSyntax array } element
                    when !array.Name.StartsWith("gl_", StringComparison.Ordinal)
                         && new GlslConstantEvaluator(compilation.GetExpressionTypeBinder(program)).IsCertainlyNotConstant(element.Index):
                    AnalyzeVariableIndex(requirement, compilation, program, array);
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>整数にしか使えない演算子の、glslang の機能の名前。</summary>
    private static string? IntegerOperation(ShaderSyntaxKind kind) => kind switch
    {
        ShaderSyntaxKind.PercentToken => "%",
        ShaderSyntaxKind.LessThanLessThanToken => "bit shift left",
        ShaderSyntaxKind.GreaterThanGreaterThanToken => "bit shift right",
        ShaderSyntaxKind.AmpersandToken => "bitwise and",
        ShaderSyntaxKind.CaretToken => "bitwise exclusive or",
        ShaderSyntaxKind.BarToken => "bitwise inclusive or",
        ShaderSyntaxKind.PercentEqualsToken => "%=",
        ShaderSyntaxKind.LessThanLessThanEqualsToken => "bit-shift left assign",
        ShaderSyntaxKind.GreaterThanGreaterThanEqualsToken => "bit-shift right assign",
        ShaderSyntaxKind.AmpersandEqualsToken => "bitwise-and assign",
        ShaderSyntaxKind.CaretEqualsToken => "bitwise-xor assign",
        ShaderSyntaxKind.BarEqualsToken => "bitwise-or assign",
        _ => null,
    };

    /// <summary>リテラルの接尾辞に要る版と拡張 (glslang の primary_expression の規則)。</summary>
    private static void AnalyzeLiteral(GlslRequirements requirement, ShaderSyntaxToken token)
    {
        if (token.Kind != ShaderSyntaxKind.NumericLiteralToken)
        {
            return;
        }

        bool floating = ShaderLiteral.GetKind(token) == ShaderLiteralKind.Floating;

        switch (ShaderLiteral.GetSuffix(token).ToLowerInvariant())
        {
            case "u" when !floating:
                requirement.FullIntegerCheck(token, "unsigned literal");
                break;

            case "l" when !floating:
                requirement.Int64Check(token, "64-bit integer literal");
                break;

            case "ul" or "uls" when !floating:
                requirement.Int64Check(token, "64-bit unsigned integer literal");
                break;

            case "s" when !floating:
                requirement.ExplicitInt16Check(token, "16-bit integer literal");
                break;

            case "us" when !floating:
                requirement.ExplicitInt16Check(token, "16-bit unsigned integer literal");
                break;

            case "lf":
                requirement.RequireProfile(token, GlslProfileMask.Core | GlslProfileMask.Compatibility, "'double literal'");
                requirement.DoubleCheck(token, "double literal");
                break;

            case "hf":
                requirement.Float16Check(token, "half float literal");
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// ES のジオメトリとテッセレーションのシェーダーの <c>gl_PointSize</c> に要る拡張 (glslang の Initialize.cpp の setVariableExtensions)。
    /// そのまま書いたものと、<c>gl_in[i].gl_PointSize</c> / <c>gl_out[i].gl_PointSize</c> を見る。
    /// </summary>
    private static void AnalyzePointSize(GlslRequirements requirement, ShaderSyntaxToken at)
    {
        if (!requirement.IsEs)
        {
            return;
        }

        switch (requirement.Stage)
        {
            case GlslShaderStage.Geometry:
                requirement.RequireExtensions(at, ["GL_EXT_geometry_point_size", "GL_OES_geometry_point_size"], "'gl_PointSize'");
                break;

            case GlslShaderStage.TessellationControl or GlslShaderStage.TessellationEvaluation:
                requirement.RequireExtensions(at, ["GL_EXT_tessellation_point_size", "GL_OES_tessellation_point_size"], "'gl_PointSize'");
                break;

            default:
                break;
        }
    }

    /// <summary>添字とメンバーを外した、もとの名前 (<c>gl_in[3].x</c> なら <c>gl_in</c>)。</summary>
    private static string? RootName(ExpressionSyntax expression) => expression switch
    {
        IdentifierExpressionSyntax identifier => identifier.Name,
        ElementAccessExpressionSyntax element => RootName(element.Target),
        ParenthesizedExpressionSyntax parenthesized => RootName(parenthesized.Expression),
        _ => null,
    };

    /// <summary>
    /// 定数でない添字で引く配列に要る版と拡張 (glslang の handleBracketDereference)。
    /// サンプラーとイメージの配列、uniform と buffer のブロックの配列、フラグメントシェーダーの出力の配列を見る。
    /// </summary>
    private static void AnalyzeVariableIndex(GlslRequirements requirement, ShaderCompilation compilation, AnalyzedProgram program, IdentifierExpressionSyntax array)
    {
        NameResolution resolution = compilation.GetExpressionTypeBinder(program).ResolveName(array);
        ShaderSyntaxToken at = array.Identifier;

        if (resolution is { Kind: DeclaredNameKind.Local or DeclaredNameKind.Global, Candidates: [{ Type.IsArray: true } candidate] })
        {
            if (OpaqueType().IsMatch(candidate.Type.TypeName) && requirement.Version >= 130)
            {
                const string feature = "サンプラーの配列を定数でない添字で引くこと";
                requirement.RequireProfile(at, GlslProfileMask.Es | GlslProfileMask.Core | GlslProfileMask.Compatibility, feature);
                requirement.ProfileRequires(at, GlslProfileMask.Es, 320, ["GL_EXT_gpu_shader5", "GL_OES_gpu_shader5"], feature);
                requirement.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 400, ["GL_ARB_gpu_shader5", "GL_NV_gpu_shader5"], feature);
            }
            else if (requirement.Stage == GlslShaderStage.Fragment
                     && resolution.Kind == DeclaredNameKind.Global
                     && candidate.Declarator is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: CompilationUnitSyntax } declaration }
                     && declaration.HasModifier("out"))
            {
                requirement.RequireProfile(at, GlslProfileMask.NotEs, "フラグメントシェーダーの出力の配列を定数でない添字で引くこと");
            }

            return;
        }

        // インスタンスの名前を持つブロックの配列は、名前の解決に載らない。関数の中にもその名前が無いときだけ見る。
        if (resolution.Kind != DeclaredNameKind.NotFound)
        {
            return;
        }

        foreach (StructDeclarationSyntax block in program.Tree.Root.Declarations.OfType<StructDeclarationSyntax>())
        {
            if (block.Keyword.Text == "struct"
                || !block.Declarators.Any(d => d.Node is VariableDeclaratorSyntax { IsArray: true } instance && instance.Name == array.Name))
            {
                continue;
            }

            switch (block.Keyword.Text)
            {
                case "buffer":
                    requirement.RequireProfile(at, GlslProfileMask.NotEs, "buffer のブロックの配列を定数でない添字で引くこと");
                    break;

                case "uniform":
                    const string feature = "uniform のブロックの配列を定数でない添字で引くこと";
                    requirement.ProfileRequires(at, GlslProfileMask.Es, 320, ["GL_EXT_gpu_shader5", "GL_OES_gpu_shader5"], feature);
                    requirement.ProfileRequires(at, GlslProfileMask.Core, 400, ["GL_ARB_gpu_shader5", "GL_NV_gpu_shader5"], feature);
                    break;

                default:
                    break;
            }

            return;
        }
    }

    /// <summary>サンプラー・イメージ・テクスチャ・サブパスの入力の型 (glslang の EbtSampler)。</summary>
    [GeneratedRegex(@"^[iu]?(sampler|image|texture|subpassInput)[0-9A-Za-z_]*$")]
    private static partial Regex OpaqueType();

    /// <summary>式の値が配列か、配列を含む構造体か (glslang の containsArray)。</summary>
    private static bool ContainsArray(ShaderCompilation compilation, AnalyzedProgram program, ExpressionSyntax expression)
    {
        ExpressionTypeBinder binder = compilation.GetExpressionTypeBinder(program);

        if (expression is IdentifierExpressionSyntax identifier && binder.ResolveName(identifier).Candidates.Any(c => c.Type.IsArray))
        {
            return true;
        }

        return binder.GetEvaluatorFor(expression).Evaluate(expression) is { } type && StructContainsArray(program, type, depth: 0);
    }

    /// <summary>構造体が配列のメンバーを持つか。入れ子の構造体も見る。</summary>
    private static bool StructContainsArray(AnalyzedProgram program, string type, int depth)
    {
        if (depth > 8 || program.Structs.FirstOrDefault(s => s.Name == type) is not { } structure)
        {
            return false;
        }

        foreach (VariableDeclarationSyntax field in structure.Fields)
        {
            if (field.Variables.Any(v => v.IsArray) || StructContainsArray(program, field.Type.Name, depth + 1))
            {
                return true;
            }
        }

        return false;
    }
}
