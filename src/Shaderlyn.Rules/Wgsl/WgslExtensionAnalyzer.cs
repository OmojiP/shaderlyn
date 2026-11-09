using System.Collections.Frozen;
using System.Collections.Immutable;
using Shaderlyn.CFamily.Syntax;
using Shaderlyn.Core.Analysis;
using Shaderlyn.Core.Diagnostics;
using Shaderlyn.Semantics;
using Shaderlyn.Semantics.Programs;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Rules.Wgsl;

/// <summary>
/// WGSL の、<c>enable</c> していない拡張の機能を使っていないかを検査する (WG0001)。
/// </summary>
/// <remarks>
/// <para>
/// naga 30 が拡張を求めるもの (<c>front/wgsl</c> の <c>require_enable_extension</c> と <c>EnableExtensionNotEnabled</c>) を、書いた位置ごとに見る。
/// </para>
/// <list type="bullet">
/// <item>型: <c>f16</c> とその短い名前 (<c>vec3h</c>)、接尾辞 <c>h</c> の数、<c>i16</c> / <c>u16</c>、<c>binding_array</c>、
/// 光線の照会の型 (<c>acceleration_structure</c>・<c>ray_query</c>・<c>RayDesc</c>・<c>RayIntersection</c>)、型引数の <c>vertex_return</c>、協調行列 (<c>coop_mat8x8</c>)</item>
/// <item>属性: <c>@blend_src</c>、メッシュシェーダー (<c>@task</c>・<c>@mesh</c>・<c>@payload</c>・<c>@per_primitive</c>)、光線追跡 (<c>@ray_generation</c> など)</item>
/// <item><c>@builtin</c> の値 (<c>clip_distances</c>・<c>primitive_index</c>・<c>draw_index</c>・メッシュシェーダーのもの) と、<c>@interpolate(per_vertex)</c></item>
/// <item>アドレス空間: <c>var&lt;task_payload&gt;</c>、<c>var&lt;ray_payload&gt;</c>・<c>var&lt;incoming_ray_payload&gt;</c></item>
/// </list>
/// <para>
/// 型の名前は、利用者が同じ名前を宣言していれば (WGSL ではあらかじめ宣言された名前を隠せる) 見ない。
/// </para>
/// </remarks>
internal sealed class WgslExtensionAnalyzer : SemanticRuleAnalyzer
{
    private const string RayQuery = "wgpu_ray_query";
    private const string RayTracing = "wgpu_ray_tracing_pipeline";
    private const string MeshShader = "wgpu_mesh_shader";

    /// <summary>型の名前と、要る拡張。</summary>
    private static readonly FrozenDictionary<string, string> Types = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["f16"] = "f16",
        ["i16"] = "wgpu_int16",
        ["u16"] = "wgpu_int16",
        ["binding_array"] = "wgpu_binding_array",
        ["acceleration_structure"] = RayQuery,
        ["ray_query"] = RayQuery,
        ["RayDesc"] = RayQuery,
        ["RayIntersection"] = RayQuery,
        ["coop_mat8x8"] = "wgpu_cooperative_matrix",
        ["coop_mat16x16"] = "wgpu_cooperative_matrix",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>属性の名前と、要る拡張。</summary>
    private static readonly FrozenDictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["blend_src"] = "dual_source_blending",
        ["task"] = MeshShader,
        ["mesh"] = MeshShader,
        ["payload"] = MeshShader,
        ["per_primitive"] = MeshShader,
        ["ray_generation"] = RayTracing,
        ["any_hit"] = RayTracing,
        ["closest_hit"] = RayTracing,
        ["miss"] = RayTracing,
        ["incoming_payload"] = RayTracing,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary><c>@builtin</c> の値と、要る拡張。</summary>
    private static readonly FrozenDictionary<string, string> Builtins = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["clip_distances"] = "clip_distances",
        ["primitive_index"] = "primitive_index",
        ["draw_index"] = "draw_index",
        ["cull_primitive"] = MeshShader,
        ["point_index"] = MeshShader,
        ["line_indices"] = MeshShader,
        ["triangle_indices"] = MeshShader,
        ["vertex_count"] = MeshShader,
        ["vertices"] = MeshShader,
        ["primitive_count"] = MeshShader,
        ["primitives"] = MeshShader,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>アドレス空間と、要る拡張。</summary>
    private static readonly FrozenDictionary<string, string> AddressSpaces = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["task_payload"] = MeshShader,
        ["ray_payload"] = RayTracing,
        ["incoming_ray_payload"] = RayTracing,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <inheritdoc/>
    protected override bool AnalyzesWgsl => true;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [WgslRuleDescriptors.ExtensionNotEnabled, WgslRuleDescriptors.UnknownExtension];

    /// <summary>naga 30 が知っている enable の拡張 (<c>parse/directive/enable_extension.rs</c>)。</summary>
    private static readonly FrozenSet<string> KnownEnableExtensions = FrozenSet.ToFrozenSet(
    [
        "clip_distances", "draw_index", "dual_source_blending", "f16", "primitive_index", "subgroups", "wgpu_binding_array", "wgpu_cooperative_matrix",
        "wgpu_int16", "wgpu_mesh_shader", "wgpu_per_vertex", "wgpu_ray_query", "wgpu_ray_query_vertex_return", "wgpu_ray_tracing_pipeline",
    ], StringComparer.Ordinal);

    /// <summary>naga 30 が知っている言語の拡張 (requires。<c>parse/directive/language_extension.rs</c>)。</summary>
    private static readonly FrozenSet<string> KnownLanguageExtensions = FrozenSet.ToFrozenSet(
        ["packed_4x8_integer_dot_product", "pointer_composite_access", "readonly_and_readwrite_storage_textures", "unrestricted_pointer_parameters"],
        StringComparer.Ordinal);

