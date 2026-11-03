using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 予約された名前の宣言を検査する (GL0401)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>reservedErrorCheck</c> と、その前の <c>redeclareBuiltinVariable</c> の判定を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// <c>gl_</c> で始まる名前の変数・仮引数・ブロックのメンバーとインスタンスの名前を報告する。
/// 関数の外で、組み込みの変数を宣言し直せる場合 (デスクトップの 1.30 以降・ES の 3.20 以降か <c>GL_EXT_shader_io_blocks</c> の、
/// <c>gl_ClipDistance</c>、<c>gl_FragDepth</c> など決まった名前) は報告しない。
/// <c>gl_</c> で始まる名前のブロック (<c>out gl_PerVertex { … }</c>) は、組み込みのブロックの宣言し直しなので見ない。
/// </para>
/// <para>
/// <c>__</c> を含む名前は、ES の 3.00 より前でだけ報告する (それ以降は glslang でも警告)。
/// <c>GL_EXT_spirv_intrinsics</c> を有効にしていれば、どちらも報告しない。
/// </para>
/// </remarks>
internal sealed class GlslReservedNameAnalyzer : SemanticRuleAnalyzer
{
    private const string SpirvIntrinsics = "GL_EXT_spirv_intrinsics";
    private const string ConservativeDepth = "GL_EXT_conservative_depth";

