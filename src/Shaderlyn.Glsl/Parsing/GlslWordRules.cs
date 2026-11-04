using System.Collections.Frozen;

namespace Shaderlyn.Glsl.Parsing;

/// <summary>GLSL の語を、言語のバージョンと有効にした拡張で分けた種類。</summary>
internal enum GlslWordKind
{
    /// <summary>キーワード (型・修飾子・文の語)。</summary>
    Keyword,

    /// <summary>名前 (そのバージョンと拡張ではキーワードでない)。</summary>
    Identifier,

    /// <summary>予約語。名前にも使えない (glslang の「Reserved word.」)。</summary>
    Reserved,
}

/// <summary>
/// GLSL の語が、キーワードか名前か予約語かを決める規則。glslang 16.6.0 の Scan.cpp の <c>TScanContext::tokenizeIdentifier</c> を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// 同じ語でも、言語のバージョン・ES か・有効にした拡張・Vulkan か OpenGL か で扱いが変わる。
/// たとえば <c>sample</c> はデスクトップの 4.00 より前では名前で (LearnOpenGL の <c>vec3 sample[9];</c>)、4.00 以降はキーワードである。
/// <c>double</c> を 1.50 より前に書くと予約語で、誤りになる。
/// </para>
/// <para>
/// glslang が組み込みの宣言を読むとき (<c>atBuiltInLevel</c>) の分かれ道は、利用者のコードには関わらないので落とした。
/// 前方互換 (<c>isForwardCompatible</c>) の警告も出さない。
/// キーワードであっても、予約語として誤りにするものがある (<see cref="GlslWord.IsReservedError"/>)。
/// </para>
/// </remarks>
internal sealed class GlslWordRules
{
    /// <summary>どのバージョンでも予約語である語 (glslang の ReservedSet)。</summary>
    public static FrozenSet<string> ReservedWords { get; } = new[]
    {
        "common", "partition", "active", "asm", "class", "union", "enum", "typedef", "template", "this",
        "goto", "public", "static", "extern", "external", "interface", "long", "short", "half", "fixed",
        "unsigned", "input", "output", "hvec2", "hvec3", "hvec4", "fvec2", "fvec3", "fvec4", "sampler3DRect",
        "filter", "sizeof", "cast", "namespace", "using",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] AepGpuShader5 = ["GL_EXT_gpu_shader5", "GL_OES_gpu_shader5"];
    private static readonly string[] AepCoreGpuShader5 = ["GL_ARB_gpu_shader5", "GL_NV_gpu_shader5"];
    private static readonly string[] AepTessellationShader = ["GL_EXT_tessellation_shader", "GL_OES_tessellation_shader"];
    private static readonly string[] AepTextureBuffer = ["GL_EXT_texture_buffer", "GL_OES_texture_buffer"];
    private static readonly string[] AepTextureCubeMapArray = ["GL_EXT_texture_cube_map_array", "GL_OES_texture_cube_map_array"];

    private readonly Func<string, bool> _isExtensionOn;
    private bool _reserve;

    /// <summary>規則を作る。</summary>
    /// <param name="version">言語のバージョン。</param>
    /// <param name="es">ES か。</param>
    /// <param name="stage">ステージ。分からなければ <see langword="null"/>。</param>
    /// <param name="vulkan">Vulkan 向けか (glslang の <c>-V</c>)。分からなければ <see langword="null"/> で、Vulkan にだけある型もキーワードとする。</param>
    /// <param name="isExtensionOn">拡張を有効にしているか。</param>
    public GlslWordRules(int version, bool es, GlslShaderStage? stage, bool? vulkan, Func<string, bool> isExtensionOn)
    {
        Version = version;
        Es = es;
        Stage = stage;
        VulkanTarget = vulkan;
        _isExtensionOn = isExtensionOn;
    }

    private int Version { get; }

    private bool Es { get; }

    private GlslShaderStage? Stage { get; }

    private bool? VulkanTarget { get; }

    /// <summary>Vulkan 向けか。分からなければ Vulkan とみなす (Vulkan にだけある型を名前と取り違えない)。</summary>
    private bool Vulkan => VulkanTarget ?? true;

    /// <summary>語を分ける。</summary>
    /// <param name="word">語。</param>
    /// <returns>種類。GLSL のキーワードの表 (<see cref="GlslKeywords"/>) に無い語は名前。</returns>
    public GlslWord Classify(string word)
    {
        if (ReservedWords.Contains(word))
        {
            return new GlslWord(GlslWordKind.Reserved, IsReservedError: true);
        }

        _reserve = false;
        GlslWordKind kind = ClassifyCore(word);
        return new GlslWord(kind, kind == GlslWordKind.Reserved || _reserve);
    }

