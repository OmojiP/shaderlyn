using System.Collections.Immutable;
using System.Globalization;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Glsl.Parsing;

namespace Shaderlyn.Glsl;

/// <summary>GLSL のプロファイル (<c>#version</c> の 2 つ目の語)。</summary>
internal enum GlslProfile
{
    /// <summary>デスクトップの GLSL のコアプロファイル。1.50 より前はプロファイルが無いが、これで表す。</summary>
    Core,

    /// <summary>デスクトップの GLSL の互換プロファイル (<c>compatibility</c>)。</summary>
    Compatibility,

    /// <summary>GLSL ES (<c>es</c>。<c>#version 100</c> も ES)。</summary>
    Es,
}

/// <summary>GLSL のシェーダーのステージ。ファイルの拡張子で決まる (glslang と同じ)。</summary>
internal enum GlslShaderStage
{
    /// <summary>頂点シェーダー (<c>.vert</c>)。</summary>
    Vertex,

    /// <summary>テッセレーションの制御シェーダー (<c>.tesc</c>)。</summary>
    TessellationControl,

    /// <summary>テッセレーションの評価シェーダー (<c>.tese</c>)。</summary>
    TessellationEvaluation,

    /// <summary>ジオメトリシェーダー (<c>.geom</c>)。</summary>
    Geometry,

    /// <summary>フラグメントシェーダー (<c>.frag</c>)。</summary>
    Fragment,

    /// <summary>コンピュートシェーダー (<c>.comp</c>)。</summary>
    Compute,

    /// <summary>タスクシェーダー (<c>.task</c>)。</summary>
    Task,

    /// <summary>メッシュシェーダー (<c>.mesh</c>)。</summary>
    Mesh,

    /// <summary>レイの生成 (<c>.rgen</c>)。</summary>
    RayGeneration,

    /// <summary>交差 (<c>.rint</c>)。</summary>
    Intersection,

    /// <summary>任意のヒット (<c>.rahit</c>)。</summary>
    AnyHit,

    /// <summary>最も近いヒット (<c>.rchit</c>)。</summary>
    ClosestHit,

    /// <summary>ミス (<c>.rmiss</c>)。</summary>
    Miss,

    /// <summary>呼び出し可能 (<c>.rcall</c>)。</summary>
    Callable,
}

/// <summary>
/// 有効にした拡張で決まる、数値の型の機能。暗黙の変換に効く (glslang 16.6.0 の <c>TNumericFeatures</c>)。
/// </summary>
[Flags]
internal enum GlslNumericFeatures
{
    /// <summary>なし。</summary>
    None = 0,

    /// <summary>GL_EXT_shader_explicit_arithmetic_types (と、その _int8 などの分かれ。GL_EXT_shader_subgroup_extended_types_* も)。</summary>
    ExplicitArithmeticTypes = 1,

    /// <summary>GL_EXT_shader_implicit_conversions (ES 3.10 以降)。</summary>
    ImplicitConversions = 2,

    /// <summary>GL_ARB_gpu_shader_fp64。</summary>
    GpuShaderFp64 = 4,

    /// <summary>GL_AMD_gpu_shader_int16。</summary>
    GpuShaderInt16 = 8,

    /// <summary>GL_AMD_gpu_shader_half_float。</summary>
    GpuShaderHalfFloat = 16,

    /// <summary>GL_NV_gpu_shader5。</summary>
    NvGpuShader5Types = 32,

    /// <summary>GL_ARB_gpu_shader5 (<c>int</c> から <c>uint</c> への変換)。</summary>
    ArbGpuShader5 = 64,

    /// <summary>すべて (バージョンの分からない断片を、いちばん多くの変換を通すものとして読む)。</summary>
    All = ExplicitArithmeticTypes | ImplicitConversions | GpuShaderFp64 | GpuShaderInt16 | GpuShaderHalfFloat | NvGpuShader5Types | ArbGpuShader5,
}

