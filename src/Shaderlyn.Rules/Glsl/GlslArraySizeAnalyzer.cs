using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// 大きさを書かなければならない配列を検査する (GL0402)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>arraySizeRequiredCheck</c>・<c>structArrayCheck</c>・<c>arraySizesCheck</c> を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// どのプロファイルでも、関数の戻り値と仮引数の配列、構造体のメンバーの配列には大きさが要る。
/// 初期化子の無い配列の配列は、一番外側の次元しか大きさを省けない。
/// </para>
/// <para>
/// ES では、初期化子の無い変数とブロック (のメンバーとインスタンス) の配列に大きさが要る。
/// 例外は、ジオメトリ・テッセレーション・メッシュのシェーダーの入力と出力の配列 (3.20 か拡張から)、buffer のブロックの最後のメンバー、
/// <c>GL_EXT_uniform_buffer_unsized_array</c> を有効にしたときの uniform のブロックの最後のメンバー。
/// </para>
/// </remarks>
internal sealed class GlslArraySizeAnalyzer : SemanticRuleAnalyzer
{
    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [GlslRuleDescriptors.ArraySizeRequired, GlslRuleDescriptors.TypeNotAllowedHere];

    /// <summary>gl_MaxMeshViewCountNV (glslang の既定の資源の値)。</summary>
    private const long MaxMeshViewCount = 4;

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
            if (node is not (FunctionDeclarationSyntax or ParameterSyntax or VariableDeclaratorSyntax))
            {
                continue;
            }

            if (!requirements.TryGetValue(program, out GlslRequirements? requirement))
            {
                requirement = requirements[program] = GlslRequirements.Create(context, compilation, program, reported);
            }