    /// <inheritdoc/>
    protected override void Analyze(SyntaxTreeAnalysisContext context, ShaderCompilation compilation)
    {
        if (!compilation.IsWgsl)
        {
            return;
        }

        foreach (AnalyzedProgram program in compilation.Programs)
        {
            CompilationUnitSyntax root = program.Tree.Root;
            HashSet<string> enabled = new(StringComparer.Ordinal);
            List<ShaderSyntaxToken> tokens = [];

            foreach (DeclarationSyntax declaration in root.Declarations)
            {
                // enable f16, wgpu_ray_query; requires …; diagnostic(…); は見ない。enable の名前を集める。
                if (declaration is IncompleteDeclarationSyntax { Tokens: [{ Text: "enable" or "requires" or "diagnostic" } directive, ..] directives })
                {
                    if (directive.Text == "enable")
                    {
                        enabled.UnionWith(directives.Skip(1).Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken).Select(t => t.Text));
                    }

                    // naga が知らない拡張の名前 (WG0003)。
                    if (directive.Text is "enable" or "requires")
                    {
                        FrozenSet<string> known = directive.Text == "enable" ? KnownEnableExtensions : KnownLanguageExtensions;

                        foreach (ShaderSyntaxToken name in directives.Skip(1).Where(t => t.Kind == ShaderSyntaxKind.IdentifierToken && !known.Contains(t.Text)))
                        {
                            if (compilation.IsReportable(name) && name.GetLocation() is { } unknownLocation)
                            {
                                context.ReportDiagnostic(Diagnostic.Create(WgslRuleDescriptors.UnknownExtension, unknownLocation,
                                    $"'{name.Text}' という{(directive.Text == "enable" ? "" : "言語の")}拡張はありません ({string.Join("・", known.Order(StringComparer.Ordinal))})。"));
                            }
                        }
                    }

                    continue;
                }

                tokens.AddRange(declaration.DescendantTokens());
            }

            HashSet<string> declared = [.. root.Declarations.SelectMany(DeclaredNames)];

            for (int i = 0; i < tokens.Count; i++)
            {
                if (Required(tokens, i, declared) is not { } extension || enabled.Contains(extension) || IsAlternative(extension, enabled))
                {
                    continue;
                }

                ShaderSyntaxToken token = tokens[i];

                if (compilation.IsReportable(token) && token.GetLocation() is { } location)
                {
                    context.ReportDiagnostic(Diagnostic.Create(WgslRuleDescriptors.ExtensionNotEnabled, location, token.Text, extension));
                }
            }
        }
    }

    /// <summary>光線の照会の型は、光線追跡の拡張でも使える。</summary>
    private static bool IsAlternative(string extension, HashSet<string> enabled)
        => extension == RayQuery && enabled.Contains(RayTracing);

    /// <summary>そのトークンが要る拡張。要らなければ <see langword="null"/>。</summary>
    private static string? Required(List<ShaderSyntaxToken> tokens, int i, HashSet<string> declared)
    {
        ShaderSyntaxToken token = tokens[i];
        ShaderSyntaxToken? previous = i > 0 ? tokens[i - 1] : null;
        ShaderSyntaxToken? beforePrevious = i > 1 ? tokens[i - 2] : null;

        if (token.Kind == ShaderSyntaxKind.NumericLiteralToken)
        {
            return WgslTypeSystem.Instance.GetLiteralTypeName(token) == "f16" ? "f16" : null;
        }

        if (token.Kind != ShaderSyntaxKind.IdentifierToken || token.IsMissing)
        {
            return null;
        }

        string text = token.Text;

        // @blend_src(0) など
        if (previous is { Kind: ShaderSyntaxKind.BadToken, Text: "@" })
        {
            return Attributes.GetValueOrDefault(text);
        }

        // @builtin(clip_distances)
        if (previous is { Kind: ShaderSyntaxKind.OpenParenToken } && beforePrevious is { Text: "builtin" })
        {
            return Builtins.GetValueOrDefault(text);
        }

        // @interpolate(per_vertex)
        if (previous is { Kind: ShaderSyntaxKind.OpenParenToken } && beforePrevious is { Text: "interpolate" })
        {
            return text == "per_vertex" ? "wgpu_per_vertex" : null;
        }

        // var<task_payload>
        if (previous is { Kind: ShaderSyntaxKind.LessThanToken } && beforePrevious is { Text: "var" })
        {
            return AddressSpaces.GetValueOrDefault(text);
        }

        // acceleration_structure<vertex_return>
        if (text == "vertex_return" && previous is { Kind: ShaderSyntaxKind.LessThanToken } && beforePrevious is { Text: "acceleration_structure" or "ray_query" })
        {
            return "wgpu_ray_query_vertex_return";
        }

        if (declared.Contains(text))
        {
            return null;
        }

        if (Types.TryGetValue(text, out string? extension))
        {
            return extension;
        }

        // vec3h・mat4x4h (f16 の短い名前)
        return WgslTypeSystem.Instance.TryDescribeNumeric(text, out ShaderNumericShape shape) && shape.BaseName == "f16" ? "f16" : null;
    }

    /// <summary>ファイルの直下で宣言した名前。</summary>
    private static IEnumerable<string> DeclaredNames(DeclarationSyntax declaration) => declaration switch
    {
        StructDeclarationSyntax structure when structure.NameToken is { } name => [name.Text],
        FunctionDeclarationSyntax function => [function.Name],
        VariableDeclarationSyntax variable => variable.Declarators.Select(d => d.Node).OfType<VariableDeclaratorSyntax>().Select(d => d.Name),
        TypedefDeclarationSyntax alias when alias.Tokens is [{ } name, ..] => [name.Text],
        _ => [],
    };
}
