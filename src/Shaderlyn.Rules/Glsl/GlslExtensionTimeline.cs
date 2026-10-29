using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Preprocessing;
using Shaderlyn.CFamily.Syntax;

namespace Shaderlyn.Rules.Glsl;

/// <summary>拡張の振る舞い (<c>#extension name : behavior</c>)。</summary>
internal enum GlslExtensionBehavior
{
    /// <summary>書いていないか <c>disable</c>。</summary>
    Disable,

    /// <summary><c>warn</c>。使えるが、使うと警告する。</summary>
    Warn,

    /// <summary><c>enable</c>。</summary>
    Enable,

    /// <summary><c>require</c>。</summary>
    Require,
}

/// <summary>
/// ファイルの中の位置ごとの、拡張の振る舞い。glslang 16.6.0 の Versions.cpp の <c>updateExtensionBehavior</c> を移したもの。
/// </summary>
/// <remarks>
/// <para>
/// <c>#extension</c> は書いた位置より後ろのコードにだけ効く。プリプロセッサが覚えた指令の位置 (<see cref="RecordedDirective.OutputIndex"/>) と、
/// 構文木のトークンの、展開した出力の中の位置を比べる。
/// </para>
/// <para>
/// 1 つの拡張を有効にすると、ほかの拡張も有効になることがある (<c>GL_KHR_shader_subgroup_arithmetic</c> は <c>GL_KHR_shader_subgroup_basic</c> も)。
/// glslang と同じに広げる。<c>all</c> は、それまでに名前の出た拡張すべてに効く (<c>enable</c> と <c>require</c> は誤り)。
/// </para>
/// </remarks>
internal sealed class GlslExtensionTimeline
{
    /// <summary>有効にすると、一緒に同じ振る舞いになる拡張 (glslang の updateExtensionBehavior)。</summary>
    private static readonly FrozenDictionary<string, string[]> Implied = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["GL_ANDROID_extension_pack_es31a"] =
        [
            "GL_KHR_blend_equation_advanced", "GL_OES_sample_variables", "GL_OES_shader_image_atomic",
            "GL_OES_shader_multisample_interpolation", "GL_OES_texture_storage_multisample_2d_array", "GL_EXT_geometry_shader",
            "GL_EXT_gpu_shader5", "GL_EXT_primitive_bounding_box", "GL_EXT_shader_io_blocks", "GL_EXT_tessellation_shader",
            "GL_EXT_texture_buffer", "GL_EXT_texture_cube_map_array",
        ],
        ["GL_EXT_geometry_shader"] = ["GL_EXT_shader_io_blocks"],
        ["GL_OES_geometry_shader"] = ["GL_OES_shader_io_blocks"],
        ["GL_EXT_tessellation_shader"] = ["GL_EXT_shader_io_blocks"],
        ["GL_OES_tessellation_shader"] = ["GL_OES_shader_io_blocks"],
        ["GL_GOOGLE_include_directive"] = ["GL_GOOGLE_cpp_style_line_directive"],
        ["GL_ARB_shading_language_include"] = ["GL_GOOGLE_cpp_style_line_directive"],
        ["GL_KHR_shader_subgroup_vote"] = ["GL_KHR_shader_subgroup_basic"],
        ["GL_KHR_shader_subgroup_arithmetic"] = ["GL_KHR_shader_subgroup_basic"],
        ["GL_KHR_shader_subgroup_ballot"] = ["GL_KHR_shader_subgroup_basic"],
        ["GL_KHR_shader_subgroup_shuffle"] = ["GL_KHR_shader_subgroup_basic"],
        ["GL_KHR_shader_subgroup_shuffle_relative"] = ["GL_KHR_shader_subgroup_basic"],
        ["GL_KHR_shader_subgroup_clustered"] = ["GL_KHR_shader_subgroup_basic"],
        ["GL_KHR_shader_subgroup_quad"] = ["GL_KHR_shader_subgroup_basic"],
        ["GL_NV_shader_subgroup_partitioned"] = ["GL_KHR_shader_subgroup_basic"],
        ["GL_EXT_buffer_reference2"] = ["GL_EXT_buffer_reference"],
        ["GL_EXT_buffer_reference_uvec2"] = ["GL_EXT_buffer_reference"],
        ["GL_NV_integer_cooperative_matrix"] = ["GL_NV_cooperative_matrix"],
        ["GL_NV_cooperative_matrix2"] = ["GL_KHR_cooperative_matrix"],
        ["GL_EXT_shader_subgroup_extended_types_int8"] = ["GL_EXT_shader_explicit_arithmetic_types_int8"],
        ["GL_EXT_shader_subgroup_extended_types_int16"] = ["GL_EXT_shader_explicit_arithmetic_types_int16"],
        ["GL_EXT_shader_subgroup_extended_types_int64"] = ["GL_EXT_shader_explicit_arithmetic_types_int64"],
        ["GL_EXT_shader_subgroup_extended_types_float16"] = ["GL_EXT_shader_explicit_arithmetic_types_float16"],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary><c>#extension all : warn</c> で決まる、名前の出ていない拡張の振る舞いを置く鍵。</summary>
    private const string AllExtensions = "*";