/// <summary>
/// GLSL の言語の設定 (<see cref="PreprocessorOptions.Language"/>)。木を読んだバージョン・プロファイル・ステージを持つ。
/// </summary>
/// <param name="Version">言語のバージョン (<c>#version</c> の数。<c>450</c> など)。分からなければ <see langword="null"/>。</param>
/// <param name="Profile">プロファイル。</param>
/// <param name="Stage">ステージ。取り込まれる断片 (<c>.glsl</c>) など、分からなければ <see langword="null"/>。</param>
/// <remarks>
/// 型システムと組み込みの表 (GL2a / GL2b) は、使える型と関数をここから決める。
/// </remarks>
internal sealed record GlslLanguageOptions(int? Version, GlslProfile Profile, GlslShaderStage? Stage) : ShaderLanguageOptions
{
    /// <summary>既定の設定。バージョンもステージも分からない。</summary>
    public static GlslLanguageOptions Default { get; } = new(null, GlslProfile.Core, null);

    /// <summary>ファイルの <c>#extension</c> で有効にした拡張の名前 (<see cref="ReadExtensions"/>)。</summary>
    public GlslExtensionSet Extensions { get; init; } = GlslExtensionSet.Empty;

    /// <summary>
    /// Vulkan 向けか (glslang の <c>-V</c>)。OpenGL 向けなら <see langword="false"/>、分からなければ <see langword="null"/>。
    /// </summary>
    /// <remarks>分からなければ、どちらにしかない書き方も誤りにしない。</remarks>
    public bool? Vulkan { get; init; }

    /// <summary>
    /// 作る SPIR-V のバージョン。1.3 なら 103。SPIR-V を作らなければ 0、分からなければ <see langword="null"/>。
    /// </summary>
    /// <remarks>分からなければ、SPIR-V のバージョンで決まる検査 (glslang の「requires SPIR-V 1.3」) をしない。</remarks>
    public int? SpirvVersion { get; init; }

    /// <summary>OpenGL 向けの SPIR-V を作るか (glslang の <c>-G</c>)。</summary>
    /// <remarks>組み込みが、SPIR-V を作らない OpenGL と違う (<c>gl_DepthRange</c> が無いなど)。</remarks>
    public bool IsOpenGlSpirv => Vulkan == false && SpirvVersion > 0;

    /// <summary>入力と出力の location をコンパイラが決めるか (glslang の <c>--auto-map-locations</c>)。</summary>
    /// <remarks>決めるなら、SPIR-V を作るときに location を書かなくてよい。</remarks>
    public bool AutoMapLocations { get; init; }

    /// <summary>uniform と buffer の binding をコンパイラが決めるか (glslang の <c>--auto-map-bindings</c>)。</summary>
    /// <remarks>決めるなら、SPIR-V を作るときに binding を書かなくてよい。</remarks>
    public bool AutoMapBindings { get; init; }

    /// <summary><c>#extension</c> で有効にした拡張で決まる、数値の型の機能。</summary>
    public GlslNumericFeatures NumericFeatures => Extensions.NumericFeatures;

    /// <summary>
    /// 展開の前のトークンから、<c>#extension</c> で有効にした拡張を読む。
    /// </summary>
    /// <param name="tokens">ファイルを字句解析しただけのトークン。</param>
    /// <returns>有効にした拡張。</returns>
    /// <remarks>
    /// <c>disable</c> 以外 (<c>enable</c> / <c>require</c> / <c>warn</c>) を有効とみなす (glslang の Versions.cpp の updateExtensionBehavior)。
    /// 指令の位置は見ず、ファイルのどこかで有効にしていれば有効とする。<c>#if</c> の中のものも数える。
    /// </remarks>
    public static GlslExtensionSet ReadExtensions(ImmutableArray<ShaderSyntaxToken> tokens)
    {
        ImmutableSortedSet<string>.Builder names = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);

        for (int i = 0; i + 4 < tokens.Length; i++)
        {
            if (tokens[i].Kind == ShaderSyntaxKind.HashToken
                && tokens[i].IsAtLineStart
                && tokens[i + 1].Text == "extension"
                && tokens[i + 3].Kind == ShaderSyntaxKind.ColonToken
                && tokens[i + 4].Text != "disable")
            {
                names.Add(tokens[i + 2].Text);
            }
        }

