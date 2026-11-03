using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Parsing;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 宣言の型を検査する (GL0403 / GL0404)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>samplerCheck</c>・<c>voidErrorCheck</c>・<c>checkIoArrayConsistency</c> のテッセレーションの入力の検査を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// サンプラー・イメージ・テクスチャの型と、それをメンバーに持つ構造体の変数は、uniform でなければならない
/// (<c>GL_ARB_bindless_texture</c> を有効にしたときを除く)。関数の外と中の変数を見る。
/// これらの型と <c>atomic_uint</c> の仮引数は、<c>out</c> / <c>inout</c> にできない (<c>parameterTypeCheck</c>)。
/// </para>
/// <para>
/// void の変数・構造体のメンバー・名前のある仮引数は宣言できない。
/// </para>
/// <para>
/// テッセレーションの制御・評価のシェーダーの、patch でない入力の配列 (とブロックの配列) の大きさは、gl_MaxPatchVertices (glslang の既定の資源の 32) か、省いたもの。
/// 大きさを数で書いたものだけを見る。
/// </para>
/// </remarks>
internal sealed partial class GlslDeclarationTypeAnalyzer : SemanticRuleAnalyzer
{
    /// <summary>glslang の既定の資源の gl_MaxPatchVertices。</summary>
    private const int MaxPatchVertices = 32;

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.TypeNotAllowedHere, GlslRuleDescriptors.PatchInputSize];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (VariableDeclaratorSyntax or ParameterSyntax or IncompleteDeclarationSyntax or StructDeclarationSyntax))
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
                case ParameterSyntax { Type.Name: "void", NameToken: { IsMissing: false, Text.Length: > 0 } name }:
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, name, name.Text, "void の仮引数には名前を付けられません。");
                    break;

                // void はほかの仮引数と並べられない (glslang の「cannot be an argument type except for '(void)'」)。
                case ParameterSyntax { Type.Name: "void", Parent: FunctionDeclarationSyntax function } parameter when function.ParameterList.Count() > 1:
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, parameter.Type.NameToken, "void", "void は、仮引数の無いことを示す (void) の形でしか書けません。");
                    break;

                // サンプラー・イメージ・atomic_uint は、out / inout の仮引数にできない (glslang の parameterTypeCheck)。
                case ParameterSyntax { NameToken.IsMissing: false } parameter
                    when parameter.ModifierTokens.Concat(parameter.Type.ModifierTokens).Any(t => t.Text is "out" or "inout")
                         && (IsOpaque(parameter.Type.Name) || parameter.Type.Name == "atomic_uint")
                         && !requirement.IsOn("GL_ARB_bindless_texture", parameter.Type.NameToken):
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, parameter.Type.NameToken, parameter.Name, "サンプラー・イメージ・atomic_uint は、out / inout の仮引数にできません。");
                    break;

                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type.Name: "void" } } variable:
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, variable.NameToken, variable.Name, "void の変数は宣言できません。");
                    break;

                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: CompilationUnitSyntax or LocalDeclarationStatementSyntax } declaration } variable:
                    CheckOpaque(requirement, program, declaration, variable);

                    // ES では、bool の型に精度の修飾子を書けない (glslang の checkPrecisionQualifier の「type cannot have precision qualifier」)。
                    if (requirement.IsEs
                        && declaration.Type.Name is "bool" or "bvec2" or "bvec3" or "bvec4"
                        && declaration.ModifierTokens.Concat(declaration.Type.ModifierTokens).Any(t => t.Text is "highp" or "mediump" or "lowp")
                        && declaration.Variables.FirstOrDefault() == variable)
                    {
                        requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, declaration.Type.NameToken, declaration.Type.Name, "bool の型には精度の修飾子を書けません。");
                    }

                    CheckPatchInput(requirement, declaration.ModifierTokens, variable.ArrayRankTokens, variable.NameToken);

                    if (declaration.Parent is CompilationUnitSyntax && !variable.IsArray && !variable.Name.StartsWith("gl_", StringComparison.Ordinal))
                    {
                        CheckArrayedIo(requirement, declaration.ModifierTokens, variable.Name, variable.NameToken);
                    }

                    break;

                // 名前のあるブロックのインスタンス。glslang はブロックの位置で報告する。
                case VariableDeclaratorSyntax { Parent: StructDeclarationSyntax { Keyword.Text: not "struct" } block } instance:
                    ImmutableArray<ShaderSyntaxToken> modifiers = [.. block.Modifiers, block.Keyword];
                    CheckPatchInput(requirement, modifiers, instance.ArrayRankTokens, modifiers[0]);

                    if (!instance.IsArray && !instance.Name.StartsWith("gl_", StringComparison.Ordinal))
                    {
                        CheckArrayedIo(requirement, modifiers, instance.Name, modifiers[0]);
                    }

                    break;

                // 構造体の定義と一緒に宣言した、サンプラーかイメージを含む構造体の uniform でない変数 (struct S { sampler2D s; } v;)。
                case StructDeclarationSyntax { Keyword.Text: "struct", Parent: CompilationUnitSyntax or LocalDeclarationStatementSyntax } inline
                    when inline.Declarators.Select(d => d.Node).OfType<VariableDeclaratorSyntax>().FirstOrDefault() is { } inlineVariable
                         && inline.Fields.Any(f => IsOpaque(f.Type.Name))
                         && !inline.Modifiers.Any(t => t.Text == "uniform")
                         && !requirement.IsOn("GL_ARB_bindless_texture", inlineVariable.NameToken):
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, inlineVariable.NameToken, inlineVariable.Name, "サンプラーかイメージを含む構造体の変数は、uniform でなければなりません。");
                    break;

                // 構造体とブロックの中の構造体の定義 (glslang の nestedStructCheck)。
                case StructDeclarationSyntax { Keyword.Text: "struct" } nested
                    when nested.Ancestors().Any(a => a is StructDeclarationSyntax or InterfaceBlockDeclarationSyntax):
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, nested.Keyword, nested.Name.Length > 0 ? nested.Name : "struct", "構造体とブロックの中では、構造体を定義できません。");
                    break;

                // ブロックのメンバーのサンプラー・イメージ・atomic_uint (それを含む構造体を含む)。
                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: StructDeclarationSyntax { Keyword.Text: not "struct" } or InterfaceBlockDeclarationSyntax } member } variable:
                    CheckBlockMember(requirement, program, member, variable);
                    break;

                // atomic_uint の精度は highp だけ (glslang の checkPrecisionQualifier)。
                case IncompleteDeclarationSyntax { Tokens: [{ Text: "precision" }, { Text: "mediump" or "lowp" }, { Text: "atomic_uint" } atomic, ..] }:
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, atomic, atomic.Text, "atomic_uint の精度には highp しか書けません。");
                    break;

                // precision mediump float;
                case IncompleteDeclarationSyntax { Tokens: [{ Text: "precision" }, { Text: "highp" or "mediump" or "lowp" }, { Kind: ShaderSyntaxKind.IdentifierToken } type, .. var rest] }
                    when !IsPrecisionType(type.Text) || rest is [{ Kind: ShaderSyntaxKind.OpenBracketToken }, ..]:
                    requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, type, type.Text, "精度の文には float・int・サンプラーとイメージの型・atomic_uint (配列でないもの) しか書けません。");
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>ブロックのメンバーは、サンプラー・イメージ・atomic_uint とそれを含む構造体にできない (GL_ARB_bindless_texture のサンプラーを除く)。</summary>
    private static void CheckBlockMember(GlslRequirements requirement, AnalyzedProgram program, VariableDeclarationSyntax member, VariableDeclaratorSyntax variable)
    {
        if (requirement.IsOn("GL_EXT_structured_descriptor_heap", variable.NameToken))
        {
            return;
        }

        string type = member.Type.Name;
        bool sampler = type.Contains("sampler", StringComparison.Ordinal) || type.Contains("texture", StringComparison.Ordinal);
        bool opaque = IsOpaque(type) || type == "atomic_uint"
                      || program.Structs.FirstOrDefault(s => s.Name == type && s.Keyword.Text == "struct") is { } structure
                         && structure.Fields.Any(f => IsOpaque(f.Type.Name) || f.Type.Name == "atomic_uint");

        if (opaque && !(sampler && requirement.IsOn("GL_ARB_bindless_texture", variable.NameToken)))
        {
            requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, variable.NameToken, variable.Name, "ブロックのメンバーは、サンプラー・イメージ・atomic_uint とそれを含む構造体にできません。");
        }
    }

    /// <summary>精度の文に書ける型か (glslang の setDefaultPrecision)。</summary>
    private static bool IsPrecisionType(string type) => type is "float" or "int" or "atomic_uint" || IsOpaque(type);

    /// <summary>サンプラーとイメージの型の変数は uniform でなければならない (samplerCheck)。</summary>
    private static void CheckOpaque(GlslRequirements requirement, AnalyzedProgram program, VariableDeclarationSyntax declaration, VariableDeclaratorSyntax variable)
    {
        if (HasStorage(declaration.ModifierTokens, "uniform")
            || HasStorage(declaration.ModifierTokens, "tileImageEXT")
            || requirement.IsOn("GL_ARB_bindless_texture", variable.NameToken))
        {
            return;
        }

        string type = declaration.Type.Name;

        // atomic_uint も uniform の変数と仮引数にしか使えない (glslang の atomicUintCheck)。
        if (IsOpaque(type) || type == "atomic_uint")
        {
            requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, variable.NameToken, variable.Name, $"{type} は uniform の変数と仮引数にしか使えません。");
        }
        else if (program.Structs.FirstOrDefault(s => s.Name == type && s.Keyword.Text == "struct") is { } structure
                 && structure.Fields.Any(f => IsOpaque(f.Type.Name)))
        {
            requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, variable.NameToken, variable.Name, $"サンプラーかイメージを含む構造体 {type} は uniform の変数にしか使えません。");
        }
    }

    /// <summary>テッセレーションの入力の配列の大きさ。</summary>
    private static void CheckPatchInput(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> modifiers, ImmutableArray<ShaderSyntaxToken> ranks, ShaderSyntaxToken at)
    {
        if (requirement.Stage is not (GlslShaderStage.TessellationControl or GlslShaderStage.TessellationEvaluation)
            || !HasStorage(modifiers, "in")
            || HasStorage(modifiers, "patch")
            || ranks is not [{ Kind: ShaderSyntaxKind.OpenBracketToken }, { Kind: ShaderSyntaxKind.NumericLiteralToken } size, { Kind: ShaderSyntaxKind.CloseBracketToken }, ..]
            || GlslConstantEvaluator.EvaluateLiteral(size) is not { Kind: GlslConstantKind.Int or GlslConstantKind.Uint } value
            || value.Integer == MaxPatchVertices)
        {
            return;
        }

        requirement.Report(
            GlslRuleDescriptors.PatchInputSize,
            at,
            value.Integer.ToString(CultureInfo.InvariantCulture),
            MaxPatchVertices.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// ジオメトリシェーダーの入力、テッセレーションの制御シェーダーの patch でない入力と出力、評価シェーダーの patch でない入力は、
    /// 配列でなければならない (glslang の ioArrayCheck と isArrayedIo)。layout(passthrough) は除く。
    /// </summary>
    private static void CheckArrayedIo(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> modifiers, string name, ShaderSyntaxToken at)
    {
        bool input = HasStorage(modifiers, "in");
        bool output = HasStorage(modifiers, "out");
        bool patch = HasStorage(modifiers, "patch");

        bool arrayed = requirement.Stage switch
        {
            GlslShaderStage.Geometry => input,
            GlslShaderStage.TessellationControl => !patch && (input || output),
            GlslShaderStage.TessellationEvaluation => !patch && input,
            _ => false,
        };

        if (arrayed && !modifiers.Any(m => m.Text == "passthrough"))
        {
            requirement.Report(
                GlslRuleDescriptors.TypeNotAllowedHere,
                at,
                name,
                $"{GlslRequirements.DescribeStage(requirement.Stage!.Value)}の{(input ? "入力" : "出力")}は配列でなければなりません。");
        }
    }

    /// <summary>修飾子 (<c>layout(…)</c> の外) に、その修飾子があるか。</summary>
    private static bool HasStorage(ImmutableArray<ShaderSyntaxToken> modifiers, string storage)
    {
        int depth = 0;

        foreach (ShaderSyntaxToken modifier in modifiers)
        {
            depth += modifier.Kind switch
            {
                ShaderSyntaxKind.OpenParenToken => 1,
                ShaderSyntaxKind.CloseParenToken => -1,
                _ => 0,
            };

            if (depth == 0 && modifier.Text == storage)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>組み込みのサンプラー・イメージ・テクスチャ・サブパスの入力の型か (glslang の EbtSampler)。attachmentEXT は除く。</summary>
    private static bool IsOpaque(string type) => GlslKeywords.IsBuiltInType(type) && Opaque().IsMatch(type);

    /// <summary>サンプラー・イメージ・テクスチャ・サブパスの入力の型 (glslang の EbtSampler)。</summary>
    [GeneratedRegex(@"^(__)?[iu]?(sampler|image|texture|subpassInput)[0-9A-Za-z_]*$")]
    private static partial Regex Opaque();
}
