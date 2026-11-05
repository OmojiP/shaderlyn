using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>Godot の組み込みの変数 1 つ。</summary>
/// <param name="Name">名前 (<c>ALBEDO</c>)。</param>
/// <param name="Type">型 (<c>vec3</c>)。</param>
/// <param name="Function">使える関数 (<c>fragment</c>)。<c>global</c> はどの関数でも、<c>constants</c> は関数の外でも使える。</param>
/// <param name="IsConstant">書き込めないか。</param>
internal sealed record GodotBuiltinVariable(string Name, string Type, string Function, bool IsConstant);

/// <summary>
/// Godot シェーダー言語の組み込みの変数・描画のモード・ステンシルのモードの表 (<c>GodotBuiltins.g.txt</c>)。
/// </summary>
/// <remarks>
/// 表は <c>tools/godot/builtins.mjs</c> が Godot のソース (<c>servers/rendering/shader_types.cpp</c>) から作り、アセンブリに埋め込む。
/// 組み込み関数は GLSL ES と同じなので、GLSL の表 (<see cref="GlslBuiltins"/>) から引く (<see cref="GodotTypeSystem"/>)。
/// </remarks>
internal sealed class GodotBuiltins
{
    /// <summary>埋め込んだ表の名前。</summary>
    private const string ResourceName = "Shaderlyn.Semantics.GodotBuiltins.txt";

    private static readonly Lazy<GodotBuiltins> Table = new(Load);

    private readonly FrozenDictionary<string, ImmutableArray<GodotBuiltinVariable>> _variables;
    private readonly FrozenDictionary<string, FrozenSet<string>> _renderModes;
    private readonly FrozenDictionary<string, FrozenSet<string>> _stencilModes;

    private GodotBuiltins(
        FrozenDictionary<string, ImmutableArray<GodotBuiltinVariable>> variables,
        FrozenDictionary<string, FrozenSet<string>> renderModes,
        FrozenDictionary<string, FrozenSet<string>> stencilModes)
    {
        _variables = variables;
        _renderModes = renderModes;
        _stencilModes = stencilModes;
    }

    /// <summary>表。初めて使うときに読む。</summary>
    public static GodotBuiltins Instance => Table.Value;

    /// <summary>表にあるシェーダーの種類 (<c>spatial</c> など)。</summary>
    public IEnumerable<string> ShaderTypes => _variables.Keys;

    /// <summary>そのシェーダーの種類の組み込みの変数。同じ名前が関数ごとに別の型で並ぶこともある。</summary>
    /// <param name="shaderType">シェーダーの種類。</param>
    /// <returns>組み込みの変数。知らない種類なら空。</returns>
    public ImmutableArray<GodotBuiltinVariable> GetVariables(string shaderType)
        => _variables.TryGetValue(shaderType, out ImmutableArray<GodotBuiltinVariable> variables) ? variables : [];

    /// <summary>そのシェーダーの種類の描画のモード (<c>render_mode</c> に書く名前)。</summary>
    /// <param name="shaderType">シェーダーの種類。</param>
    /// <returns>描画のモード。知らない種類なら空。</returns>
    public FrozenSet<string> GetRenderModes(string shaderType)
        => _renderModes.TryGetValue(shaderType, out FrozenSet<string>? modes) ? modes : FrozenSet<string>.Empty;

    /// <summary>そのシェーダーの種類のステンシルのモード (<c>stencil_mode</c> に書く名前)。</summary>
    /// <param name="shaderType">シェーダーの種類。</param>
    /// <returns>ステンシルのモード。知らない種類なら空。</returns>
    public FrozenSet<string> GetStencilModes(string shaderType)
        => _stencilModes.TryGetValue(shaderType, out FrozenSet<string>? modes) ? modes : FrozenSet<string>.Empty;

    /// <summary>埋め込んだ表を読む。</summary>
    /// <returns>表。</returns>
    private static GodotBuiltins Load()
    {
        using Stream stream = typeof(GodotBuiltins).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"埋め込んだ表 {ResourceName} がありません。");
        using StreamReader reader = new(stream);

        Dictionary<string, ImmutableArray<GodotBuiltinVariable>.Builder> variables = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> renderModes = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> stencilModes = new(StringComparer.Ordinal);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] fields = line.Split('\t');

            switch (fields[0])
            {
                case "V":
                    if (!variables.TryGetValue(fields[1], out ImmutableArray<GodotBuiltinVariable>.Builder? list))
                    {
                        variables[fields[1]] = list = ImmutableArray.CreateBuilder<GodotBuiltinVariable>();
                    }

                    list.Add(new GodotBuiltinVariable(fields[3], fields[4], fields[2], fields[5] == "const"));
                    break;

                case "R":
                    Add(renderModes, fields[1], fields[2]);
                    break;

                case "S":
                    Add(stencilModes, fields[1], fields[2]);
                    break;
            }
        }

        return new GodotBuiltins(
            variables.ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutable(), StringComparer.Ordinal),
            renderModes.ToFrozenDictionary(p => p.Key, p => p.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal),
            stencilModes.ToFrozenDictionary(p => p.Key, p => p.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal));

        static void Add(Dictionary<string, HashSet<string>> table, string shaderType, string mode)
        {
            if (!table.TryGetValue(shaderType, out HashSet<string>? modes))
            {
                table[shaderType] = modes = new HashSet<string>(StringComparer.Ordinal);
            }

            modes.Add(mode);
        }
    }
}
