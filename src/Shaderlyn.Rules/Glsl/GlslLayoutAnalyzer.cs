using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Glsl;
using Shaderlyn.Glsl.Syntax;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Glsl;

/// <summary>
/// <c>layout(…)</c> の名前と値を検査する (GL0001 / GL0002 / GL0102 / GL0103)。
/// glslang 16.6.0 の ParseHelper.cpp の <c>setLayoutQualifier</c> (2 つ) を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 名前ごとに、使えるバージョン・プロファイル・拡張・ステージ・向け (Vulkan か) と、値の範囲を確かめる。
/// 名前は glslang と同じく、大文字と小文字を区別しない。
/// </para>
/// <para>
/// 値の式は、リテラル・<c>const</c> の変数・演算子を畳んで求める。求められない値 (組み込みの定数など) は検査しない。
/// ステージが分からないファイル (<c>.glsl</c>) では、ステージによって変わる名前を検査しない。
/// </para>
/// </remarks>
internal sealed partial class GlslLayoutAnalyzer : SemanticRuleAnalyzer
{
    // glslang の既定の資源の上限 (glslang-default-resource-limits)。
    private const int MaxTransformFeedbackBuffers = 4;
    private const int MaxTransformFeedbackInterleavedComponents = 64;
    private const int MaxGeometryOutputVertices = 256;
    private const int MaxMeshOutputVertices = 256;
    private const int MaxMeshOutputPrimitivesNv = 512;
    private const int MaxMeshOutputPrimitivesExt = 256;

    // glslang の TQualifier の上限。
    private const int LayoutLocationEnd = 0xFFF;
    private const int LayoutComponentEnd = 4;
    private const int LayoutSetEnd = 0x3F;
    private const int LayoutBindingEnd = 0xFFFF;
    private const int LayoutXfbBufferEnd = 0xF;
    private const int LayoutXfbStrideEnd = 0x3FFF;
    private const int LayoutXfbOffsetEnd = 0x1FFF;
    private const int LayoutBankEnd = 0xF;

    private static readonly string[] EsFloatFormats = ["rgba32f", "rgba16f", "r32f", "rgba8", "rgba8_snorm"];
    private static readonly string[] FloatFormats =
    [
        "rg32f", "rg16f", "r11f_g11f_b10f", "r16f", "rgba16", "rgb10_a2", "rg16", "rg8", "r16", "r8",
        "rgba16_snorm", "rg16_snorm", "rg8_snorm", "r16_snorm", "r8_snorm",
    ];
    private static readonly string[] EsIntFormats = ["rgba32i", "rgba16i", "rgba8i", "r32i"];
    private static readonly string[] IntFormats = ["rg32i", "rg16i", "rg8i", "r16i", "r8i", "r64i"];
    private static readonly string[] EsUintFormats = ["rgba32ui", "rgba16ui", "rgba8ui", "r32ui"];
    private static readonly string[] UintFormats =
    [
        "rg32ui", "rg16ui", "rgb10_a2ui", "rg8ui", "r16ui", "r8ui", "r64ui",
        "size1x8", "size1x16", "size1x32", "size2x32", "size4x32",
    ];

    /// <summary>イメージの形式の名前すべて。</summary>
    internal static readonly ImmutableHashSet<string> ImageFormats =
        [.. EsFloatFormats, .. FloatFormats, .. EsIntFormats, .. IntFormats, .. EsUintFormats, .. UintFormats];

    private static readonly string[] BlendEquations =
    [
        "blend_support_multiply", "blend_support_screen", "blend_support_overlay", "blend_support_darken", "blend_support_lighten",
        "blend_support_colordodge", "blend_support_colorburn", "blend_support_hardlight", "blend_support_softlight",
        "blend_support_difference", "blend_support_exclusion", "blend_support_hsl_hue", "blend_support_hsl_saturation",
        "blend_support_hsl_color", "blend_support_hsl_luminosity", "blend_support_all_equations",
    ];

    private static readonly string[] AepCoreGpuShader5 = ["GL_ARB_gpu_shader5", "GL_NV_gpu_shader5"];
    private static readonly string[] AepMeshShader = ["GL_NV_mesh_shader", "GL_EXT_mesh_shader"];
    private static readonly string[] ExplicitLocation = ["GL_ARB_separate_shader_objects", "GL_ARB_explicit_attrib_location"];