        return names.Count == 0 ? GlslExtensionSet.Empty : new GlslExtensionSet(names.ToImmutable());
    }

    /// <summary>
    /// <c>#version</c> が無いファイルのバージョン。glslang は ES の 100 として読む (glslang 16.6.0 で確かめた)。
    /// </summary>
    /// <remarks>GLSL 4.60 の仕様書は 1.10 とするが、実際にコンパイルする glslang に合わせる。</remarks>
    public const int VersionWithoutDirective = 100;

    /// <inheritdoc/>
    public override string DescribeUnavailable() => "GLSL";

    /// <inheritdoc/>
    public override bool IsCompleteUnits(ImmutableArray<ShaderSyntaxToken> tokens) => GlslParser.IsCompleteUnits(tokens);

    /// <summary>ES のときに glslang が定義する拡張のマクロ (Versions.cpp の getPreamble)。</summary>
    private static readonly string[] EsExtensionMacros =
    [
        "GL_OES_texture_3D", "GL_OES_standard_derivatives", "GL_EXT_frag_depth", "GL_OES_EGL_image_external",
        "GL_OES_EGL_image_external_essl3", "GL_EXT_YUV_target", "GL_EXT_shader_texture_lod", "GL_EXT_shadow_samplers",
        "GL_EXT_fragment_shading_rate", "GL_EXT_conservative_depth", "GL_ANDROID_extension_pack_es31a", "GL_OES_sample_variables",
        "GL_OES_shader_image_atomic", "GL_OES_shader_multisample_interpolation", "GL_OES_texture_storage_multisample_2d_array",
        "GL_EXT_geometry_shader", "GL_EXT_geometry_point_size", "GL_EXT_gpu_shader5", "GL_EXT_primitive_bounding_box",
        "GL_EXT_shader_io_blocks", "GL_EXT_tessellation_shader", "GL_EXT_tessellation_point_size", "GL_EXT_texture_buffer",
        "GL_EXT_texture_cube_map_array", "GL_EXT_shader_implicit_conversions", "GL_EXT_shader_integer_mix", "GL_EXT_blend_func_extended",
        "GL_EXT_descriptor_heap", "GL_EXT_structured_descriptor_heap", "GL_EXT_split_barrier", "GL_OES_geometry_shader",
        "GL_OES_geometry_point_size", "GL_OES_gpu_shader5", "GL_OES_primitive_bounding_box", "GL_OES_shader_io_blocks",
        "GL_OES_tessellation_shader", "GL_OES_tessellation_point_size", "GL_OES_texture_buffer", "GL_OES_texture_cube_map_array",
        "GL_EXT_shader_non_constant_global_initializers", "GL_QCOM_image_processing", "GL_QCOM_image_processing2",
        "GL_QCOM_image_processing3", "GL_QCOM_tile_shading", "GL_QCOM_cooperative_matrix_conversion", "GL_QCOM_multiple_wait_queues",
    ];

    /// <summary>デスクトップのときに glslang が定義する拡張のマクロ (Versions.cpp の getPreamble)。</summary>
    private static readonly string[] DesktopExtensionMacros =
    [
        "GL_ARB_texture_rectangle", "GL_ARB_shading_language_420pack", "GL_ARB_texture_gather", "GL_ARB_gpu_shader5",
        "GL_ARB_separate_shader_objects", "GL_ARB_compute_shader", "GL_ARB_tessellation_shader", "GL_ARB_enhanced_layouts",
        "GL_ARB_texture_cube_map_array", "GL_ARB_texture_multisample", "GL_ARB_shader_texture_lod", "GL_ARB_explicit_attrib_location",
        "GL_ARB_explicit_uniform_location", "GL_ARB_shader_image_load_store", "GL_ARB_shader_atomic_counters",
        "GL_ARB_shader_draw_parameters", "GL_ARB_shader_group_vote", "GL_ARB_derivative_control", "GL_ARB_shader_texture_image_samples",
        "GL_ARB_viewport_array", "GL_ARB_gpu_shader_int64", "GL_ARB_gpu_shader_fp64", "GL_ARB_shader_ballot", "GL_ARB_sparse_texture2",
        "GL_ARB_sparse_texture_clamp", "GL_ARB_shader_stencil_export", "GL_ARB_sample_shading", "GL_ARB_shader_image_size",
        "GL_ARB_shading_language_packing", "GL_ARB_cull_distance", "GL_ARB_post_depth_coverage", "GL_ARB_fragment_shader_interlock",
        "GL_ARB_uniform_buffer_object", "GL_ARB_shader_bit_encoding", "GL_ARB_shader_storage_buffer_object", "GL_ARB_texture_query_lod",
        "GL_ARB_vertex_attrib_64bit", "GL_NV_gpu_shader5", "GL_ARB_draw_instanced", "GL_ARB_fragment_coord_conventions",
        "GL_ARB_conservative_depth", "GL_EXT_shader_non_constant_global_initializers", "GL_EXT_shader_image_load_formatted",
        "GL_EXT_post_depth_coverage", "GL_EXT_control_flow_attributes", "GL_EXT_nonuniform_qualifier", "GL_EXT_shader_16bit_storage",
        "GL_EXT_shader_8bit_storage", "GL_EXT_samplerless_texture_functions", "GL_EXT_scalar_block_layout",
        "GL_EXT_fragment_invocation_density", "GL_EXT_buffer_reference", "GL_EXT_buffer_reference2", "GL_EXT_buffer_reference_uvec2",
        "GL_EXT_demote_to_helper_invocation", "GL_EXT_debug_printf", "GL_EXT_fragment_shading_rate", "GL_EXT_shared_memory_block",
        "GL_EXT_shader_integer_mix", "GL_EXT_spec_constant_composites", "GL_EXT_abort", "GL_EXT_split_barrier",
        "GL_EXT_cooperative_matrix_maintenance1", "GL_KHR_shader_subgroup_basic", "GL_KHR_shader_subgroup_vote",
        "GL_KHR_shader_subgroup_arithmetic", "GL_KHR_shader_subgroup_ballot", "GL_KHR_shader_subgroup_shuffle",
        "GL_KHR_shader_subgroup_shuffle_relative", "GL_KHR_shader_subgroup_clustered", "GL_KHR_shader_subgroup_quad",
        "GL_KHR_cooperative_matrix", "GL_EXT_shader_image_int64", "GL_EXT_shader_atomic_int64", "GL_EXT_shader_realtime_clock",
        "GL_EXT_ray_tracing", "GL_EXT_ray_query", "GL_EXT_ray_flags_primitive_culling", "GL_EXT_ray_cull_mask",
        "GL_EXT_ray_tracing_position_fetch", "GL_EXT_spirv_intrinsics", "GL_EXT_mesh_shader", "GL_AMD_shader_ballot",
        "GL_AMD_shader_trinary_minmax", "GL_AMD_shader_explicit_vertex_parameter", "GL_AMD_gcn_shader", "GL_AMD_gpu_shader_half_float",
        "GL_AMD_texture_gather_bias_lod", "GL_AMD_gpu_shader_int16", "GL_AMD_shader_image_load_store_lod", "GL_AMD_shader_fragment_mask",
        "GL_AMD_gpu_shader_half_float_fetch", "GL_INTEL_shader_integer_functions2", "GL_NV_sample_mask_override_coverage",
        "GL_NV_geometry_shader_passthrough", "GL_NV_viewport_array2", "GL_NV_shader_atomic_int64",
        "GL_NV_conservative_raster_underestimation", "GL_NV_shader_subgroup_partitioned", "GL_NV_shading_rate_image", "GL_NV_ray_tracing",
        "GL_NV_ray_tracing_motion_blur", "GL_NV_fragment_shader_barycentric", "GL_KHR_compute_shader_derivatives",
        "GL_NV_compute_shader_derivatives", "GL_NV_shader_texture_footprint", "GL_NV_mesh_shader", "GL_NV_cooperative_matrix",
        "GL_NV_integer_cooperative_matrix", "GL_NV_shader_invocation_reorder", "GL_NV_cooperative_matrix2",
        "GL_NV_cooperative_matrix_decode_vector", "GL_NV_desktop_lowp_mediump", "GL_NV_explicit_typecast", "GL_QCOM_image_processing",
        "GL_QCOM_image_processing2", "GL_QCOM_image_processing3", "GL_QCOM_tile_shading", "GL_QCOM_cooperative_matrix_conversion",
        "GL_QCOM_multiple_wait_queues", "GL_EXT_shader_explicit_arithmetic_types", "GL_EXT_shader_explicit_arithmetic_types_int8",
        "GL_EXT_shader_explicit_arithmetic_types_int16", "GL_EXT_shader_explicit_arithmetic_types_int32",
        "GL_EXT_shader_explicit_arithmetic_types_int64", "GL_EXT_shader_explicit_arithmetic_types_float16",
        "GL_EXT_shader_explicit_arithmetic_types_float32", "GL_EXT_shader_explicit_arithmetic_types_float64",
        "GL_EXT_shader_subgroup_extended_types_int8", "GL_EXT_shader_subgroup_extended_types_int16",
        "GL_EXT_shader_subgroup_extended_types_int64", "GL_EXT_shader_subgroup_extended_types_float16", "GL_EXT_shader_atomic_float",
        "GL_EXT_shader_atomic_float2", "GL_EXT_fragment_shader_barycentric", "GL_EXT_shader_quad_control", "GL_EXT_texture_array",
        "GL_EXT_control_flow_attributes2", "GL_EXT_function_control_attributes", "GL_EXT_integer_dot_product", "GL_EXT_bfloat16",
        "GL_EXT_float_e5m2", "GL_EXT_float_e4m3", "GL_EXT_uniform_buffer_unsized_array", "GL_EXT_shader_64bit_indexing",
        "GL_EXT_float_e2m1", "GL_EXT_float_e3m2", "GL_EXT_float_e2m3", "GL_EXT_float_ue8m0", "GL_EXT_float_mxint8",
        "GL_EXT_shader_invocation_reorder", "GL_EXT_descriptor_heap", "GL_EXT_structured_descriptor_heap",
    ];

    /// <summary>
    /// コンパイラが定義するマクロを作る。glslang 16.6.0 の Versions.cpp の <c>getPreamble</c> を移したもの。
    /// </summary>
    /// <returns>マクロ。バージョンが分からなければ空。</returns>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description><c>__FILE__</c> は文字列の番号の 0 (C と HLSL と違い、ファイル名ではない)</description></item>
    ///   <item><description><c>__VERSION__</c> はバージョン。ES は <c>GL_ES</c>、デスクトップの 1.50 以降は <c>GL_core_profile</c> (互換プロファイルなら <c>GL_compatibility_profile</c> も)</description></item>
    ///   <item><description>glslang が読める拡張の名前のマクロ (<c>GL_EXT_ray_tracing</c> など)。拡張を有効にしていなくても定義する</description></item>
    ///   <item><description>デスクトップでは、ステージのマクロ (<c>GL_FRAGMENT_SHADER</c> など)</description></item>
    ///   <item><description>Vulkan 向けと分かっていれば <c>VULKAN</c> を 100 にする (glslang の <c>-V</c>)。OpenGL 向けの SPIR-V なら <c>GL_SPIRV</c> を 100 にする (<c>-G</c>)</description></item>
    /// </list>
    /// </remarks>
    public ImmutableDictionary<string, string> CreateCompilerMacros()
    {
        ImmutableDictionary<string, string>.Builder macros = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        // GLSL の __FILE__ はファイル名の文字列でなく、文字列の番号 (整数) である。1 つのファイルを読むので 0 になる。
        macros["__FILE__"] = "0";

        if (Version is not { } version)
        {
            return macros.ToImmutable();
        }

        bool es = Profile == GlslProfile.Es;
        macros["__VERSION__"] = version.ToString(CultureInfo.InvariantCulture);

        void Define(params ReadOnlySpan<string> names)
        {
            foreach (string name in names)
            {
                macros[name] = "1";
            }
        }

        if (es)
        {
            Define("GL_ES", "GL_FRAGMENT_PRECISION_HIGH");
            Define(EsExtensionMacros);

            if (version >= 300)
            {
                Define("GL_NV_shader_noperspective_interpolation");
            }

            if (version >= 310)
            {
                Define("GL_EXT_null_initializer", "GL_EXT_subgroup_uniform_control_flow", "GL_EXT_maximal_reconvergence");
            }
        }
        else
        {
            Define(DesktopExtensionMacros);

            // SPIR-V を作らないときだけ (Vulkan 向けでないとき)。
            if (Vulkan != true)
            {
                Define("GL_ARB_bindless_texture");
            }

            if (version >= 150)
            {
                Define("GL_core_profile");

                if (Profile == GlslProfile.Compatibility)
                {
                    Define("GL_compatibility_profile");
                }
            }

            if (version >= 140)
            {
                Define("GL_EXT_null_initializer", "GL_EXT_subgroup_uniform_control_flow", "GL_EXT_maximal_reconvergence");
            }

            if (version >= 130)
            {
                Define("GL_FRAGMENT_PRECISION_HIGH");
            }

            if (version >= 460)
            {
                Define("GL_ARM_tensors", "GL_ARM_tensors_bfloat16", "GL_ARM_tensors_float_e5m2", "GL_ARM_tensors_float_e4m3");
            }
        }

        if ((!es && version >= 460) || (es && version >= 320))
        {
            Define("GL_EXT_nontemporal_keyword");
        }

        if ((!es && version >= 140) || (es && version >= 310))
        {
            Define("GL_EXT_device_group", "GL_EXT_multiview", "GL_NV_shader_sm_builtins");
        }

        if ((!es && version >= 130) || (es && version >= 300))
        {
            Define("GL_EXT_texture_offset_non_const");
        }

        if (version >= 300)
        {
            Define("GL_OVR_multiview", "GL_OVR_multiview2");
        }

        if ((!es && version >= 140) || (es && version >= 300))
        {
            Define("GL_EXT_optional_input_attachment_index");
        }

        Define("GL_GOOGLE_cpp_style_line_directive", "GL_GOOGLE_include_directive", "GL_KHR_blend_equation_advanced", "GL_EXT_terminate_invocation");

        if (Vulkan == true)
        {
            macros["VULKAN"] = "100";
        }

        // OpenGL 向けの SPIR-V (glslang の -G)。
        if (IsOpenGlSpirv)
        {
            macros["GL_SPIRV"] = "100";
        }

        if (!es && Stage is { } stage)
        {
            Define(stage switch
            {
                GlslShaderStage.Vertex => "GL_VERTEX_SHADER",
                GlslShaderStage.TessellationControl => "GL_TESSELLATION_CONTROL_SHADER",
                GlslShaderStage.TessellationEvaluation => "GL_TESSELLATION_EVALUATION_SHADER",
                GlslShaderStage.Geometry => "GL_GEOMETRY_SHADER",
                GlslShaderStage.Fragment => "GL_FRAGMENT_SHADER",
                GlslShaderStage.Compute => "GL_COMPUTE_SHADER",
                GlslShaderStage.RayGeneration => "GL_RAY_GENERATION_SHADER_EXT",
                GlslShaderStage.Intersection => "GL_INTERSECTION_SHADER_EXT",
                GlslShaderStage.AnyHit => "GL_ANY_HIT_SHADER_EXT",
                GlslShaderStage.ClosestHit => "GL_CLOSEST_HIT_SHADER_EXT",
                GlslShaderStage.Miss => "GL_MISS_SHADER_EXT",
                GlslShaderStage.Callable => "GL_CALLABLE_SHADER_EXT",
                GlslShaderStage.Task => "GL_TASK_SHADER_EXT",
                _ => "GL_MESH_SHADER_EXT",
            });
        }

        return macros.ToImmutable();
    }

    /// <summary>
    /// 展開の前のトークンから、<c>#version</c> の指定を読む。
    /// </summary>
    /// <param name="tokens">ファイルを字句解析しただけのトークン。</param>
    /// <param name="version">バージョン。</param>
    /// <param name="profile">プロファイル。書いていなければ、100 と 300 は ES、ほかはコア。</param>
    /// <returns><c>#version</c> を読めれば <see langword="true"/>。</returns>
    /// <remarks>最初の <c>#version</c> だけを読む。2 つ目以降はコンパイラが誤りにする。</remarks>
    public static bool TryReadVersion(ImmutableArray<ShaderSyntaxToken> tokens, out int version, out GlslProfile profile)
    {
        for (int i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i].Kind != ShaderSyntaxKind.HashToken
                || !tokens[i].IsAtLineStart
                || tokens[i + 1].Text != "version"
                || tokens[i + 2].IsAtLineStart)
            {
                continue;
            }

            if (!int.TryParse(tokens[i + 2].Text, NumberStyles.None, CultureInfo.InvariantCulture, out version))
            {
                break;
            }

            string? written = i + 3 < tokens.Length && !tokens[i + 3].IsAtLineStart ? tokens[i + 3].Text : null;
            profile = written switch
            {
                "es" => GlslProfile.Es,
                "compatibility" => GlslProfile.Compatibility,
                "core" => GlslProfile.Core,
                _ => version is 100 or 300 ? GlslProfile.Es : GlslProfile.Core,
            };

            return true;
        }

        version = 0;
        profile = GlslProfile.Core;
        return false;
    }

    /// <summary>ファイルの名前から、ステージを決める。</summary>
    /// <param name="filePath">ファイルのパス。</param>
    /// <returns>ステージ。<c>.glsl</c> などステージを表さない拡張子なら <see langword="null"/>。</returns>
    /// <remarks>
    /// glslang と同じく、<c>.glsl</c> の前にステージの拡張子があれば (<c>shadow.vert.glsl</c>) そのステージにする。
    /// </remarks>
    public static GlslShaderStage? StageFromFileName(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        string extension = Path.GetExtension(filePath);

        if (extension.Equals(".glsl", StringComparison.OrdinalIgnoreCase))
        {
            extension = Path.GetExtension(Path.GetFileNameWithoutExtension(filePath));
        }

        return extension.ToLowerInvariant() switch
        {
            ".vert" => GlslShaderStage.Vertex,
            ".tesc" => GlslShaderStage.TessellationControl,
            ".tese" => GlslShaderStage.TessellationEvaluation,
            ".geom" => GlslShaderStage.Geometry,
            ".frag" => GlslShaderStage.Fragment,
            ".comp" => GlslShaderStage.Compute,
            ".task" => GlslShaderStage.Task,
            ".mesh" => GlslShaderStage.Mesh,
            ".rgen" => GlslShaderStage.RayGeneration,
            ".rint" => GlslShaderStage.Intersection,
            ".rahit" => GlslShaderStage.AnyHit,
            ".rchit" => GlslShaderStage.ClosestHit,
            ".rmiss" => GlslShaderStage.Miss,
            ".rcall" => GlslShaderStage.Callable,
            _ => null,
        };
    }
}

