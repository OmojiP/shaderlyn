using System.Collections.Frozen;
using Shaderlyn.Hlsl.Parsing;

namespace Shaderlyn.Slang.Parsing;

/// <summary>
/// Slang のキーワード。HLSL のものに、Slang が足すものを加える。
/// </summary>
/// <remarks>
/// 元のデータは slang 2026.19 の <c>source/slang/slang-parser.cpp</c> (修飾子と宣言のキーワード) と、標準ライブラリ (<c>core.meta.slang</c>) の型。
/// </remarks>
internal static class SlangKeywords
{
    /// <summary>宣言の前に書く修飾子 (HLSL のものに足す分)。</summary>
    public static FrozenSet<string> ExtraDeclarationModifiers { get; } = new[]
    {
        // 可視性とモジュール
        "public", "private", "internal", "export", "__exported", "__extern_cpp", "__global",

        // 自動微分
        "no_diff",

        // 定数
        "constexpr",

        // メンバーと関数
        "override", "mutating", "nonmutating", "__unsafeForceInlineEarly", "__forceinline", "dynamic_uniform", "open", "required",
        "__builtin", "__generic_value_param",

        // 動的な型
        "dyn", "some",

        // 頂点ごとの値 (GetAttributeAtVertex)
        "pervertex",

        // ポインターと記憶域 (__ref は仮引数の修飾子)
        "__ref", "__constref", "constref", "__ptr",

        // GLSL と同じ書き方の修飾子
        "highp", "mediump", "lowp", "flat", "smooth", "buffer", "readonly", "writeonly", "coherent", "restrict",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>括弧の引数を取ることのある修飾子 (<c>__target_intrinsic(hlsl, "…")</c>、<c>__implicit_conversion(100)</c>)。</summary>
    public static FrozenSet<string> ModifiersWithArguments { get; } = new[]
    {
        "__target_intrinsic", "__implicit_conversion", "__specialized_for_target", "__intrinsic_op", "__glsl_extension", "__glsl_version",
        "__spirv_version", "__cuda_sm_version", "__requiresNVAPI", "__readNone", "require", "__attributeTarget", "__exported",
        "__intrinsic_type", "__magic_type",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Slang に固有の宣言を始めるキーワード。</summary>
    public static FrozenSet<string> DeclarationKeywords { get; } = new[]
    {
        "func", "let", "var", "typealias", "associatedtype", "extension", "property", "__init", "__subscript",
        "import", "__import", "module", "implementing", "__include", "__generic",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Slang の標準ライブラリの型のうち、HLSL に無いもの。型として読む (名前が宣言として続くか、型の引数が続く)。</summary>
    public static FrozenSet<string> ExtraTypes { get; } = new[]
    {
        "This", "Ptr", "Tuple", "Optional", "Conditional", "CoopVec", "CoopMat", "Atomic", "DescriptorHandle", "NativeString", "String",
        "Array", "DifferentialPair", "ParameterBlock", "ConstantBuffer", "TextureBuffer", "Sampler2D", "Sampler3D", "SamplerCube",
        "Sampler1D", "Sampler2DArray", "SamplerCubeArray", "Sampler2DMS", "_Texture", "RaytracingAccelerationStructure",
        "int8_t", "uint8_t", "int16_t", "uint16_t", "int64_t", "uint64_t", "float16_t", "float32_t", "float64_t", "half", "Ref",
        "IRWArray", "IArray", "Texture1D", "Texture2D", "Texture3D", "TextureCube", "RWTexture2D",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>型の名前か。HLSL の型と、Slang が足す型。</summary>
    /// <param name="name">名前。</param>
    /// <returns>型なら <see langword="true"/>。</returns>
    public static bool IsKnownTypeName(string name) => HlslKeywords.IsKnownTypeName(name) || ExtraTypes.Contains(name);

    /// <summary>宣言の前に書く修飾子か。HLSL のものと、Slang が足すもの。</summary>
    /// <param name="text">語。</param>
    /// <returns>修飾子なら <see langword="true"/>。</returns>
    public static bool IsDeclarationModifier(string text) => HlslKeywords.DeclarationModifiers.Contains(text) || ExtraDeclarationModifiers.Contains(text);
}