            if (requirement is not null)
            {
                Check(requirement, node);
            }
        }
    }

    /// <summary>1 つの宣言を検査する。</summary>
    private static void Check(GlslRequirements requirement, ShaderSyntaxNode node)
    {
        switch (node)
        {
            case FunctionDeclarationSyntax function when HasUnsized(function.ReturnType.ArrayRanks):
                requirement.Report(GlslRuleDescriptors.ArraySizeRequired, function.ReturnType.NameToken, "関数の戻り値");
                break;

            case ParameterSyntax parameter when HasUnsized([.. parameter.ArrayRankTokens, .. parameter.Type.ArrayRanks]):
                requirement.Report(GlslRuleDescriptors.ArraySizeRequired, parameter.Type.NameToken, "仮引数");
                break;

            // 構造体のメンバー (structArrayCheck)。
            case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: StructDeclarationSyntax { Keyword.Text: "struct" } } field } member:
                if (HasUnsized([.. member.ArrayRankTokens, .. field.Type.ArrayRanks]))
                {
                    requirement.Report(GlslRuleDescriptors.ArraySizeRequired, member.NameToken, "構造体のメンバー");
                }

                break;

            case VariableDeclaratorSyntax { Initializer: null } declarator:
                CheckVariable(requirement, declarator);
                break;

            default:
                break;
        }
    }

    /// <summary>初期化子の無い変数、ブロックのメンバーとインスタンスを検査する (arraySizesCheck)。</summary>
    private static void CheckVariable(GlslRequirements requirement, VariableDeclaratorSyntax declarator)
    {
        ImmutableArray<ShaderSyntaxToken> modifiers;
        ImmutableArray<ShaderSyntaxToken> ranks;
        ShaderSyntaxToken at = declarator.NameToken;
        bool lastMember = false;

        switch (declarator.Parent)
        {
            // 名前の無いブロックのメンバー。
            case VariableDeclarationSyntax { Parent: InterfaceBlockDeclarationSyntax block } member:
                modifiers = [.. block.Modifiers, .. member.ModifierTokens];
                ranks = [.. declarator.ArrayRankTokens, .. member.Type.ArrayRanks];
                lastMember = IsLast(block.Members, declarator);
                break;

            // 名前のあるブロックのメンバー。
            case VariableDeclarationSyntax { Parent: StructDeclarationSyntax block } member:
                modifiers = [.. block.Modifiers, block.Keyword, .. member.ModifierTokens];
                ranks = [.. declarator.ArrayRankTokens, .. member.Type.ArrayRanks];
                lastMember = IsLast(block.Members, declarator);
                break;

            case VariableDeclarationSyntax { Parent: CompilationUnitSyntax or LocalDeclarationStatementSyntax } variable:
                modifiers = variable.ModifierTokens;
                ranks = [.. declarator.ArrayRankTokens, .. variable.Type.ArrayRanks];
                break;

            // 名前のあるブロックのインスタンス。glslang はブロックの位置で報告する。
            case StructDeclarationSyntax { Keyword.Text: not "struct" } block:
                modifiers = [.. block.Modifiers, block.Keyword];
                ranks = declarator.ArrayRankTokens;
                at = modifiers[0];
                break;

            default:
                return;
        }

        ImmutableArray<bool> unsized = Dimensions(ranks);
        bool perView = HasStorage(modifiers, "perviewNV") && requirement.Stage == GlslShaderStage.Mesh && HasStorage(modifiers, "out")
                       && declarator.Parent is VariableDeclarationSyntax;

        // perviewNV の出力は、ビューの次元 (ブロックのメンバーは一番外、ほかは外から 2 番目) を持たなければならない。
        // その次元は省くか gl_MaxMeshViewCountNV にする (glslang が gl_MaxMeshViewCountNV に決める。checkAndResizeMeshViewDim)。
        int viewDimension = declarator.Parent is VariableDeclarationSyntax { Parent: InterfaceBlockDeclarationSyntax or StructDeclarationSyntax } ? 0 : 1;

        if (perView && unsized.Length <= viewDimension)
        {
            requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, at, declarator.Name, "perviewNV の出力は、ビューの次元を持つ配列でなければなりません。");
            return;
        }

        if (perView && !unsized[viewDimension]
            && GlslLayoutQualifier.Evaluate(DimensionTokens(ranks)[viewDimension], t => t.Text == "gl_MaxMeshViewCountNV" ? new GlslConstant(MaxMeshViewCount, GlslConstantKind.Int, IsLiteral: false) : null) is { } viewCount
            && viewCount.Value != MaxMeshViewCount)
        {
            requirement.Report(GlslRuleDescriptors.TypeNotAllowedHere, at, declarator.Name, "perviewNV の出力のビューの次元の大きさは、gl_MaxMeshViewCountNV にするか省かなければなりません。");
        }

        if (unsized.IsEmpty)
        {
            return;
        }

        // 内側の次元の大きさは省けない。ただし perviewNV のビューの次元は除く。
        if (unsized.Select((u, i) => u && i > 0 && !(HasStorage(modifiers, "perviewNV") && i == viewDimension)).Any(u => u))
        {
            requirement.Report(GlslRuleDescriptors.ArraySizeRequired, at, "配列の配列の内側の次元");
        }

        if (!requirement.IsEs || !unsized[0] || IsImplicitlySizedAllowed(requirement, modifiers, lastMember, at))
        {
            return;
        }

        requirement.Report(GlslRuleDescriptors.ArraySizeRequired, at, "ES の、初期化子の無い変数とブロック");
    }

    /// <summary>ES で大きさを省ける例外か。</summary>
    private static bool IsImplicitlySizedAllowed(GlslRequirements requirement, ImmutableArray<ShaderSyntaxToken> modifiers, bool lastMember, ShaderSyntaxToken at)
    {
        bool input = HasStorage(modifiers, "in");
        bool output = HasStorage(modifiers, "out");
        bool patch = HasStorage(modifiers, "patch");
        bool es320 = requirement.Version >= 320;

        bool io = requirement.Stage switch
        {
            GlslShaderStage.Geometry => input && (es320 || IsOn(requirement, at, "GL_EXT_geometry_shader", "GL_OES_geometry_shader")),
            GlslShaderStage.TessellationControl => (input || output && !patch) && (es320 || IsOn(requirement, at, "GL_EXT_tessellation_shader", "GL_OES_tessellation_shader")),
            GlslShaderStage.TessellationEvaluation => (input && !patch || output) && (es320 || IsOn(requirement, at, "GL_EXT_tessellation_shader", "GL_OES_tessellation_shader")),
            GlslShaderStage.Mesh => output && (es320 || IsOn(requirement, at, "GL_NV_mesh_shader", "GL_EXT_mesh_shader")),
            _ => false,
        };

        return io
               || HasStorage(modifiers, "buffer") && lastMember
               || HasStorage(modifiers, "uniform") && lastMember && requirement.IsOn("GL_EXT_uniform_buffer_unsized_array", at);
    }

    /// <summary>どれかの拡張を有効にしているか。</summary>
    private static bool IsOn(GlslRequirements requirement, ShaderSyntaxToken at, params string[] extensions)
        => extensions.Any(e => requirement.IsOn(e, at));

    /// <summary>次元ごとに、大きさを省いているか (外側から)。</summary>
    private static ImmutableArray<bool> Dimensions(ImmutableArray<ShaderSyntaxToken> ranks)
    {
        ImmutableArray<bool>.Builder dimensions = ImmutableArray.CreateBuilder<bool>();

        for (int i = 0; i < ranks.Length; i++)
        {
            if (ranks[i].Kind == ShaderSyntaxKind.OpenBracketToken)
            {
                dimensions.Add(i + 1 < ranks.Length && ranks[i + 1].Kind == ShaderSyntaxKind.CloseBracketToken);
            }
        }

        return dimensions.ToImmutable();
    }

    /// <summary>次元ごとの、角括弧の中のトークン。</summary>
    private static ImmutableArray<ImmutableArray<ShaderSyntaxToken>> DimensionTokens(ImmutableArray<ShaderSyntaxToken> ranks)
    {
        ImmutableArray<ImmutableArray<ShaderSyntaxToken>>.Builder dimensions = ImmutableArray.CreateBuilder<ImmutableArray<ShaderSyntaxToken>>();
        ImmutableArray<ShaderSyntaxToken>.Builder current = ImmutableArray.CreateBuilder<ShaderSyntaxToken>();
        int depth = 0;

        foreach (ShaderSyntaxToken token in ranks)
        {
            if (token.Kind == ShaderSyntaxKind.OpenBracketToken && depth++ == 0)
            {
                current.Clear();
            }
            else if (token.Kind == ShaderSyntaxKind.CloseBracketToken && --depth == 0)
            {
                dimensions.Add(current.ToImmutable());
            }
            else
            {
                current.Add(token);
            }
        }

        return dimensions.ToImmutable();
    }

    /// <summary>大きさを省いた次元があるか。</summary>
    private static bool HasUnsized(ImmutableArray<ShaderSyntaxToken> ranks) => Dimensions(ranks).Any(u => u);

    /// <summary>ブロックの最後のメンバーか。</summary>
    private static bool IsLast(ImmutableArray<DeclarationSyntax> members, VariableDeclaratorSyntax declarator)
        => members.OfType<VariableDeclarationSyntax>().LastOrDefault() is { } last && last.Variables.LastOrDefault() == declarator;

    /// <summary>修飾子 (<c>layout(…)</c> の外) に、その記憶域があるか。</summary>
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
}