    /// <summary>関数の外で宣言し直せる組み込みの変数 (glslang の redeclareBuiltinVariable の名前の一覧)。版と言語で決まるものは別に見る。</summary>
    private static readonly FrozenSet<string> Redeclarable = FrozenSet.ToFrozenSet(
    [
        "gl_ClipDistance", "gl_CullDistance", "gl_ShadingRateEXT", "gl_PrimitiveShadingRateEXT",
        "gl_FrontColor", "gl_BackColor", "gl_FrontSecondaryColor", "gl_BackSecondaryColor", "gl_SecondaryColor",
        "gl_SampleMask", "gl_EnableOpacityMicromapEXT", "gl_Layer", "gl_PrimitiveIndicesNV",
        "gl_PrimitivePointIndicesEXT", "gl_PrimitiveLineIndicesEXT", "gl_PrimitiveTriangleIndicesEXT", "gl_TexCoord",
    ], StringComparer.Ordinal);

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.ReservedName];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, GlslRequirements?> requirements = [];
        HashSet<string> declaredGlobals = new(StringComparer.Ordinal);

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (node is not (VariableDeclaratorSyntax or ParameterSyntax))
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
                case ParameterSyntax parameter:
                    Check(requirement, parameter.NameToken, parameter.NameToken, mayRedeclare: false);
                    break;

                // 名前の無いブロックのメンバー。
                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: InterfaceBlockDeclarationSyntax block } } member:
                    if (!IsBuiltInName(block.Name))
                    {
                        Check(requirement, member.NameToken, member.NameToken, mayRedeclare: false);
                    }

                    break;

                // 構造体のメンバーは見ない。名前のあるブロックのメンバー。
                case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: StructDeclarationSyntax structure } } member:
                    if (!IsStruct(structure) && !IsBuiltInName(structure.NameToken?.Text ?? string.Empty))
                    {
                        Check(requirement, member.NameToken, member.NameToken, mayRedeclare: false);
                    }
                    else if (IsStruct(structure))
                    {
                        CheckKeyword(requirement, member.NameToken, member.NameToken);
                    }

                    break;

                // 名前のあるブロックのインスタンスの名前。glslang はブロックの位置で報告する。
                case VariableDeclaratorSyntax { Parent: StructDeclarationSyntax structure } instance when !IsStruct(structure):
                    if (!IsBuiltInName(structure.NameToken?.Text ?? string.Empty))
                    {
                        Check(requirement, instance.NameToken, structure.Keyword, mayRedeclare: false);
                    }

                    break;

                case VariableDeclaratorSyntax variable:
                    // 関数の外の同じ gl_ の名前の 2 度目は、glslang は伝えない (1 度目で利用者の変数になる)。
                    if (variable.Ancestors().Any(a => a is FunctionDeclarationSyntax) || !IsBuiltInName(variable.Name) || declaredGlobals.Add(variable.Name))
                    {
                        Check(requirement, variable.NameToken, variable.NameToken, mayRedeclare: !variable.Ancestors().Any(a => a is FunctionDeclarationSyntax));
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary><c>gl_</c> で始まるか (glslang の builtInName)。</summary>
    private static bool IsBuiltInName(string name) => name.StartsWith("gl_", StringComparison.Ordinal);

    /// <summary>ブロックでなく構造体の宣言か。</summary>
    private static bool IsStruct(StructDeclarationSyntax structure) => structure.Keyword.Text == "struct";

    /// <summary>
    /// そのバージョンと拡張でキーワードの語 (4.00 の precise、GL_EXT_terminate_invocation の terminateInvocation) は名前にできない。
    /// glslang は構文の誤りにする。予約語の誤りは構文の検査 (CF0001) が伝える。
    /// </summary>
    /// <returns>報告したなら <see langword="true"/>。</returns>
    private static bool CheckKeyword(GlslRequirements requirement, ShaderSyntaxToken name, ShaderSyntaxToken at)
    {
        if (new Shaderlyn.Glsl.Parsing.GlslWordRules(requirement.Version, requirement.IsEs, requirement.Stage, requirement.Vulkan, extension => requirement.IsOn(extension, name))
                .Classify(name.Text) is not { Kind: Shaderlyn.Glsl.Parsing.GlslWordKind.Keyword, IsReservedError: false })
        {
            return false;
        }

        requirement.Report(GlslRuleDescriptors.ReservedName, at, name.Text, "このバージョンと拡張ではキーワードなので、名前に使えません。");
        return true;
    }

    /// <summary>名前を検査する。</summary>
    /// <param name="requirement">版と拡張。</param>
    /// <param name="name">名前のトークン。</param>
    /// <param name="at">報告する位置。</param>
    /// <param name="mayRedeclare">組み込みの変数を宣言し直せる場所 (関数の外の変数) か。</param>
    private static void Check(GlslRequirements requirement, ShaderSyntaxToken name, ShaderSyntaxToken at, bool mayRedeclare)
    {
        string text = name.Text;

        if (requirement.IsOn(SpirvIntrinsics, name))
        {
            return;
        }

        if (CheckKeyword(requirement, name, at))
        {
            return;
        }

        if (IsBuiltInName(text))
        {
            if (text == "gl_FragDepth" && requirement.IsOn(ConservativeDepth, name)
                || mayRedeclare && MayRedeclare(requirement, text, name))
            {
                return;
            }

            requirement.Report(GlslRuleDescriptors.ReservedName, at, text, "gl_ で始まる名前は宣言できません。");
        }

        if (text.Contains("__", StringComparison.Ordinal) && requirement.IsEs && requirement.Version < 300)
        {
            requirement.Report(GlslRuleDescriptors.ReservedName, at, text, "__ を含む名前は、ES の 3.00 より前では宣言できません。");
        }
    }

    /// <summary>
    /// 組み込みの変数を宣言し直しているかもしれないか (glslang の redeclareBuiltinVariable が名前を受け付けるか)。
    /// その版とステージに組み込みの変数があるかは確かめず、あるかもしれなければ報告しない側に倒す。
    /// gl_Color は、互換のプロファイルか 1.30 までのフラグメントシェーダーにだけある (glslang の IncludeLegacy)。
    /// </summary>
    internal static bool MayRedeclare(GlslRequirements requirement, string name, ShaderSyntaxToken at)
    {
        bool desktop = !requirement.IsEs && (requirement.Version >= 130 || name == "gl_TexCoord");
        bool es = requirement.IsEs
            && (requirement.Version >= 320
                || requirement.IsOn("GL_EXT_shader_io_blocks", at)
                || requirement.IsOn("GL_OES_shader_io_blocks", at)
                || name == "gl_FragDepth" && requirement.IsOn(ConservativeDepth, at));

        if (!desktop && !es)
        {
            return false;
        }

        bool separateShaderObjects = !requirement.IsEs && requirement.Version <= 140 && requirement.IsOn("GL_ARB_separate_shader_objects", at)
            && name is "gl_Position" or "gl_PointSize" or "gl_ClipVertex" or "gl_FogFragCoord";

        return separateShaderObjects
            || Redeclarable.Contains(name)
            || name == "gl_FragDepth" && (desktop && requirement.Version >= 420 || es)
            || name == "gl_FragCoord" && (desktop && requirement.Version >= 140 || es)
            || name == "gl_Color" && requirement.Stage == Shaderlyn.Glsl.GlslShaderStage.Fragment
               && (requirement.Version <= 130 || requirement.Profile == GlslProfileMask.Compatibility)
            || name == "gl_FragStencilRefARB" && desktop && requirement.Version >= 140 && requirement.Stage == Shaderlyn.Glsl.GlslShaderStage.Fragment;
    }
}