    private const GlslWordKind Keyword = GlslWordKind.Keyword;
    private const GlslWordKind Identifier = GlslWordKind.Identifier;
    private const GlslWordKind Reserved = GlslWordKind.Reserved;

    private bool On(string extension) => _isExtensionOn(extension);

    private bool AnyOn(string[] extensions) => extensions.Any(_isExtensionOn);

    /// <summary>予約語として誤りにする (キーワードとしては読む)。</summary>
    private void Reserve() => _reserve = true;

    private GlslWordKind ClassifyCore(string word)
    {
        switch (word)
        {
            case "const":
            case "uniform":
            case "tileImageEXT":
            case "in":
            case "out":
            case "inout":
            case "break":
            case "continue":
            case "do":
            case "for":
            case "while":
            case "if":
            case "else":
            case "discard":
            case "return":
            case "case":
                return Keyword;

            case "terminateInvocation":
                if (!On("GL_EXT_terminate_invocation"))
                {
                    return Identifier;
                }

                return Keyword;

            case "terminateRayEXT":
            case "ignoreIntersectionEXT":
                if (!On("GL_EXT_ray_tracing"))
                {
                    return Identifier;
                }

                return Keyword;

            case "buffer":
                if ((Es && Version < 310) || (!Es && (Version < 430 && !On("GL_ARB_shader_storage_buffer_object"))))
                {
                    return Identifier;
                }

                return Keyword;

            case "struct":
                return Keyword;

            case "switch":
            case "default":
                if ((Es && Version < 300) || (!Es && Version < 130))
                {
                    Reserve();
                }

                return Keyword;

            case "void":
            case "bool":
            case "float":
            case "int":
            case "bvec2":
            case "bvec3":
            case "bvec4":
            case "vec2":
            case "vec3":
            case "vec4":
            case "ivec2":
            case "ivec3":
            case "ivec4":
            case "mat2":
            case "mat3":
            case "mat4":
            case "sampler2D":
            case "samplerCube":
                return Keyword;

            case "true":
            case "false":
                return Keyword;

            case "smooth":
                if ((Es && Version < 300) || (!Es && Version < 130))
                {
                    return Identifier;
                }

                return Keyword;

            case "flat":
                if (Es && Version < 300)
                {
                    Reserve();
                }
                else if (!Es && Version < 130)
                {
                    return Identifier;
                }

                return Keyword;

            case "centroid":
                if (Version < 120)
                {
                    return Identifier;
                }

                return Keyword;

            case "invariant":
                if (!Es && Version < 120)
                {
                    return Identifier;
                }

                return Keyword;

            case "packed":
                if ((Es && Version < 300) || (!Es && Version < 140))
                {
                    return Reserved;
                }

                return Identifier;

            case "resource":
                {
                    bool reserved = (Es && Version >= 300) || (!Es && Version >= 420);
                    return IdentifierOrReserved(reserved);
                }

            case "superp":
                {
                    bool reserved = Es || Version >= 130;
                    return IdentifierOrReserved(reserved);
                }

            case "noperspective":
                if (On("GL_NV_shader_noperspective_interpolation"))
                {
                    return Keyword;
                }

                return Es30ReservedFromGlsl(130);

            case "nonuniformEXT":
                if (On("GL_EXT_nonuniform_qualifier"))
                {
                    return Keyword;
                }
                else
                {
                    return Identifier;
                }

            case "attribute":
            case "varying":
                if (Es && Version >= 300)
                {
                    Reserve();
                }

                return Keyword;

            case "rayPayloadNV":
            case "rayPayloadInNV":
            case "hitAttributeNV":
            case "callableDataNV":
            case "callableDataInNV":
            case "accelerationStructureNV":
                if (On("GL_NV_ray_tracing"))
                {
                    return Keyword;
                }

                return Identifier;

            case "accelerationStructureEXT":
                if (On("GL_EXT_ray_tracing") || On("GL_EXT_ray_query") || On("GL_NV_displacement_micromap"))
                {
                    return Keyword;
                }

                return Identifier;

            case "rayPayloadEXT":
            case "rayPayloadInEXT":
            case "hitAttributeEXT":
            case "callableDataEXT":
            case "callableDataInEXT":
                if (On("GL_EXT_ray_tracing") || On("GL_EXT_ray_query"))
                {
                    return Keyword;
                }

                return Identifier;

            case "rayQueryEXT":
                if ((!Es && Version >= 460 && On("GL_EXT_ray_query")))
                {
                    return Keyword;
                }

                return Identifier;

            case "atomic_uint":
                if ((Es && Version >= 310) || On("GL_ARB_shader_atomic_counters"))
                {
                    return Keyword;
                }

                return Es30ReservedFromGlsl(420);

            case "coherent":
            case "devicecoherent":
            case "queuefamilycoherent":
            case "workgroupcoherent":
            case "subgroupcoherent":
            case "shadercallcoherent":
            case "nonprivate":
            case "restrict":
            case "readonly":
            case "writeonly":
                if (Es && Version >= 310)
                {
                    return Keyword;
                }

                return Es30ReservedFromGlsl(On("GL_ARB_shader_image_load_store") ? 130 : 420);

            case "volatile":
                if (Es && Version >= 310)
                {
                    return Keyword;
                }

                if ((Es || (Version < 420 && ! On("GL_ARB_shader_image_load_store"))))
                {
                    Reserve();
                }

                return Keyword;

            case "nontemporal":
                if (On("GL_EXT_nontemporal_keyword"))
                {
                    return Keyword;
                }

                return Identifier;

            case "patch":
                if ((Es && (Version >= 320 || AnyOn(AepTessellationShader))) || (!Es && On("GL_ARB_tessellation_shader")))
                {
                    return Keyword;
                }

                return Es30ReservedFromGlsl(400);

            case "sample":
                {
                    string[] layoutExts = ["GL_OES_shader_multisample_interpolation", "GL_ARB_gpu_shader5", "GL_NV_gpu_shader5"];
                    if ((Es && Version >= 320) || AnyOn(layoutExts))
                    {
                        return Keyword;
                    }

                    return Es30ReservedFromGlsl(400);
                }

            case "subroutine":
                return Es30ReservedFromGlsl(400);

            case "shared":
                if ((Es && Version < 300) || (!Es && Version < 140))
                {
                    return Identifier;
                }

                return Keyword;

            case "layout":
                {
                    string[] layoutExts = ["GL_ARB_shading_language_420pack", "GL_ARB_explicit_attrib_location"];
                    if ((Es && Version < 300) || (!Es && Version < 140 && ! AnyOn(layoutExts)))
                    {
                        return Identifier;
                    }

                    return Keyword;
                }

            case "highp":
            case "mediump":
            case "lowp":
            case "precision":
                return PrecisionKeyword();

            case "mat2x2":
            case "mat2x3":
            case "mat2x4":
            case "mat3x2":
            case "mat3x3":
            case "mat3x4":
            case "mat4x2":
            case "mat4x3":
            case "mat4x4":
                return MatNxM();

            case "dmat2":
            case "dmat3":
            case "dmat4":
            case "dmat2x2":
            case "dmat2x3":
            case "dmat2x4":
            case "dmat3x2":
            case "dmat3x3":
            case "dmat3x4":
            case "dmat4x2":
            case "dmat4x3":
            case "dmat4x4":
                return DMat();

            case "image1D":
            case "iimage1D":
            case "uimage1D":
            case "image1DArray":
            case "iimage1DArray":
            case "uimage1DArray":
            case "image2DRect":
            case "iimage2DRect":
            case "uimage2DRect":
                return FirstGenerationImage(false);

            case "i64image1D":
            case "u64image1D":
            case "i64image1DArray":
            case "u64image1DArray":
            case "i64image2DRect":
            case "u64image2DRect":
                if (On("GL_EXT_shader_image_int64"))
                {
                    return FirstGenerationImage(false);
                }
                return Identifier;

            case "imageBuffer":
            case "iimageBuffer":
            case "uimageBuffer":
                if ((Es && Version >= 320) || AnyOn(AepTextureBuffer))
                {
                    return Keyword;
                }

                return FirstGenerationImage(false);

            case "i64imageBuffer":
            case "u64imageBuffer":
                if (On("GL_EXT_shader_image_int64"))
                {
                    if ((Es && Version >= 320) || AnyOn(AepTextureBuffer))
                    {
                        return Keyword;
                    }

                    return FirstGenerationImage(false);
                }
                return Identifier;

            case "image2D":
            case "iimage2D":
            case "uimage2D":
            case "image3D":
            case "iimage3D":
            case "uimage3D":
            case "imageCube":
            case "iimageCube":
            case "uimageCube":
            case "image2DArray":
            case "iimage2DArray":
            case "uimage2DArray":
                return FirstGenerationImage(true);

            case "i64image2D":
            case "u64image2D":
            case "i64image3D":
            case "u64image3D":
            case "i64imageCube":
            case "u64imageCube":
            case "i64image2DArray":
            case "u64image2DArray":
                if (On("GL_EXT_shader_image_int64"))
                {
                    return FirstGenerationImage(true);
                }

                return Identifier;

            case "imageCubeArray":
            case "iimageCubeArray":
            case "uimageCubeArray":
                if ((Es && Version >= 320) || AnyOn(AepTextureCubeMapArray))
                {
                    return Keyword;
                }

                return SecondGenerationImage();

            case "i64imageCubeArray":
            case "u64imageCubeArray":
                if (On("GL_EXT_shader_image_int64"))
                {
                    if ((Es && Version >= 320) || AnyOn(AepTextureCubeMapArray))
                    {
                        return Keyword;
                    }

                    return SecondGenerationImage();
                }
                return Identifier;

            case "image2DMS":
            case "iimage2DMS":
            case "uimage2DMS":
            case "image2DMSArray":
            case "iimage2DMSArray":
            case "uimage2DMSArray":
                return SecondGenerationImage();

            case "i64image2DMS":
            case "u64image2DMS":
            case "i64image2DMSArray":
            case "u64image2DMSArray":
                if (On("GL_EXT_shader_image_int64"))
                {
                    return SecondGenerationImage();
                }
                return Identifier;

            case "double":
            case "dvec2":
            case "dvec3":
            case "dvec4":
                if (Es || Version < 150 || ((Version < 400 && !On("GL_ARB_gpu_shader_fp64") && (Version < 410 && !On("GL_ARB_vertex_attrib_64bit")))))
                {
                    Reserve();
                }

                return Keyword;

            case "int64_t":
            case "uint64_t":
            case "i64vec2":
            case "i64vec3":
            case "i64vec4":
            case "u64vec2":
            case "u64vec3":
            case "u64vec4":
                if (On("GL_ARB_gpu_shader_int64") || On("GL_EXT_shader_explicit_arithmetic_types") || On("GL_NV_gpu_shader5") || On("GL_EXT_shader_explicit_arithmetic_types_int64"))
                {
                    return Keyword;
                }
                return Identifier;

            case "int8_t":
            case "uint8_t":
            case "i8vec2":
            case "i8vec3":
            case "i8vec4":
            case "u8vec2":
            case "u8vec3":
            case "u8vec4":
                if (On("GL_EXT_shader_explicit_arithmetic_types") || On("GL_EXT_shader_8bit_storage") || On("GL_NV_gpu_shader5") || On("GL_EXT_shader_explicit_arithmetic_types_int8"))
                {
                    return Keyword;
                }
                return Identifier;

            case "int16_t":
            case "uint16_t":
            case "i16vec2":
            case "i16vec3":
            case "i16vec4":
            case "u16vec2":
            case "u16vec3":
            case "u16vec4":
                if (On("GL_AMD_gpu_shader_int16") || On("GL_EXT_shader_16bit_storage") || On("GL_EXT_shader_explicit_arithmetic_types") || On("GL_NV_gpu_shader5") || On("GL_EXT_shader_explicit_arithmetic_types_int16"))
                {
                    return Keyword;
                }
                return Identifier;

            case "int32_t":
            case "uint32_t":
            case "i32vec2":
            case "i32vec3":
            case "i32vec4":
            case "u32vec2":
            case "u32vec3":
            case "u32vec4":
                if (On("GL_EXT_shader_explicit_arithmetic_types") || On("GL_NV_gpu_shader5") || On("GL_EXT_shader_explicit_arithmetic_types_int32"))
                {
                    return Keyword;
                }
                return Identifier;

            case "float32_t":
            case "f32vec2":
            case "f32vec3":
            case "f32vec4":
                if (On("GL_EXT_shader_explicit_arithmetic_types") || On("GL_NV_gpu_shader5") || On("GL_EXT_shader_explicit_arithmetic_types_float32"))
                {
                    return Keyword;
                }
                return Identifier;

            case "f32mat2":
            case "f32mat3":
            case "f32mat4":
            case "f32mat2x2":
            case "f32mat2x3":
            case "f32mat2x4":
            case "f32mat3x2":
            case "f32mat3x3":
            case "f32mat3x4":
            case "f32mat4x2":
            case "f32mat4x3":
            case "f32mat4x4":
                if (On("GL_EXT_shader_explicit_arithmetic_types") || On("GL_EXT_shader_explicit_arithmetic_types_float32"))
                {
                    return Keyword;
                }
                return Identifier;

            case "float64_t":
            case "f64vec2":
            case "f64vec3":
            case "f64vec4":
                if (On("GL_EXT_shader_explicit_arithmetic_types") || (On("GL_NV_gpu_shader5") && On("GL_ARB_gpu_shader_fp64")) || On("GL_EXT_shader_explicit_arithmetic_types_float64"))
                {
                    return Keyword;
                }
                return Identifier;

            case "f64mat2":
            case "f64mat3":
            case "f64mat4":
            case "f64mat2x2":
            case "f64mat2x3":
            case "f64mat2x4":
            case "f64mat3x2":
            case "f64mat3x3":
            case "f64mat3x4":
            case "f64mat4x2":
            case "f64mat4x3":
            case "f64mat4x4":
                if (On("GL_EXT_shader_explicit_arithmetic_types") || On("GL_EXT_shader_explicit_arithmetic_types_float64"))
                {
                    return Keyword;
                }
                return Identifier;

            case "float16_t":
            case "f16vec2":
            case "f16vec3":
            case "f16vec4":
                if (On("GL_AMD_gpu_shader_half_float") || On("GL_EXT_shader_16bit_storage") || On("GL_EXT_shader_explicit_arithmetic_types") || On("GL_NV_gpu_shader5") || On("GL_EXT_shader_explicit_arithmetic_types_float16"))
                {
                    return Keyword;
                }
                return Identifier;

            case "f16mat2":
            case "f16mat3":
            case "f16mat4":
            case "f16mat2x2":
            case "f16mat2x3":
            case "f16mat2x4":
            case "f16mat3x2":
            case "f16mat3x3":
            case "f16mat3x4":
            case "f16mat4x2":
            case "f16mat4x3":
            case "f16mat4x4":
                if (On("GL_AMD_gpu_shader_half_float") || On("GL_EXT_shader_explicit_arithmetic_types") || On("GL_EXT_shader_explicit_arithmetic_types_float16"))
                {
                    return Keyword;
                }

                return Identifier;

            case "bfloat16_t":
            case "bf16vec2":
            case "bf16vec3":
            case "bf16vec4":
                if (On("GL_EXT_bfloat16"))
                {
                    return Keyword;
                }

                return Identifier;

            case "floate5m2_t":
            case "fe5m2vec2":
            case "fe5m2vec3":
            case "fe5m2vec4":
                if (On("GL_EXT_float_e5m2"))
                {
                    return Keyword;
                }

                return Identifier;

            case "floate4m3_t":
            case "fe4m3vec2":
            case "fe4m3vec3":
            case "fe4m3vec4":
                if (On("GL_EXT_float_e4m3"))
                {
                    return Keyword;
                }

                return Identifier;

            case "floate2m1_t":
            case "fe2m1vec2":
            case "fe2m1vec3":
            case "fe2m1vec4":
                if (On("GL_EXT_float_e2m1"))
                {
                    return Keyword;
                }

                return Identifier;

            case "floate3m2_t":
            case "fe3m2vec2":
            case "fe3m2vec3":
            case "fe3m2vec4":
                if (On("GL_EXT_float_e3m2"))
                {
                    return Keyword;
                }

                return Identifier;

            case "floate2m3_t":
            case "fe2m3vec2":
            case "fe2m3vec3":
            case "fe2m3vec4":
                if (On("GL_EXT_float_e2m3"))
                {
                    return Keyword;
                }

                return Identifier;

            case "floatue8m0_t":
            case "fue8m0vec2":
            case "fue8m0vec3":
            case "fue8m0vec4":
                if (On("GL_EXT_float_ue8m0"))
                {
                    return Keyword;
                }

                return Identifier;

            case "floatmxint8_t":
            case "fmxint8vec2":
            case "fmxint8vec3":
            case "fmxint8vec4":
                if (On("GL_EXT_float_mxint8"))
                {
                    return Keyword;
                }

                return Identifier;

            case "samplerCubeArray":
            case "samplerCubeArrayShadow":
            case "isamplerCubeArray":
            case "usamplerCubeArray":
                if ((Es && Version >= 320) || AnyOn(AepTextureCubeMapArray))
                {
                    return Keyword;
                }

                if (Es || (Version < 400 && ! On("GL_ARB_texture_cube_map_array") && ! AnyOn(AepCoreGpuShader5)))
                {
                    Reserve();
                }

                return Keyword;

            case "textureCubeArray":
            case "itextureCubeArray":
            case "utextureCubeArray":
                if (Vulkan)
                {
                    return Keyword;
                }
                else
                {
                    return Identifier;
                }

            case "uint":
            case "uvec2":
            case "uvec3":
            case "uvec4":
            case "samplerCubeShadow":
            case "sampler2DArray":
            case "sampler2DArrayShadow":
            case "isampler2D":
            case "isampler3D":
            case "isamplerCube":
            case "isampler2DArray":
            case "usampler2D":
            case "usampler3D":
            case "usamplerCube":
            case "usampler2DArray":
                if (word == "sampler2DArray" || word == "sampler2DArrayShadow")
                {
                    if (!Es && (On("GL_EXT_texture_array")))
                    {
                        return Keyword;
                    }
                }
                return NonreservedKeyword(300, 130);

            case "sampler3D":
                if (Es && Version < 300)
                {
                    if (!On("GL_OES_texture_3D"))
                    {
                        Reserve();
                    }
                }
                return Keyword;

            case "sampler2DShadow":
                if (Es && Version < 300)
                {
                    if (!On("GL_EXT_shadow_samplers"))
                    {
                        Reserve();
                    }
                }
                return Keyword;

            case "texture2D":
            case "textureCube":
            case "texture2DArray":
            case "itexture2D":
            case "itexture3D":
            case "itextureCube":
            case "itexture2DArray":
            case "utexture2D":
            case "utexture3D":
            case "utextureCube":
            case "utexture2DArray":
            case "texture3D":
            case "sampler":
            case "samplerShadow":
                if (Vulkan)
                {
                    return Keyword;
                }
                else
                {
                    return Identifier;
                }

            case "isampler1D":
            case "isampler1DArray":
            case "sampler1DArrayShadow":
            case "usampler1D":
            case "usampler1DArray":
                if (word == "sampler1DArrayShadow")
                {
                    if (!Es && (On("GL_EXT_texture_array")))
                    {
                        return Keyword;
                    }
                }
                return Es30ReservedFromGlsl(130);

            case "isampler2DRect":
            case "usampler2DRect":
                return Es30ReservedFromGlsl(140);

            case "samplerBuffer":
                if ((Es && Version >= 320) || AnyOn(AepTextureBuffer))
                {
                    return Keyword;
                }

                return Es30ReservedFromGlsl(130);

            case "isamplerBuffer":
            case "usamplerBuffer":
                if ((Es && Version >= 320) || AnyOn(AepTextureBuffer))
                {
                    return Keyword;
                }

                return Es30ReservedFromGlsl(140);

            case "sampler2DMS":
            case "isampler2DMS":
            case "usampler2DMS":
                if (Es && Version >= 310)
                {
                    return Keyword;
                }

                if (!Es && (Version > 140 || (Version == 140 && On("GL_ARB_texture_multisample"))))
                {
                    return Keyword;
                }

                return Es30ReservedFromGlsl(150);

            case "sampler2DMSArray":
            case "isampler2DMSArray":
            case "usampler2DMSArray":
                if ((Es && Version >= 320) || On("GL_OES_texture_storage_multisample_2d_array"))
                {
                    return Keyword;
                }

                if (!Es && (Version > 140 || (Version == 140 && On("GL_ARB_texture_multisample"))))
                {
                    return Keyword;
                }

                return Es30ReservedFromGlsl(150);

            case "sampler1D":
            case "sampler1DShadow":
                if (Es)
                {
                    Reserve();
                }

                return Keyword;

            case "sampler2DRect":
            case "sampler2DRectShadow":
                if (Es)
                {
                    Reserve();
                }
                else if (Version < 140 && ! On("GL_ARB_texture_rectangle"))
                {
                    Reserve();
                }
                return Keyword;

            case "sampler1DArray":
                if (Es && Version == 300)
                {
                    Reserve();
                }
                else if ((Es && Version < 300) || ((!Es && Version < 130) && !On("GL_EXT_texture_array")))
                {
                    return Identifier;
                }

                return Keyword;

            case "samplerExternalOES":
                if (On("GL_OES_EGL_image_external") || On("GL_OES_EGL_image_external_essl3"))
                {
                    return Keyword;
                }

                return Identifier;

            case "__samplerExternal2DY2YEXT":
                if (On("GL_EXT_YUV_target"))
                {
                    return Keyword;
                }

                return Identifier;

            case "itexture1DArray":
            case "utexture1D":
            case "itexture1D":
            case "utexture1DArray":
            case "textureBuffer":
            case "itexture2DRect":
            case "utexture2DRect":
            case "itextureBuffer":
            case "utextureBuffer":
            case "texture2DMS":
            case "itexture2DMS":
            case "utexture2DMS":
            case "texture2DMSArray":
            case "itexture2DMSArray":
            case "utexture2DMSArray":
            case "texture1D":
            case "texture2DRect":
            case "texture1DArray":
                if (Vulkan)
                {
                    return Keyword;
                }
                else
                {
                    return Identifier;
                }

            case "subpassInput":
            case "subpassInputMS":
            case "isubpassInput":
            case "isubpassInputMS":
            case "usubpassInput":
            case "usubpassInputMS":
            case "attachmentEXT":
            case "iattachmentEXT":
            case "uattachmentEXT":
                if (Vulkan)
                {
                    return Keyword;
                }
                else
                {
                    return Identifier;
                }

            case "f16sampler1D":
            case "f16sampler2D":
            case "f16sampler3D":
            case "f16sampler2DRect":
            case "f16samplerCube":
            case "f16sampler1DArray":
            case "f16sampler2DArray":
            case "f16samplerCubeArray":
            case "f16samplerBuffer":
            case "f16sampler2DMS":
            case "f16sampler2DMSArray":
            case "f16sampler1DShadow":
            case "f16sampler2DShadow":
            case "f16sampler1DArrayShadow":
            case "f16sampler2DArrayShadow":
            case "f16sampler2DRectShadow":
            case "f16samplerCubeShadow":
            case "f16samplerCubeArrayShadow":

            case "f16image1D":
            case "f16image2D":
            case "f16image3D":
            case "f16image2DRect":
            case "f16imageCube":
            case "f16image1DArray":
            case "f16image2DArray":
            case "f16imageCubeArray":
            case "f16imageBuffer":
            case "f16image2DMS":
            case "f16image2DMSArray":

            case "f16texture1D":
            case "f16texture2D":
            case "f16texture3D":
            case "f16texture2DRect":
            case "f16textureCube":
            case "f16texture1DArray":
            case "f16texture2DArray":
            case "f16textureCubeArray":
            case "f16textureBuffer":
            case "f16texture2DMS":
            case "f16texture2DMSArray":

            case "f16subpassInput":
            case "f16subpassInputMS":
                if (On("GL_AMD_gpu_shader_half_float_fetch"))
                {
                    return Keyword;
                }
                return Identifier;

            case "__explicitInterpAMD":
                if (On("GL_AMD_shader_explicit_vertex_parameter"))
                {
                    return Keyword;
                }

                return Identifier;

            case "pervertexNV":
                if ((!Es && Version >= 450) || On("GL_NV_fragment_shader_barycentric"))
                {
                    return Keyword;
                }

                return Identifier;

            case "pervertexEXT":
                if ((!Es && Version >= 450) || On("GL_EXT_fragment_shader_barycentric"))
                {
                    return Keyword;
                }

                return Identifier;

            case "precise":
                if ((Es && (Version >= 320 || AnyOn(AepGpuShader5))) || (!Es && (Version >= 400 || AnyOn(AepCoreGpuShader5))))
                {
                    return Keyword;
                }

                if (Es && Version == 310)
                {
                    Reserve();
                    return Keyword;
                }
                return Identifier;

            case "perprimitiveNV":
            case "perviewNV":
            case "taskNV":
                if (On("GL_NV_mesh_shader"))
                {
                    return Keyword;
                }

                return Identifier;

            case "perprimitiveEXT":
            case "taskPayloadSharedEXT":
                if (On("GL_EXT_mesh_shader"))
                {
                    return Keyword;
                }

                return Identifier;

            case "fcoopmatNV":
                if (On("GL_NV_cooperative_matrix"))
                {
                    return Keyword;
                }
                return Identifier;

            case "ucoopmatNV":
            case "icoopmatNV":
                if (On("GL_NV_integer_cooperative_matrix"))
                {
                    return Keyword;
                }
                return Identifier;

            case "tensorARM":
                if (On("GL_ARM_tensors"))
                {
                    return Keyword;
                }
                return Identifier;

            case "coopmat":
                if (On("GL_KHR_cooperative_matrix"))
                {
                    return Keyword;
                }
                return Identifier;

            case "coopvecNV":
                if (On("GL_NV_cooperative_vector"))
                {
                    return Keyword;
                }
                return Identifier;

            case "vector":
                if (On("GL_EXT_long_vector"))
                {
                    return Keyword;
                }
                return Identifier;

            case "demote":
                if (On("GL_EXT_demote_to_helper_invocation"))
                {
                    return Keyword;
                }
                else
                {
                    return Identifier;
                }

            case "spirv_instruction":
            case "spirv_execution_mode":
            case "spirv_execution_mode_id":
            case "spirv_decorate":
            case "spirv_decorate_id":
            case "spirv_decorate_string":
            case "spirv_type":
            case "spirv_storage_class":
            case "spirv_by_reference":
            case "spirv_literal":
                if (On("GL_EXT_spirv_intrinsics"))
                {
                    return Keyword;
                }

                return Identifier;

            case "hitObjectNV":
                if ((!Es && Version >= 460 && On("GL_NV_shader_invocation_reorder")))
                {
                    return Keyword;
                }

                return Identifier;

            case "hitObjectEXT":
                if ((!Es && Version >= 460 && On("GL_EXT_shader_invocation_reorder")))
                {
                    return Keyword;
                }

                return Identifier;

            case "hitObjectAttributeNV":
                if ((!Es && Version >= 460 && On("GL_NV_shader_invocation_reorder")))
                {
                    return Keyword;
                }

                return Identifier;

            case "hitObjectAttributeEXT":
                if ((!Es && Version >= 460 && On("GL_EXT_shader_invocation_reorder")))
                {
                    return Keyword;
                }

                return Identifier;

            case "__function":
            case "tensorLayoutNV":
            case "tensorViewNV":
                if (On("GL_NV_cooperative_matrix2"))
                {
                    return Keyword;
                }
                return Identifier;

            case "resourceheap":
            case "samplerheap":
                if (On("GL_EXT_structured_descriptor_heap") && On("GL_EXT_descriptor_heap"))
                {
                    return Keyword;
                }

                return Identifier;

            case "inline":
            case "noinline":
                if (!On("GL_EXT_function_control_attributes"))
                {
                    return Reserved;
                }

                return Keyword;

            default:
                return Identifier;
        }
    }

