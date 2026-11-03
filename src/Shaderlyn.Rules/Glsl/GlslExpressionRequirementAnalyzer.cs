using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Preprocessing;
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
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.NotAvailable, GlslRuleDescriptors.LiteralOutOfRange];

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

            // マクロの本体のトークンの連結 (##) と文字列化 (#)。ES では書けず、デスクトップでは 1.30 から (glslang の Pp.cpp の CPPdefine)。
            // 最後に残った定義を、#define の名前の位置で見る。
            foreach (MacroDefinition macro in first.Tree.PreprocessResult.Macros.Values)
            {
                if (!compilation.IsReportable(macro.NameToken))
                {
                    continue;
                }

                foreach (string feature in macro.Body
                             .Select(t => t.Kind switch
                             {
                                 ShaderSyntaxKind.HashHashToken => "'token pasting (##)'",
                                 ShaderSyntaxKind.HashToken => "'stringify (#)'",
                                 _ => null,
                             })
                             .OfType<string>()
                             .Distinct(StringComparer.Ordinal))
                {
                    fileRequirement.RequireProfile(macro.NameToken, GlslProfileMask.NotEs, feature);
                    fileRequirement.ProfileRequires(macro.NameToken, GlslProfileMask.NotEs, 130, [], feature);
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
                    AnalyzeIntegerLiteral(requirement, literal.Token);
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

                // 配列の .length() (glslang の handleDotDereference。配列でない値の .length() は CF0312)。
                case InvocationExpressionSyntax { Target: MemberAccessExpressionSyntax { Name: "length", Target: IdentifierExpressionSyntax target } length }
                    when compilation.GetExpressionTypeBinder(program).ResolveName(target).Candidates is { IsEmpty: false } candidates
                         && candidates.All(c => c.Type.IsArray):
                    requirement.ArrayObjectCheck(length.NameToken, ".length");
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

    /// <summary>
    /// 32 ビットの整数のリテラル (接尾辞が無いか u のもの) の大きさと、8 進数の桁 (glslang の Scan.cpp の字句の検査)。
    /// 0xFFFFFFFF より大きいもの、8 進数に 8 と 9 を書いたものは誤りになる。
    /// </summary>
    private static void AnalyzeIntegerLiteral(GlslRequirements requirement, ShaderSyntaxToken token)
    {
        string suffix = ShaderLiteral.GetSuffix(token);

        if (ShaderLiteral.GetKind(token) == ShaderLiteralKind.Floating || suffix is not ("" or "u" or "U"))
        {
            return;
        }

        string digits = token.Text[..^suffix.Length];
        string? problem;

        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            problem = digits.Length == 2 ? "16 進数のリテラルに桁がありません"
                : digits[2..].TrimStart('0').Length > 8 ? "16 進数のリテラルが 32 ビットに収まりません"
                : null;
        }
        else if (digits.Length > 1 && digits[0] == '0')
        {
            problem = digits.Any(c => c is '8' or '9') ? "8 進数のリテラルに 8 と 9 は書けません"
                : !Fits(digits, 8) ? "8 進数のリテラルが 32 ビットに収まりません"
                : null;
        }
        else
        {
            problem = !Fits(digits, 10) ? "数のリテラルが 32 ビットに収まりません" : null;
        }

        if (problem is not null)
        {
            requirement.Report(GlslRuleDescriptors.LiteralOutOfRange, token, token.Text, problem);
        }

        static bool Fits(string digits, int radix)
        {
            ulong value = 0;

            foreach (char c in digits)
            {
                if (c is < '0' or > '9')
                {
                    return true;
                }

                value = (value * (ulong)radix) + (ulong)(c - '0');

                if (value > uint.MaxValue)
                {
                    return false;
                }
            }

            return true;
        }
    }

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

            // 1.0f (glslang の Scan.cpp。ES の 3.00、デスクトップの 1.20 から)。
            case "f" when floating:
                requirement.ProfileRequires(token, GlslProfileMask.Es, 300, [], "'floating-point suffix'");
                requirement.ProfileRequires(token, GlslProfileMask.NotEs, 120, [], "'floating-point suffix'");
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

        // 名前は、その宣言の型で見る (関数の中で宣言した構造体の変数は、式の型が求まらないことがある)。
        // 構造体の宣言と同時に宣言した変数 (struct S { … } s1, s2;) は、その構造体を見る。
        if (expression is IdentifierExpressionSyntax identifier
            && binder.ResolveName(identifier).Candidates.Any(c => c.Type.IsArray
                || (c.Declarator.Parent is StructDeclarationSyntax { Keyword.Text: "struct" } own
                    ? FieldsContainArray(program, own, expression, depth: 0)
                    : StructContainsArray(program, c.Type.TypeName, expression))))
        {
            return true;
        }

        return binder.GetEvaluatorFor(expression).Evaluate(expression) is { } type && StructContainsArray(program, type, expression);
    }

    /// <summary>構造体が配列のメンバーを持つか。入れ子の構造体も見る。</summary>
    /// <param name="program">木。</param>
    /// <param name="type">構造体の名前。</param>
    /// <param name="at">使う位置。それを囲む関数の中で、それより前に宣言した同じ名前の構造体を、関数の外のものより先に引く。</param>
    /// <param name="depth">入れ子の深さ。</param>
    internal static bool StructContainsArray(AnalyzedProgram program, string type, ShaderSyntaxNode? at = null, int depth = 0)
        => depth <= 8 && FindStruct(program, type, at) is { } structure && FieldsContainArray(program, structure, at, depth);

    /// <summary>構造体のメンバーに、配列か配列を含む構造体があるか。</summary>
    private static bool FieldsContainArray(AnalyzedProgram program, StructDeclarationSyntax structure, ShaderSyntaxNode? at, int depth)
        => structure.Fields.Any(field => field.Variables.Any(v => v.IsArray) || StructContainsArray(program, field.Type.Name, at, depth + 1));

    /// <summary>その位置から見える構造体の宣言を引く。関数の中で、使う位置より前に宣言したものを先に引く。</summary>
    private static StructDeclarationSyntax? FindStruct(AnalyzedProgram program, string type, ShaderSyntaxNode? at)
    {
        if (at?.FirstToken is { } use)
        {
            for (Shaderlyn.Core.Syntax.SyntaxNode? node = at.Parent; node is not null; node = node.Parent)
            {
                if (node is FunctionDeclarationSyntax function)
                {
                    if (function.DescendantNodesAndSelf().OfType<StructDeclarationSyntax>()
                            .LastOrDefault(s => s.Keyword.Text == "struct" && s.Name == type && s.Keyword.Span.Start < use.Span.Start) is { } local)
                    {
                        return local;
                    }

                    break;
                }
            }
        }

        return program.Structs.FirstOrDefault(s => s.Keyword.Text == "struct" && s.Name == type);
    }
}