/// <summary>有効にした拡張の名前の集まり。中身で比べる (言語の設定の等しさに使う)。</summary>
/// <param name="names">拡張の名前。</param>
internal sealed class GlslExtensionSet(ImmutableSortedSet<string> names) : IEquatable<GlslExtensionSet>
{
    /// <summary>空の集まり。</summary>
    public static GlslExtensionSet Empty { get; } = new([]);

    /// <summary>拡張の名前。</summary>
    public ImmutableSortedSet<string> Names { get; } = names;

    /// <summary>拡張で決まる、数値の型の機能。</summary>
    public GlslNumericFeatures NumericFeatures { get; } = ComputeNumericFeatures(names);

    /// <summary>拡張を有効にしているか。</summary>
    /// <param name="name">拡張の名前。</param>
    /// <returns>有効なら <see langword="true"/>。</returns>
    public bool Contains(string name) => Names.Contains(name);

    /// <inheritdoc/>
    public bool Equals(GlslExtensionSet? other) => other is not null && Names.SetEquals(other.Names);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as GlslExtensionSet);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;

        foreach (string name in Names)
        {
            hash.Add(name, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private static GlslNumericFeatures ComputeNumericFeatures(ImmutableSortedSet<string> names)
    {
        GlslNumericFeatures features = GlslNumericFeatures.None;

        foreach (string name in names)
        {
            features |= name switch
            {
                _ when name.StartsWith("GL_EXT_shader_explicit_arithmetic_types", StringComparison.Ordinal)
                       || name.StartsWith("GL_EXT_shader_subgroup_extended_types_", StringComparison.Ordinal)
                    => GlslNumericFeatures.ExplicitArithmeticTypes,
                "GL_EXT_shader_implicit_conversions" => GlslNumericFeatures.ImplicitConversions,
                "GL_ARB_gpu_shader_fp64" => GlslNumericFeatures.GpuShaderFp64,
                "GL_AMD_gpu_shader_int16" => GlslNumericFeatures.GpuShaderInt16,
                "GL_AMD_gpu_shader_half_float" => GlslNumericFeatures.GpuShaderHalfFloat,
                "GL_NV_gpu_shader5" => GlslNumericFeatures.NvGpuShader5Types,
                "GL_ARB_gpu_shader5" => GlslNumericFeatures.ArbGpuShader5,
                _ => GlslNumericFeatures.None,
            };
        }

        return features;
    }
}