    private GlslWordKind IdentifierOrReserved(bool reserved) => reserved ? Reserved : Identifier;

    /// <summary>ES の 3.00 で予約語になり、デスクトップでは version からキーワードになった語。</summary>
    private GlslWordKind Es30ReservedFromGlsl(int version)
    {
        if ((Es && Version < 300) || (!Es && Version < version))
        {
            return Identifier;
        }

        if (Es && Version >= 300)
        {
            Reserve();
        }

        return Keyword;
    }

    /// <summary>予約されないまま、ES とデスクトップのあるバージョンからキーワードになった語。</summary>
    private GlslWordKind NonreservedKeyword(int esVersion, int nonEsVersion)
        => (Es && Version < esVersion) || (!Es && Version < nonEsVersion) ? Identifier : Keyword;

    private GlslWordKind PrecisionKeyword() => Es || Version >= 130 ? Keyword : Identifier;

    private GlslWordKind MatNxM() => Version > 110 ? Keyword : Identifier;

    private GlslWordKind DMat()
    {
        if (Es && Version >= 300)
        {
            Reserve();
            return Keyword;
        }

        if (!Es && (Version >= 400
                    || (Version >= 150 && On("GL_ARB_gpu_shader_fp64"))
                    || (Version >= 150 && On("GL_ARB_vertex_attrib_64bit") && Stage == GlslShaderStage.Vertex)))
        {
            return Keyword;
        }

        return Identifier;
    }

    private GlslWordKind FirstGenerationImage(bool inEs310)
    {
        if ((!Es && (Version >= 420 || On("GL_ARB_shader_image_load_store")))
            || (inEs310 && Es && Version >= 310))
        {
            return Keyword;
        }

        if ((Es && Version >= 300) || (!Es && Version >= 130))
        {
            Reserve();
            return Keyword;
        }

        return Identifier;
    }

    private GlslWordKind SecondGenerationImage()
    {
        if (Es && Version >= 310)
        {
            Reserve();
            return Keyword;
        }

        return !Es && (Version >= 420 || On("GL_ARB_shader_image_load_store")) ? Keyword : Identifier;
    }
}

/// <summary>語を分けた結果。</summary>
/// <param name="Kind">種類。</param>
/// <param name="IsReservedError">予約語として誤りにするか。キーワードとして読むが、そのバージョンでは使えない語 (<c>double</c> を 1.40 で書くなど) も含む。</param>
internal readonly record struct GlslWord(GlslWordKind Kind, bool IsReservedError);
