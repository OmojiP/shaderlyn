using System.Collections.Frozen;

namespace Shaderlyn.Godot.Parsing;

/// <summary>
/// Godot シェーダー言語のキーワード。Godot 4.7.2 の <c>servers/rendering/shader_language.cpp</c> の <c>keyword_list</c> から写した。
/// </summary>
internal static class GodotKeywords
{
    /// <summary>組み込みの型。</summary>
    public static FrozenSet<string> BuiltInTypes { get; } = new[]
    {
        "void", "bool", "bvec2", "bvec3", "bvec4",
        "int", "ivec2", "ivec3", "ivec4", "uint", "uvec2", "uvec3", "uvec4",
        "float", "vec2", "vec3", "vec4", "mat2", "mat3", "mat4",
        "sampler2D", "isampler2D", "usampler2D", "sampler2DArray", "isampler2DArray", "usampler2DArray",
        "sampler3D", "isampler3D", "usampler3D", "samplerCube", "samplerCubeArray", "samplerExternalOES",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>宣言の前に書く修飾子 (補間・精度・記憶域・<c>instance</c> / <c>global</c>)。</summary>
    public static FrozenSet<string> Qualifiers { get; } = new[]
    {
        "flat", "smooth", "lowp", "mediump", "highp", "uniform", "varying", "const", "instance", "global",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>仮引数の前に書く修飾子。</summary>
    public static FrozenSet<string> ParameterQualifiers { get; } = new[]
    {
        "in", "out", "inout",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>uniform の宣言の <c>:</c> の後ろに書くヒント (フィルターと繰り返しの指定を含む)。</summary>
    public static FrozenSet<string> Hints { get; } = new[]
    {
        "source_color", "color_conversion_disabled", "hint_range", "hint_enum", "instance_index",
        "hint_normal", "hint_default_white", "hint_default_black", "hint_default_transparent", "hint_anisotropy",
        "hint_roughness_r", "hint_roughness_g", "hint_roughness_b", "hint_roughness_a", "hint_roughness_normal", "hint_roughness_gray",
        "hint_screen_texture", "hint_normal_roughness_texture", "hint_depth_texture",
        "hint_blit_source0", "hint_blit_source1", "hint_blit_source2", "hint_blit_source3",
        "filter_nearest", "filter_linear", "filter_nearest_mipmap", "filter_linear_mipmap",
        "filter_nearest_mipmap_anisotropic", "filter_linear_mipmap_anisotropic",
        "repeat_enable", "repeat_disable",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>ファイルの外側に書く、セミコロンで終わる宣言の先頭の語。</summary>
    public static FrozenSet<string> ModeDeclarations { get; } = new[]
    {
        "shader_type", "render_mode", "stencil_mode", "group_uniforms",
    }.ToFrozenSet(StringComparer.Ordinal);
}