    /// <summary>展開した出力のトークンから、出力の中の位置。</summary>
    private readonly Dictionary<ShaderSyntaxToken, int> _tokenIndex;

    /// <summary>指令ごとの、その指令の後の振る舞い。出力の中の位置の順。</summary>
    private readonly ImmutableArray<(int OutputIndex, FrozenDictionary<string, GlslExtensionBehavior> State)> _states;

    /// <summary>展開した木から作る。</summary>
    /// <param name="tree">GLSL の木。</param>
    public GlslExtensionTimeline(ShaderSyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        ImmutableArray<ShaderSyntaxToken> tokens = tree.PreprocessResult.Tokens;
        _tokenIndex = new Dictionary<ShaderSyntaxToken, int>(tokens.Length, ReferenceEqualityComparer.Instance);

        for (int i = 0; i < tokens.Length; i++)
        {
            _tokenIndex.TryAdd(tokens[i], i);
        }

        Dictionary<string, GlslExtensionBehavior> current = new(StringComparer.Ordinal);
        ImmutableArray<(int, FrozenDictionary<string, GlslExtensionBehavior>)>.Builder states = ImmutableArray.CreateBuilder<(int, FrozenDictionary<string, GlslExtensionBehavior>)>();

        foreach (RecordedDirective directive in tree.PreprocessResult.RecordedDirectives)
        {
            if (directive.Directive.Text != "extension" || ReadExtension(directive) is not { } extension)
            {
                continue;
            }

            Apply(current, extension.Name, extension.Behavior);
            states.Add((directive.OutputIndex, current.ToFrozenDictionary(StringComparer.Ordinal)));
        }

        _states = states.ToImmutable();
    }

    /// <summary>トークンの時点での、拡張の振る舞いを返す。</summary>
    /// <param name="extension">拡張の名前。</param>
    /// <param name="at">位置のトークン。展開した出力に無いトークン (欠けたトークンなど) なら、ファイルの終わりの時点とする。</param>
    /// <returns>振る舞い。</returns>
    public GlslExtensionBehavior GetBehavior(string extension, ShaderSyntaxToken? at)
    {
        int index = at is not null && _tokenIndex.TryGetValue(at, out int found) ? found : int.MaxValue;
        FrozenDictionary<string, GlslExtensionBehavior>? state = null;

        // 位置より前の、最後の指令の後の振る舞い。指令の数は少ないので、前から見る。
        foreach ((int outputIndex, FrozenDictionary<string, GlslExtensionBehavior> after) in _states)
        {
            if (outputIndex > index)
            {
                break;
            }

            state = after;
        }

        return state is null ? GlslExtensionBehavior.Disable
            : state.TryGetValue(extension, out GlslExtensionBehavior behavior) ? behavior
            : state.GetValueOrDefault(AllExtensions, GlslExtensionBehavior.Disable);
    }

    /// <summary>トークンの時点で、拡張を有効にしているか (<c>enable</c> / <c>require</c> / <c>warn</c>。glslang の extensionTurnedOn)。</summary>
    /// <param name="extension">拡張の名前。</param>
    /// <param name="at">位置のトークン。</param>
    /// <returns>有効なら <see langword="true"/>。</returns>
    public bool IsOn(string extension, ShaderSyntaxToken? at) => GetBehavior(extension, at) != GlslExtensionBehavior.Disable;

    /// <summary><c>#extension</c> の行から、名前と振る舞いを読む。</summary>
    /// <param name="directive">指令。</param>
    /// <returns>名前と振る舞い。形が違えば <see langword="null"/>。</returns>
    public static (string Name, GlslExtensionBehavior Behavior)? ReadExtension(RecordedDirective directive)
    {
        if (directive.Tokens is not [{ Kind: ShaderSyntaxKind.IdentifierToken } name, { Kind: ShaderSyntaxKind.ColonToken }, { } behavior])
        {
            return null;
        }

        GlslExtensionBehavior? parsed = behavior.Text switch
        {
            "require" => GlslExtensionBehavior.Require,
            "enable" => GlslExtensionBehavior.Enable,
            "warn" => GlslExtensionBehavior.Warn,
            "disable" => GlslExtensionBehavior.Disable,
            _ => null,
        };

        return parsed is { } value ? (name.Text, value) : null;
    }

    /// <summary>振る舞いを書き換える。一緒に変わる拡張も書き換える。</summary>
    private static void Apply(Dictionary<string, GlslExtensionBehavior> state, string name, GlslExtensionBehavior behavior)
    {
        if (name == "all")
        {
            // enable と require は誤り (GL0003 が伝える)。warn と disable は、glslang が知っている拡張すべてに効く。
            if (behavior is GlslExtensionBehavior.Warn or GlslExtensionBehavior.Disable)
            {
                foreach (string known in state.Keys.ToList())
                {
                    state[known] = behavior;
                }

                state[AllExtensions] = behavior;
            }

            return;
        }

        state[name] = behavior;

        if (Implied.TryGetValue(name, out string[]? implied))
        {
            foreach (string other in implied)
            {
                Apply(state, other, behavior);
            }
        }
    }
}
