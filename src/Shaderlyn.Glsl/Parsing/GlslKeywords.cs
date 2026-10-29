using System.Collections.Frozen;

namespace Shaderlyn.Glsl.Parsing;

/// <summary>
/// GLSL の組み込みの型と修飾子。構文解析が、宣言の始まりと型の名前を見分けるために使う。
/// </summary>
/// <remarks>
/// glslang 16.6.0 の文法 (<c>glslang.y</c> の <c>type_specifier_nonarray</c> と、修飾子の規則) と、
/// 字句解析のキーワードの表 (<c>Scan.cpp</c>) に揃えている。拡張で足される型と修飾子も含む
/// (glslang は拡張を有効にしていなくても、これらをキーワードとして読み、使った所で拡張を求める)。
/// 組み込みの関数と変数は持たない (意味解析の単位で足す)。
/// </remarks>
internal static class GlslKeywords
{
    /// <summary>
    /// 型の引数を山括弧で取る型 (<c>coopmat&lt;float, gl_ScopeSubgroup, 16, 16, gl_MatrixUseA&gt;</c>、<c>vector&lt;float, 5&gt;</c>)。
    /// </summary>
    public static FrozenSet<string> ParameterizedTypes { get; } = new[]
    {
        // 協調行列 (GL_NV_cooperative_matrix / GL_KHR_cooperative_matrix)、協調ベクトル (GL_NV_cooperative_vector)
        "fcoopmatNV", "icoopmatNV", "ucoopmatNV", "coopmat", "coopvecNV",

        // テンソル (GL_NV_cooperative_matrix2 / GL_ARM_tensors)
        "tensorLayoutNV", "tensorViewNV", "tensorARM",

        // 長いベクトル (GL_EXT_long_vector)
        "vector",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>組み込みの型の名前。<see cref="ParameterizedTypes"/> を含むので、その後ろで初期化する。</summary>
    public static FrozenSet<string> BuiltInTypes { get; } = BuildBuiltInTypes();

    /// <summary>宣言の前に置く修飾子 (記憶域・補間・精度・メモリ・不変)。</summary>
    public static FrozenSet<string> Qualifiers { get; } = new[]
    {
        // 記憶域
        "const", "in", "out", "inout", "uniform", "buffer", "shared", "attribute", "varying",
        "centroid", "sample", "patch",

        // 補間・不変
        "flat", "smooth", "noperspective", "invariant", "precise", "__explicitInterpAMD",

        // 精度
        "highp", "mediump", "lowp",

        // メモリ
        "coherent", "volatile", "restrict", "readonly", "writeonly",
        "devicecoherent", "queuefamilycoherent", "workgroupcoherent", "subgroupcoherent", "nonprivate",
        "shadercallcoherent", "nontemporal",

        // レイトレーシング (GL_EXT_ray_tracing / GL_NV_ray_tracing)、ヒットオブジェクト (GL_EXT_shader_invocation_reorder / GL_NV_…)
        "rayPayloadEXT", "rayPayloadInEXT", "hitAttributeEXT", "callableDataEXT", "callableDataInEXT", "shaderRecordEXT",
        "rayPayloadNV", "rayPayloadInNV", "hitAttributeNV", "callableDataNV", "callableDataInNV", "shaderRecordNV",
        "hitObjectAttributeEXT", "hitObjectAttributeNV",

        // メッシュシェーダー (GL_EXT_mesh_shader / GL_NV_mesh_shader)
        "taskPayloadSharedEXT", "perprimitiveEXT", "perprimitiveNV", "perviewNV", "taskNV",

        // 重心座標 (GL_EXT_fragment_shader_barycentric / GL_NV_fragment_shader_barycentric)
        "pervertexEXT", "pervertexNV",

        // タイルイメージ (GL_EXT_shader_tile_image)、ディスクリプターヒープ (GL_EXT_descriptor_heap)
        "tileImageEXT", "resourceheap", "samplerheap",

        // 一様でない値 (GL_EXT_nonuniform_qualifier)
        "nonuniformEXT",

        // SPIR-V の組み込み (GL_EXT_spirv_intrinsics)。spirv_storage_class(…) などの括弧を取るものは、layout と同じく別に読む
        "spirv_by_reference", "spirv_literal",

        "subroutine",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// 括弧に値を取る修飾子 (<c>layout(…)</c> と GL_EXT_spirv_intrinsics の <c>spirv_decorate(…)</c> など)。括弧ごと修飾子として読む。
    /// </summary>
    public static FrozenSet<string> QualifierGroups { get; } = new[]
    {
        "layout", "spirv_storage_class", "spirv_decorate", "spirv_decorate_id", "spirv_decorate_string",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// インターフェースブロックを作る修飾子。これらのどれかの後に <c>名前 {</c> が続けば、ブロックとして読む。
    /// </summary>
    public static FrozenSet<string> BlockQualifiers { get; } = new[]
    {
        "uniform", "buffer", "in", "out", "shared",
        "rayPayloadEXT", "rayPayloadInEXT", "hitAttributeEXT", "callableDataEXT", "callableDataInEXT", "shaderRecordEXT",
        "rayPayloadNV", "rayPayloadInNV", "hitAttributeNV", "callableDataNV", "callableDataInNV", "shaderRecordNV",
        "hitObjectAttributeEXT", "hitObjectAttributeNV",
        "taskPayloadSharedEXT", "taskNV", "perprimitiveEXT", "perprimitiveNV",
        "resourceheap", "samplerheap",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>組み込みの型かを判定する。</summary>
    /// <param name="name">判定する名前。</param>
    /// <returns>組み込みの型なら <see langword="true"/>。</returns>
    public static bool IsBuiltInType(string name) => BuiltInTypes.Contains(name);

    private static FrozenSet<string> BuildBuiltInTypes()
    {
        HashSet<string> types = new(StringComparer.Ordinal)
        {
            "void", "bool", "int", "uint", "float", "double", "atomic_uint",
            "sampler", "samplerShadow", "accelerationStructureEXT", "accelerationStructureNV", "rayQueryEXT",
            "hitObjectEXT", "hitObjectNV", "samplerExternalOES", "__samplerExternal2DY2YEXT",

            // GL_EXT_spirv_intrinsics の関数の型 (glslang の内部向け)
            "__function",
        };

        // 明示的な幅の算術型 (GL_EXT_shader_explicit_arithmetic_types)、bfloat16 と 8 ビットの浮動小数点数
        foreach (string scalar in (string[])[
            "int8_t", "uint8_t", "int16_t", "uint16_t", "int32_t", "uint32_t", "int64_t", "uint64_t", "float16_t", "float32_t", "float64_t",
            "bfloat16_t", "floate5m2_t", "floate4m3_t", "floate2m1_t", "floate3m2_t", "floate2m3_t", "floatue8m0_t", "floatmxint8_t"])
        {
            types.Add(scalar);
        }

        for (int n = 2; n <= 4; n++)
        {
            foreach (string prefix in (string[])[
                "", "b", "i", "u", "d", "i8", "u8", "i16", "u16", "i32", "u32", "i64", "u64", "f16", "f32", "f64",
                "bf16", "fe5m2", "fe4m3", "fe2m1", "fe3m2", "fe2m3", "fue8m0", "fmxint8"])
            {
                types.Add($"{prefix}vec{n}");
            }

            foreach (string prefix in (string[])["", "d", "f16", "f32", "f64"])
            {
                types.Add($"{prefix}mat{n}");

                for (int m = 2; m <= 4; m++)
                {
                    types.Add($"{prefix}mat{n}x{m}");
                }
            }
        }

        // サンプラー・テクスチャ・イメージ。i / u は要素が整数、f16 は 16 ビットの浮動小数点数 (GL_AMD_gpu_shader_half_float_fetch)。
        string[] shapes = ["1D", "2D", "3D", "Cube", "2DRect", "1DArray", "2DArray", "CubeArray", "Buffer", "2DMS", "2DMSArray"];

        foreach (string element in (string[])["", "i", "u", "f16"])
        {
            foreach (string shape in shapes)
            {
                types.Add($"{element}sampler{shape}");
                types.Add($"{element}texture{shape}");
                types.Add($"{element}image{shape}");
            }

            types.Add($"{element}subpassInput");
            types.Add($"{element}subpassInputMS");
        }

        // 64 ビットの整数のイメージ (GL_EXT_shader_image_int64)
        foreach (string shape in shapes)
        {
            types.Add($"i64image{shape}");
            types.Add($"u64image{shape}");
        }

        foreach (string element in (string[])["", "f16"])
        {
            foreach (string shape in (string[])["1D", "2D", "Cube", "2DRect", "1DArray", "2DArray", "CubeArray"])
            {
                types.Add($"{element}sampler{shape}Shadow");
            }
        }

        // タイルイメージの添付 (GL_EXT_shader_tile_image)
        foreach (string element in (string[])["", "i", "u"])
        {
            types.Add($"{element}attachmentEXT");
        }

        types.UnionWith(ParameterizedTypes);
        return types.ToFrozenSet(StringComparer.Ordinal);
    }
}
