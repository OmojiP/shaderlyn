using System.Collections.Frozen;

namespace Shaderlyn.Glsl.Parsing;

/// <summary>
/// GLSL の組み込みの型と修飾子。構文解析が、宣言の始まりと型の名前を見分けるために使う。
/// </summary>
/// <remarks>
/// 出典は GLSL 4.60 と GLSL ES 3.20 の仕様書のキーワードの章、および Vulkan で使う拡張
/// (GL_EXT_ray_tracing、GL_EXT_mesh_shader、GL_EXT_shader_explicit_arithmetic_types) である。
/// 組み込みの関数と変数は持たない (意味解析の単位で足す)。
/// </remarks>
internal static class GlslKeywords
{
    /// <summary>組み込みの型の名前。</summary>
    public static FrozenSet<string> BuiltInTypes { get; } = BuildBuiltInTypes();

    /// <summary>宣言の前に置く修飾子 (記憶域・補間・精度・メモリ・不変)。</summary>
    public static FrozenSet<string> Qualifiers { get; } = new[]
    {
        // 記憶域
        "const", "in", "out", "inout", "uniform", "buffer", "shared", "attribute", "varying",
        "centroid", "sample", "patch",

        // 補間・不変
        "flat", "smooth", "noperspective", "invariant", "precise",

        // 精度
        "highp", "mediump", "lowp",

        // メモリ
        "coherent", "volatile", "restrict", "readonly", "writeonly",
        "devicecoherent", "queuefamilycoherent", "workgroupcoherent", "subgroupcoherent", "nonprivate",

        // レイトレーシング (GL_EXT_ray_tracing / GL_NV_ray_tracing)
        "rayPayloadEXT", "rayPayloadInEXT", "hitAttributeEXT", "callableDataEXT", "callableDataInEXT", "shaderRecordEXT",
        "rayPayloadNV", "rayPayloadInNV", "hitAttributeNV", "callableDataNV", "callableDataInNV", "shaderRecordNV",

        // メッシュシェーダー (GL_EXT_mesh_shader / GL_NV_mesh_shader)
        "taskPayloadSharedEXT", "perprimitiveEXT", "perprimitiveNV", "perviewNV", "taskNV",

        // 重心座標 (GL_EXT_fragment_shader_barycentric / GL_NV_fragment_shader_barycentric)
        "pervertexEXT", "pervertexNV",

        "subroutine",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// インターフェースブロックを作る修飾子。これらのどれかの後に <c>名前 {</c> が続けば、ブロックとして読む。
    /// </summary>
    public static FrozenSet<string> BlockQualifiers { get; } = new[]
    {
        "uniform", "buffer", "in", "out", "shared",
        "rayPayloadEXT", "rayPayloadInEXT", "hitAttributeEXT", "callableDataEXT", "callableDataInEXT", "shaderRecordEXT",
        "rayPayloadNV", "rayPayloadInNV", "hitAttributeNV", "callableDataNV", "callableDataInNV", "shaderRecordNV",
        "taskPayloadSharedEXT", "taskNV", "perprimitiveEXT", "perprimitiveNV",
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
        };

        // 明示的な幅の算術型 (GL_EXT_shader_explicit_arithmetic_types)
        foreach (string scalar in (string[])["int8_t", "uint8_t", "int16_t", "uint16_t", "int32_t", "uint32_t", "int64_t", "uint64_t", "float16_t", "float32_t", "float64_t"])
        {
            types.Add(scalar);
        }

        for (int n = 2; n <= 4; n++)
        {
            foreach (string prefix in (string[])["", "b", "i", "u", "d", "i8", "u8", "i16", "u16", "i32", "u32", "i64", "u64", "f16", "f32", "f64"])
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

        // サンプラー・テクスチャ・イメージ。i / u は要素が整数のもの。
        string[] shapes = ["1D", "2D", "3D", "Cube", "2DRect", "1DArray", "2DArray", "CubeArray", "Buffer", "2DMS", "2DMSArray"];

        foreach (string element in (string[])["", "i", "u"])
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

        foreach (string shape in (string[])["1D", "2D", "Cube", "2DRect", "1DArray", "2DArray", "CubeArray"])
        {
            types.Add($"sampler{shape}Shadow");
        }

        types.Add("samplerExternalOES");
        return types.ToFrozenSet(StringComparer.Ordinal);
    }
}