    /// <inheritdoc/>
    protected override bool AnalyzesGlsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        GlslRuleDescriptors.NotAvailable, GlslRuleDescriptors.NotAvailableInStage,
        GlslRuleDescriptors.QualifierNotAllowed, GlslRuleDescriptors.UnknownLayoutIdentifier,
        GlslRuleDescriptors.InvalidLayoutValue, GlslRuleDescriptors.LayoutRequired,
    ];

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsGlsl)
        {
            return;
        }

        HashSet<(string? File, int Start, string Message)> reported = [];
        Dictionary<AnalyzedProgram, Checker?> checkers = [];

        foreach ((ShaderSyntaxNode node, AnalyzedProgram program) in compilation.EnumerateRuleNodes())
        {
            if (!checkers.TryGetValue(program, out Checker? checker))
            {
                checker = checkers[program] = GlslRequirements.Create(context, compilation, program, reported) is { } requirement
                    ? new Checker(requirement, compilation, program)
                    : null;
            }

            ImmutableArray<ShaderSyntaxToken> modifiers = node switch
            {
                VariableDeclarationSyntax variable => variable.ModifierTokens,
                ParameterSyntax parameter => parameter.ModifierTokens,
                FunctionDeclarationSyntax function => function.ModifierTokens,
                StructDeclarationSyntax block when IsBlock(block) => [.. block.Modifiers, block.Keyword],
                InterfaceBlockDeclarationSyntax block => block.Modifiers,
                IncompleteDeclarationSyntax incomplete => incomplete.Tokens,
                _ => [],
            };

            if (checker is null || modifiers.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (GlslLayoutEntry entry in GlslLayoutQualifier.Read(modifiers))
            {
                checker.Check(entry);
            }

            checker.Visit(node);
        }
    }

    /// <summary>1 つの木の <c>layout(…)</c> を検査する。</summary>
    private sealed partial class Checker
    {
        private readonly GlslRequirements _r;
        private readonly GlslTypeSystem? _types;
        private readonly Core.Syntax.SyntaxNode _root;
        private readonly Dictionary<string, GlslConstant?> _constants = new(StringComparer.Ordinal);
        private readonly HashSet<string> _variables = new(StringComparer.Ordinal);
        private readonly HashSet<string> _specializationConstants = new(StringComparer.Ordinal);
        private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
        private readonly HashSet<long> _constantIds = [];

        public Checker(GlslRequirements requirement, ShaderCompilation compilation, AnalyzedProgram program)
        {
            _r = requirement;
            _types = compilation.TypeSystem.ForTree(program.Tree) as GlslTypeSystem;
            _root = program.Tree.Root;
            GlslConstantEvaluator evaluator = new(compilation.GetExpressionTypeBinder(program));

            // 関数の外の変数。const なら値を、そうでなければ名前だけを覚える。
            foreach (VariableDeclarationSyntax declaration in program.Tree.Root.DescendantNodesAndSelf().OfType<VariableDeclarationSyntax>())
            {
                if (declaration.Ancestors().Any(a => a is FunctionDeclarationSyntax))
                {
                    continue;
                }

                bool isConst = declaration.HasModifier("const");

                // 特殊化の定数 (layout(constant_id = 1) const int n = 4;) の値は、glslang では畳まない。
                bool isSpecialization = GlslLayoutQualifier.Read(declaration.ModifierTokens).Any(e => e.Name.Text == "constant_id");

                foreach (VariableDeclaratorSyntax variable in declaration.Variables)
                {
                    if (isSpecialization)
                    {
                        _specializationConstants.Add(variable.Name);
                    }
                    else if (isConst && !variable.IsArray && variable.Initializer is { } initializer)
                    {
                        _constants[variable.Name] = evaluator.Evaluate(initializer);
                    }
                    else if (!isConst)
                    {
                        _variables.Add(variable.Name);
                    }
                }
            }

            // 宣言された名前 (関数の中・構造体のメンバー・関数・型を含む)。layout の値の、宣言されていない名前を見分ける。
            foreach (Core.Syntax.SyntaxNode node in program.Tree.Root.DescendantNodesAndSelf())
            {
                string? declared = node switch
                {
                    VariableDeclaratorSyntax variable => variable.Name,
                    ParameterSyntax parameter => parameter.Name,
                    FunctionDeclarationSyntax function => function.Name,
                    StructDeclarationSyntax structure => structure.NameToken?.Text,
                    _ => null,
                };

                if (declared is not null)
                {
                    _declared.Add(declared);
                }
            }
        }

        private GlslShaderStage? Stage => _r.Stage;

        public void Check(GlslLayoutEntry entry)
        {
            string id = entry.Name.Text.ToLowerInvariant();

            if (entry.HasValue)
            {
                CheckValue(entry, id);
            }
            else if (!CheckName(entry.Name, id) && Stage is not null)
            {
                Invalid(GlslRuleDescriptors.UnknownLayoutIdentifier, entry.Name,
                    $"'{entry.Name.Text}' は layout の名前にありません ({StageName}で値を取らない名前として)。綴りを確かめるか、値を取る名前なら '{entry.Name.Text} = 値' と書いてください。");
            }
        }

        private string StageName => Stage is { } stage ? GlslRequirements.DescribeStage(stage) : "このステージ";

        private void Invalid(DiagnosticDescriptor descriptor, ShaderSyntaxToken at, string message) => _r.Report(descriptor, at, message);

        private bool IsStage(params GlslShaderStage[] stages) => Stage is { } stage && stages.Contains(stage);

        /// <summary>値を取らない名前 (glslang の 1 つ目の setLayoutQualifier)。知っている名前なら <see langword="true"/>。</summary>
        private bool CheckName(ShaderSyntaxToken at, string id)
        {
            switch (id)
            {
                case "column_major" or "row_major" or "std140":
                    return true;

                case "packed" or "shared":
                    _r.SpvRemoved(at, id);
                    return true;

                case "std430":
                    _r.RequireProfile(at, GlslProfileMask.Es | GlslProfileMask.Core | GlslProfileMask.Compatibility, "'std430'");
                    _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 430, ["GL_ARB_shader_storage_buffer_object"], "'std430'");
                    _r.ProfileRequires(at, GlslProfileMask.Es, 310, [], "'std430'");
                    return true;

                case "scalar":
                    _r.RequireVulkan(at, "scalar");
                    _r.RequireExtensions(at, ["GL_EXT_scalar_block_layout"], "scalar のブロックのレイアウト");
                    return true;

                case "descriptor_heap":
                    _r.RequireExtensions(at, ["GL_EXT_descriptor_heap"], "'descriptor_heap'");
                    _r.RequireVulkan(at, "descriptor_heap");
                    return true;

                case "push_constant":
                    _r.RequireVulkan(at, "push_constant");
                    return true;

                case "buffer_reference":
                    _r.RequireVulkan(at, "buffer_reference");
                    _r.RequireExtensions(at, ["GL_EXT_buffer_reference"], "'buffer_reference'");
                    return true;

                case "buffer_type":
                    _r.RequireVulkan(at, "buffer_type");
                    _r.RequireExtensions(at, ["GL_EXT_structured_descriptor_heap"], "'buffer_type'");
                    return true;

                case "bindless_sampler" or "bindless_image" or "bound_sampler" or "bound_image":
                    _r.RequireExtensions(at, ["GL_ARB_bindless_texture"], $"'{id}'");
                    return true;

                default:
                    break;
            }

            if (CheckFormat(at, id))
            {
                return true;
            }

            // ステージが分からなければ、ステージで変わる名前は知っているものとして扱う。
            if (Stage is null)
            {
                return true;
            }

            if (IsStage(GlslShaderStage.Geometry, GlslShaderStage.TessellationEvaluation, GlslShaderStage.Mesh) && CheckPrimitiveName(at, id))
            {
                return true;
            }

            if (Stage == GlslShaderStage.Fragment && CheckFragmentName(at, id))
            {
                return true;
            }

            if (IsStage(GlslShaderStage.Vertex, GlslShaderStage.TessellationControl, GlslShaderStage.TessellationEvaluation, GlslShaderStage.Geometry))
            {
                if (id == "viewport_relative")
                {
                    _r.RequireExtensions(at, ["GL_NV_viewport_array2"], "'viewport_relative'");
                    return true;
                }
            }
            else if (IsStage(GlslShaderStage.RayGeneration, GlslShaderStage.Intersection, GlslShaderStage.AnyHit, GlslShaderStage.ClosestHit, GlslShaderStage.Miss, GlslShaderStage.Callable))
            {
                switch (id)
                {
                    case "shaderrecordnv":
                        _r.RequireExtensions(at, ["GL_NV_ray_tracing"], "'shaderRecordNV'");
                        return true;
                    case "shaderrecordext":
                        _r.RequireExtensions(at, ["GL_EXT_ray_tracing"], "'shaderRecordEXT'");
                        return true;
                    case "hitobjectshaderrecordnv":
                        _r.RequireExtensions(at, ["GL_NV_shader_invocation_reorder"], "'hitobjectshaderrecordnv'");
                        return true;
                    case "hitobjectshaderrecordext":
                        _r.RequireExtensions(at, ["GL_EXT_shader_invocation_reorder"], "'hitobjectshaderrecordext'");
                        return true;
                    default:
                        break;
                }
            }

            if (IsStage(GlslShaderStage.Compute, GlslShaderStage.Task, GlslShaderStage.Mesh) && id.StartsWith("derivative_group_", StringComparison.Ordinal))
            {
                if (id is "derivative_group_quadsnv" or "derivative_group_linearnv")
                {
                    _r.RequireExtensions(at, ["GL_NV_compute_shader_derivatives"], "コンピュートシェーダーの微分");

                    if (Stage != GlslShaderStage.Compute)
                    {
                        Invalid(GlslRuleDescriptors.UnknownLayoutIdentifier, at, $"'{at.Text}' はコンピュートシェーダーにだけ書けます。");
                    }

                    return true;
                }

                if (id is "derivative_group_quadskhr" or "derivative_group_linearkhr")
                {
                    _r.RequireExtensions(at, ["GL_KHR_compute_shader_derivatives"], "コンピュートシェーダーの微分");
                    return true;
                }
            }

            if (Stage == GlslShaderStage.Compute && id == "tile_attachmentqcom")
            {
                _r.RequireExtensions(at, ["GL_QCOM_tile_shading"], "'tile_attachmentQCOM'");
                return true;
            }

            if (id == "primitive_culling")
            {
                _r.RequireExtensions(at, ["GL_EXT_ray_flags_primitive_culling"], "'primitive_culling'");
                return true;
            }

            if (id == "quad_derivatives")
            {
                QuadControl(at, "'quad_derivatives'");
                return true;
            }

            return false;
        }

        private void QuadControl(ShaderSyntaxToken at, string feature)
        {
            _r.RequireProfile(at, GlslProfileMask.Compatibility | GlslProfileMask.Core | GlslProfileMask.Es, feature);
            _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 140, ["GL_EXT_shader_quad_control"], feature);
            _r.ProfileRequires(at, GlslProfileMask.Es, 310, ["GL_EXT_shader_quad_control"], feature);
        }

        /// <summary>イメージの形式 (<c>rgba8</c> など)。</summary>
        private bool CheckFormat(ShaderSyntaxToken at, string id)
        {
            bool esFormat = EsFloatFormats.Contains(id) || EsIntFormats.Contains(id) || EsUintFormats.Contains(id);

            if (!esFormat && !FloatFormats.Contains(id) && !IntFormats.Contains(id) && !UintFormats.Contains(id))
            {
                return false;
            }

            if (!esFormat)
            {
                _r.RequireProfile(at, GlslProfileMask.None | GlslProfileMask.Core | GlslProfileMask.Compatibility, $"イメージの形式 '{id}'");
            }

            _r.ProfileRequires(at, GlslProfileMask.None | GlslProfileMask.Core | GlslProfileMask.Compatibility, 420, ["GL_ARB_shader_image_load_store"], "イメージの読み書き");
            _r.ProfileRequires(at, GlslProfileMask.Es, 310, ["GL_ARB_shader_image_load_store"], "イメージの読み書き");
            return true;
        }

        /// <summary>ジオメトリ・テッセレーションの評価・メッシュのシェーダーの、プリミティブの名前。</summary>
        private bool CheckPrimitiveName(ShaderSyntaxToken at, string id)
        {
            if (id == "triangles")
            {
                return true;
            }

            if (IsStage(GlslShaderStage.Geometry, GlslShaderStage.Mesh))
            {
                if (id is "points" or "lines")
                {
                    return true;
                }

                if (Stage == GlslShaderStage.Geometry)
                {
                    if (id is "line_strip" or "lines_adjacency" or "triangles_adjacency" or "triangle_strip")
                    {
                        return true;
                    }

                    if (id == "passthrough")
                    {
                        _r.RequireExtensions(at, ["GL_NV_geometry_shader_passthrough"], "ジオメトリシェーダーの passthrough");
                        return true;
                    }
                }

                return false;
            }

            return id is "quads" or "isolines" or "equal_spacing" or "fractional_even_spacing" or "fractional_odd_spacing"
                or "cw" or "ccw" or "point_mode";
        }

        /// <summary>フラグメントシェーダーの名前。</summary>
        private bool CheckFragmentName(ShaderSyntaxToken at, string id)
        {
            switch (id)
            {
                case "origin_upper_left" or "pixel_center_integer":
                    _r.RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility | GlslProfileMask.None, $"'{id}'");

                    if (_r.Profile == GlslProfileMask.None)
                    {
                        _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 140, ["GL_ARB_fragment_coord_conventions"], $"'{id}'");
                    }

                    return true;

                case "early_fragment_tests":
                    _r.ProfileRequires(at, GlslProfileMask.NotEs, 420, ["GL_ARB_shader_image_load_store"], "'early_fragment_tests'");
                    _r.ProfileRequires(at, GlslProfileMask.Es, 310, [], "'early_fragment_tests'");
                    return true;

                case "early_and_late_fragment_tests_amd":
                    _r.ProfileRequires(at, GlslProfileMask.NotEs, 420, ["GL_AMD_shader_early_and_late_fragment_tests"], $"'{at.Text}'");
                    _r.ProfileRequires(at, GlslProfileMask.Es, 310, [], $"'{at.Text}'");
                    return true;

                case "post_depth_coverage":
                    _r.RequireExtensions(at, ["GL_ARB_post_depth_coverage", "GL_EXT_post_depth_coverage"], "'post_depth_coverage'");
                    return true;

                case "non_coherent_color_attachment_readext" or "non_coherent_depth_attachment_readext" or "non_coherent_stencil_attachment_readext":
                    _r.RequireExtensions(at, ["GL_EXT_shader_tile_image"], $"'{at.Text}'");
                    return true;

                case "depth_any" or "depth_greater" or "depth_less" or "depth_unchanged":
                    _r.RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility | GlslProfileMask.Es, "深度の layout 修飾子");
                    _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 420, ["GL_ARB_conservative_depth"], "深度の layout 修飾子");
                    _r.ProfileRequires(at, GlslProfileMask.Es, 0, ["GL_EXT_conservative_depth"], "深度の layout 修飾子");
                    return true;

                case "stencil_ref_unchanged_front_amd" or "stencil_ref_greater_front_amd" or "stencil_ref_less_front_amd"
                    or "stencil_ref_unchanged_back_amd" or "stencil_ref_greater_back_amd" or "stencil_ref_less_back_amd":
                    _r.RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, "ステンシルの layout 修飾子");
                    _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 420, [], "ステンシルの layout 修飾子");
                    return true;

                case "pixel_interlock_ordered" or "pixel_interlock_unordered" or "sample_interlock_ordered" or "sample_interlock_unordered"
                    or "shading_rate_interlock_ordered" or "shading_rate_interlock_unordered":
                    _r.RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, "フラグメントシェーダーのインターロックの layout 修飾子");
                    _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 450, [], "フラグメントシェーダーのインターロックの layout 修飾子");
                    _r.RequireExtensions(at, ["GL_ARB_fragment_shader_interlock"], $"'{id}'");

                    if (id.StartsWith("shading_rate", StringComparison.Ordinal))
                    {
                        _r.RequireExtensions(at, ["GL_NV_shading_rate_image"], $"'{id}'");
                    }

                    return true;

                case "override_coverage":
                    _r.RequireExtensions(at, ["GL_NV_sample_mask_override_coverage"], "'override_coverage'");
                    return true;

                case "full_quads":
                    QuadControl(at, "'full_quads'");
                    return true;

                case "non_coherent_attachment_readqcom" or "tile_attachmentqcom":
                    _r.RequireExtensions(at, ["GL_QCOM_tile_shading"], $"'{at.Text}'");
                    return true;

                default:
                    break;
            }

            if (id.StartsWith("blend_support", StringComparison.Ordinal))
            {
                if (BlendEquations.Contains(id))
                {
                    _r.ProfileRequires(at, GlslProfileMask.Es, 320, ["GL_KHR_blend_equation_advanced"], "ブレンドの式");
                    _r.ProfileRequires(at, GlslProfileMask.NotEs, 0, ["GL_KHR_blend_equation_advanced"], "ブレンドの式");
                }
                else
                {
                    Invalid(GlslRuleDescriptors.UnknownLayoutIdentifier, at, $"'{at.Text}' というブレンドの式はありません。");
                }

                return true;
            }

            return false;
        }

        /// <summary>値を取る名前 (glslang の 2 つ目の setLayoutQualifier)。</summary>
        private void CheckValue(GlslLayoutEntry entry, string id)
        {
            ShaderSyntaxToken at = entry.Name;
            string name = at.Text;
            GlslConstant? constant = GlslLayoutQualifier.Evaluate(entry.Value, Resolve);
            bool nonLiteral = false;
            long value = 0;

            if (entry.Value.FirstOrDefault(IsUndeclared) is { } undeclared)
            {
                nonLiteral = true;
                Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, $"'{name}' の値の '{undeclared.Text}' は宣言されていません。");
            }
            else if (entry.Value.Any(t => t.Kind == ShaderSyntaxKind.IdentifierToken && _specializationConstants.Contains(t.Text)))
            {
                // 特殊化の定数は定数だが、値はリテラルにならない。
                nonLiteral = true;
            }
            else if (constant is { } c)
            {
                if (c.Kind is GlslConstantKind.Float or GlslConstantKind.Bool)
                {
                    Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, $"'{name}' の値は整数でなければなりません。");
                }

                value = c.Kind == GlslConstantKind.Uint ? unchecked((int)(uint)c.Integer) : c.Integer;

                if (!c.IsLiteral)
                {
                    const string feature = "リテラルでない layout の値";
                    _r.RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, feature);
                    _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 440, ["GL_ARB_enhanced_layouts"], feature);
                }
            }
            else if (IsCertainlyNotConstant(entry.Value))
            {
                nonLiteral = true;

                if (id != "heap_offset")
                {
                    Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, $"'{name}' の値は定数の式でなければなりません。");
                }
            }
            else
            {
                // 値が分からない。値によらない検査だけをする。
                CheckValueName(at, id, name, value: null, nonLiteral: false);
                return;
            }

            if (value < 0)
            {
                Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, $"'{name}' の値は負にできません。");
                return;
            }

            CheckValueName(at, id, name, value, nonLiteral);
        }

        /// <summary>値を取る名前ごとの検査。<paramref name="value"/> が <see langword="null"/> なら、値によらない検査だけをする。</summary>
        private void CheckValueName(ShaderSyntaxToken at, string id, string name, long? value, bool nonLiteral)
        {
            void Value(string message) => Invalid(GlslRuleDescriptors.InvalidLayoutValue, at, message);

            void Literal()
            {
                if (nonLiteral)
                {
                    Value($"'{name}' の値は整数のリテラルでなければなりません。");
                }
            }

            static bool IsPow2(long v) => v > 0 && (v & (v - 1)) == 0;

            switch (id)
            {
                case "offset":
                    if (_r.SpirvVersion == 0)
                    {
                        _r.RequireProfile(at, GlslProfileMask.Es | GlslProfileMask.Core | GlslProfileMask.Compatibility, "'offset'");
                        _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 420, ["GL_ARB_enhanced_layouts", "GL_ARB_shader_atomic_counters"], "'offset'");
                        _r.ProfileRequires(at, GlslProfileMask.Es, 310, [], "'offset'");
                    }

                    Literal();
                    return;

                case "align":
                    if (_r.SpirvVersion == 0)
                    {
                        _r.RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, "ブロックのメンバーの align");
                        _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 440, ["GL_ARB_enhanced_layouts"], "ブロックのメンバーの align");
                    }

                    if (value is { } align && !IsPow2(align))
                    {
                        Value($"'{name}' の値は 2 のべき乗でなければなりません。");
                    }

                    Literal();
                    return;

                case "location":
                    _r.ProfileRequires(at, GlslProfileMask.Es, 300, [], "'location'");
                    _r.ProfileRequires(at, GlslProfileMask.NotEs, 330, ExplicitLocation, "'location'");

                    if (value >= LayoutLocationEnd)
                    {
                        Value($"'{name}' の値が大きすぎます ({LayoutLocationEnd - 1} まで)。");
                    }

                    Literal();
                    return;

                case "set":
                    if (value >= LayoutSetEnd)
                    {
                        Value($"'{name}' の値が大きすぎます ({LayoutSetEnd - 1} まで)。");
                    }

                    if (value is not 0)
                    {
                        _r.RequireVulkan(at, "descriptor set");
                    }

                    Literal();
                    return;

                case "binding":
                    _r.ProfileRequires(at, GlslProfileMask.NotEs, 420, ["GL_ARB_shading_language_420pack"], "'binding'");
                    _r.ProfileRequires(at, GlslProfileMask.Es, 310, [], "'binding'");

                    if (value >= LayoutBindingEnd)
                    {
                        Value($"'{name}' の値が大きすぎます ({LayoutBindingEnd - 1} まで)。");
                    }

                    Literal();
                    return;

                case "constant_id":
                    _r.RequireSpv(at, "constant_id");

                    if (value is { } constantId && !_constantIds.Add(constantId))
                    {
                        Value($"特殊化の定数の id {constantId.ToString(CultureInfo.InvariantCulture)} は、もう使われています。");
                    }

                    Literal();
                    return;

                case "component":
                    _r.RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, "'component'");
                    _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 440, ["GL_ARB_enhanced_layouts"], "'component'");

                    if (value >= LayoutComponentEnd)
                    {
                        Value($"'{name}' の値が大きすぎます ({LayoutComponentEnd - 1} まで)。");
                    }

                    Literal();
                    return;

                case "xfb_buffer" or "xfb_offset" or "xfb_stride":
                    const string xfb = "変換フィードバックの修飾子";
                    _r.RequireStage(at, [GlslShaderStage.Vertex, GlslShaderStage.Geometry, GlslShaderStage.TessellationControl, GlslShaderStage.TessellationEvaluation], xfb);
                    _r.RequireProfile(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, xfb);
                    _r.ProfileRequires(at, GlslProfileMask.Core | GlslProfileMask.Compatibility, 440, ["GL_ARB_enhanced_layouts"], xfb);

                    if (id == "xfb_buffer" && value >= MaxTransformFeedbackBuffers)
                    {
                        Value($"'{name}' の値が大きすぎます (gl_MaxTransformFeedbackBuffers は {MaxTransformFeedbackBuffers})。");
                    }
                    else if (id == "xfb_buffer" && value >= LayoutXfbBufferEnd)
                    {
                        Value($"'{name}' の値が大きすぎます ({LayoutXfbBufferEnd - 1} まで)。");
                    }
                    else if (id == "xfb_offset" && value >= LayoutXfbOffsetEnd)
                    {
                        Value($"'{name}' の値が大きすぎます ({LayoutXfbOffsetEnd - 1} まで)。");
                    }
                    else if (id == "xfb_stride" && value > 4 * MaxTransformFeedbackInterleavedComponents)
                    {
                        Value($"'{name}' の値の 1/4 が大きすぎます (gl_MaxTransformFeedbackInterleavedComponents は {MaxTransformFeedbackInterleavedComponents})。");
                    }
                    else if (id == "xfb_stride" && value >= LayoutXfbStrideEnd)
                    {
                        Value($"'{name}' の値が大きすぎます ({LayoutXfbStrideEnd - 1} まで)。");
                    }

                    Literal();
                    return;

                case "input_attachment_index":
                    _r.RequireVulkan(at, "input_attachment_index");
                    Literal();
                    return;

                case "num_views":
                    _r.RequireExtensions(at, ["GL_OVR_multiview", "GL_OVR_multiview2"], "'num_views'");
                    Literal();
                    return;

                case "buffer_reference_align":
                    _r.RequireExtensions(at, ["GL_EXT_buffer_reference"], "'buffer_reference_align'");

                    if (value is { } referenceAlign && !IsPow2(referenceAlign))
                    {
                        Value($"'{name}' の値は 2 のべき乗でなければなりません。");
                    }

                    Literal();
                    return;

                case "bank":
                    _r.RequireExtensions(at, ["GL_NV_push_constant_bank"], "'bank'");

                    if (nonLiteral)
                    {
                        Literal();
                    }
                    else if (value >= LayoutBankEnd)
                    {
                        Value($"'{name}' の値が範囲の外です ({LayoutBankEnd - 1} まで)。");
                    }

                    return;

                case "member_offset":
                    _r.RequireExtensions(at, ["GL_NV_push_constant_bank"], "'member_offset'");
                    Literal();
                    return;

                case "descriptor_stride":
                    _r.RequireExtensions(at, ["GL_EXT_descriptor_heap"], "'descriptor_stride'");
                    _r.RequireVulkan(at, "descriptor_stride");

                    if (value is { } stride && !IsPow2(stride))
                    {
                        Value($"'{name}' の値は 2 のべき乗でなければなりません。");
                    }

                    return;

                case "descriptor_size":
                    _r.RequireExtensions(at, ["GL_EXT_descriptor_heap"], "'descriptor_size'");
                    _r.RequireExtensions(at, ["GL_EXT_structured_descriptor_heap"], "'descriptor_size'");
                    _r.RequireVulkan(at, "descriptor_size");
                    Literal();
                    return;

                case "heap_offset":
                    _r.RequireExtensions(at, ["GL_EXT_descriptor_heap"], "'heap_offset'");
                    _r.RequireExtensions(at, ["GL_EXT_structured_descriptor_heap"], "'heap_offset'");
                    _r.RequireVulkan(at, "heap_offset");
                    return;

                default:
                    break;
            }

            if (id == "secondary_view_offset"
                && IsStage(GlslShaderStage.Vertex, GlslShaderStage.TessellationControl, GlslShaderStage.TessellationEvaluation, GlslShaderStage.Geometry))
            {
                _r.RequireExtensions(at, ["GL_NV_stereo_view_rendering"], "ステレオの描画");
                Literal();
                return;
            }

            if (Stage is null)
            {
                return;
            }

            if (CheckStageValue(at, id, name, value, nonLiteral, Value, Literal))
            {
                return;
            }

            Invalid(GlslRuleDescriptors.UnknownLayoutIdentifier, at, $"'{name}' は{StageName}の layout で値を取る名前ではありません。");
        }

        /// <summary>ステージごとの、値を取る名前。知っている名前なら <see langword="true"/>。</summary>
        private bool CheckStageValue(ShaderSyntaxToken at, string id, string name, long? value, bool nonLiteral, Action<string> report, Action literal)
        {
            switch (Stage)
            {
                case GlslShaderStage.TessellationControl when id == "vertices":
                    if (value == 0)
                    {
                        report($"'{name}' の値は 0 より大きくなければなりません。");
                    }

                    literal();
                    return true;

                case GlslShaderStage.Geometry when id == "invocations":
                    _r.ProfileRequires(at, GlslProfileMask.Compatibility | GlslProfileMask.Core, 400, AepCoreGpuShader5, "'invocations'");

                    if (value == 0)
                    {
                        report($"'{name}' の値は 1 以上でなければなりません。");
                    }

                    literal();
                    return true;

                case GlslShaderStage.Geometry when id == "max_vertices":
                    if (value > MaxGeometryOutputVertices)
                    {
                        report($"'{name}' の値が大きすぎます (gl_MaxGeometryOutputVertices は {MaxGeometryOutputVertices})。");
                    }

                    literal();
                    return true;

                case GlslShaderStage.Geometry when id == "stream":
                    _r.RequireProfile(at, GlslProfileMask.NotEs, "出力のストリームの選択");
                    literal();
                    return true;

                case GlslShaderStage.Fragment when id == "index":
                    const string feature = "フラグメントシェーダーの出力の index";
                    _r.RequireProfile(at, GlslProfileMask.Compatibility | GlslProfileMask.Core | GlslProfileMask.Es, feature);
                    _r.ProfileRequires(at, GlslProfileMask.Compatibility | GlslProfileMask.Core, 330, ExplicitLocation, feature);
                    _r.ProfileRequires(at, GlslProfileMask.Es, 310, ["GL_EXT_blend_func_extended"], feature);

                    if (value is < 0 or > 1)
                    {
                        report($"'{name}' の値は 0 か 1 でなければなりません。");
                    }

                    literal();
                    return true;

                case GlslShaderStage.Mesh when id is "max_vertices" or "max_primitives":
                    _r.RequireExtensions(at, AepMeshShader, $"'{id}'");
                    bool ext = _r.IsOn("GL_EXT_mesh_shader", at);
                    int max = id == "max_vertices" ? MaxMeshOutputVertices : ext ? MaxMeshOutputPrimitivesExt : MaxMeshOutputPrimitivesNv;

                    if (value > max)
                    {
                        string limit = id == "max_vertices" ? "gl_MaxMeshOutputVertices" : "gl_MaxMeshOutputPrimitives";
                        report($"'{name}' の値が大きすぎます ({limit}{(ext ? "EXT" : "NV")} は {max})。");
                    }

                    literal();
                    return true;

                case GlslShaderStage.Compute or GlslShaderStage.Task or GlslShaderStage.Mesh:
                    return CheckWorkGroupValue(at, id, name, value, nonLiteral, report, literal);

                default:
                    return false;
            }
        }

        /// <summary>コンピュート・タスク・メッシュのシェーダーの、ワークグループの大きさなど。</summary>
        private bool CheckWorkGroupValue(ShaderSyntaxToken at, string id, string name, long? value, bool nonLiteral, Action<string> report, Action literal)
        {
            if (id.StartsWith("local_size_", StringComparison.Ordinal))
            {
                if (Stage is GlslShaderStage.Mesh or GlslShaderStage.Task)
                {
                    _r.RequireExtensions(at, AepMeshShader, "'gl_WorkGroupSize'");
                }
                else
                {
                    _r.ProfileRequires(at, GlslProfileMask.Es, 310, [], "'gl_WorkGroupSize'");
                    _r.ProfileRequires(at, GlslProfileMask.NotEs, 430, ["GL_ARB_compute_shader"], "'gl_WorkGroupSize'");
                }

                if (nonLiteral)
                {
                    report("'local_size' の値は整数のリテラルでなければなりません。");
                }

                if (id.Length == 12 && value == 0)
                {
                    report($"'{name}' の値は 1 以上でなければなりません。");
                    return true;
                }

                if (id is "local_size_x" or "local_size_y" or "local_size_z")
                {
                    return true;
                }

                if (_r.SpirvVersion != 0 && id is "local_size_x_id" or "local_size_y_id" or "local_size_z_id")
                {
                    return true;
                }
            }

            if (id is "shading_rate_xqcom" or "shading_rate_yqcom" or "shading_rate_zqcom")
            {
                _r.RequireExtensions(at, ["GL_QCOM_tile_shading"], "'tile shading QCOM'");

                if (nonLiteral)
                {
                    report("'shading_rate_*QCOM' の値は整数のリテラルでなければなりません。");
                }

                if (value == 0)
                {
                    report($"'{name}' の値は 1 以上でなければなりません。");
                }
                else if (id != "shading_rate_zqcom" && value is { } rate && (rate & (rate - 1)) != 0)
                {
                    report($"'{name}' の値は 2 のべき乗でなければなりません。");
                }

                return true;
            }

            _ = literal;
            return false;
        }

        /// <summary>値の式の名前を、関数の外の <c>const</c> の変数の値にする。</summary>
        private GlslConstant? Resolve(ShaderSyntaxToken name) => _constants.GetValueOrDefault(name.Text);

        /// <summary>値の式が、<c>const</c> でない変数を使っているか。</summary>
        private bool IsUndeclared(ShaderSyntaxToken token)
            => token.Kind == ShaderSyntaxKind.IdentifierToken
               && token.Text is not ("true" or "false" or "int" or "uint" or "bool" or "float" or "double")
               && !token.Text.StartsWith("gl_", StringComparison.Ordinal)
               && !_declared.Contains(token.Text);

        private bool IsCertainlyNotConstant(ImmutableArray<ShaderSyntaxToken> tokens)
            => tokens.Any(t => t.Kind == ShaderSyntaxKind.IdentifierToken && _variables.Contains(t.Text) && !_constants.ContainsKey(t.Text));
    }
}
