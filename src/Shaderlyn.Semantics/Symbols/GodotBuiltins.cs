using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;

namespace Shaderlyn.Semantics.Symbols;

/// <summary>Godot の組み込みの変数 1 つ。</summary>
/// <param name="Name">名前 (<c>ALBEDO</c>)。</param>
/// <param name="Type">型 (<c>vec3</c>)。</param>
/// <param name="Function">使える関数 (<c>fragment</c>)。<c>global</c> はどの関数でも、<c>constants</c> は関数の外でも使える。</param>
/// <param name="IsConstant">書き込めないか。</param>
internal sealed record GodotBuiltinVariable(string Name, string Type, string Function, bool IsConstant);

/// <summary>Godot の組み込み関数の多重定義 1 つ (値の作成 <c>vec2(…)</c> を含む)。</summary>
/// <param name="Name">名前。</param>
/// <param name="ReturnType">戻り値の型。</param>
/// <param name="Parameters">仮引数の型。</param>
/// <param name="HighEnd">high-end の描画 (Forward+ と Mobile) でだけ使えるか。</param>
internal sealed record GodotBuiltinFunction(string Name, string ReturnType, ImmutableArray<string> Parameters, bool HighEnd);

/// <summary>Godot のステージ関数の中でだけ使える組み込み関数 (<c>canvas_item</c> の <c>texture_sdf</c> など)。</summary>
/// <param name="ShaderType">シェーダーの種類。</param>
/// <param name="StageFunction">使えるステージ関数 (<c>fragment</c> など)。</param>
/// <param name="Function">関数の形。</param>
internal sealed record GodotStageFunction(string ShaderType, string StageFunction, GodotBuiltinFunction Function);

/// <summary>Godot の組み込み関数の、定数でなければならない実引数。</summary>
/// <param name="Index">実引数の位置 (0 から)。</param>
/// <param name="Minimum">値の最小。</param>
/// <param name="Maximum">値の最大。</param>
internal sealed record GodotConstantArgument(int Index, int Minimum, int Maximum);

/// <summary>
/// Godot シェーダー言語の組み込みの変数・描画のモード・ステンシルのモードの表 (<c>GodotBuiltins.g.txt</c>)。
/// </summary>
/// <remarks>
/// 表は <c>tools/godot/builtins.mjs</c> が Godot のソース (<c>servers/rendering/shader_types.cpp</c>) から作り、アセンブリに埋め込む。
/// 組み込み関数も Godot の表 (<c>shader_language.cpp</c> の <c>builtin_func_defs</c> など) から作る。
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
        FrozenDictionary<string, FrozenSet<string>> stencilModes,
        FrozenDictionary<string, ImmutableArray<GodotBuiltinFunction>> functions,
        FrozenDictionary<string, ImmutableArray<int>> outArguments,
        FrozenDictionary<string, ImmutableArray<GodotConstantArgument>> constantArguments,
        FrozenSet<string> fragmentOnlyFunctions,
        ImmutableArray<GodotStageFunction> stageFunctions)
    {
        StageFunctions = stageFunctions;
        _variables = variables;
        _renderModes = renderModes;
        _stencilModes = stencilModes;
        Functions = functions;
        OutArguments = outArguments;
        ConstantArguments = constantArguments;
        FragmentOnlyFunctions = fragmentOnlyFunctions;
    }

    /// <summary>組み込み関数の多重定義を、名前ごとに並べたもの (値の作成 <c>vec2</c> なども含む)。</summary>
    public FrozenDictionary<string, ImmutableArray<GodotBuiltinFunction>> Functions { get; }

    /// <summary>組み込み関数の out の仮引数の位置 (<c>modf</c> の 1 など)。</summary>
    public FrozenDictionary<string, ImmutableArray<int>> OutArguments { get; }

    /// <summary>組み込み関数の、定数でなければならない実引数 (<c>textureGather</c> の 2 番目は 0 から 3)。</summary>
    public FrozenDictionary<string, ImmutableArray<GodotConstantArgument>> ConstantArguments { get; }

    /// <summary>ステージ関数の中でだけ使える組み込み関数 (<c>texture_sdf</c>、<c>emit_subparticle</c> など)。</summary>
    public ImmutableArray<GodotStageFunction> StageFunctions { get; }

    /// <summary>フラグメントと light の関数でだけ使える組み込み関数 (<c>dFdx</c> など)。</summary>
    public FrozenSet<string> FragmentOnlyFunctions { get; }

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
        Dictionary<string, ImmutableArray<GodotBuiltinFunction>.Builder> functions = new(StringComparer.Ordinal);
        Dictionary<string, ImmutableArray<int>.Builder> outArguments = new(StringComparer.Ordinal);
        Dictionary<string, ImmutableArray<GodotConstantArgument>.Builder> constantArguments = new(StringComparer.Ordinal);
        HashSet<string> fragmentOnly = new(StringComparer.Ordinal);
        ImmutableArray<GodotStageFunction>.Builder stageFunctions = ImmutableArray.CreateBuilder<GodotStageFunction>();

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

                case "B":
                    BuilderOf(functions, fields[1]).Add(new GodotBuiltinFunction(
                        fields[1],
                        fields[2],
                        fields[3].Length == 0 ? [] : [.. fields[3].Split(',')],
                        fields[4] == "high_end"));
                    break;

                case "O":
                    BuilderOf(outArguments, fields[1]).Add(int.Parse(fields[2], CultureInfo.InvariantCulture));
                    break;

                case "K":
                    BuilderOf(constantArguments, fields[1]).Add(new GodotConstantArgument(
                        int.Parse(fields[2], CultureInfo.InvariantCulture),
                        int.Parse(fields[3], CultureInfo.InvariantCulture),
                        int.Parse(fields[4], CultureInfo.InvariantCulture)));
                    break;

                case "G":
                    fragmentOnly.Add(fields[1]);
                    break;

                case "P":
                    stageFunctions.Add(new GodotStageFunction(fields[1], fields[2], new GodotBuiltinFunction(
                        fields[3],
                        fields[4],
                        fields[5].Length == 0 ? [] : [.. fields[5].Split(',')],
                        HighEnd: false)));
                    break;
            }
        }

        return new GodotBuiltins(
            variables.ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutable(), StringComparer.Ordinal),
            renderModes.ToFrozenDictionary(p => p.Key, p => p.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal),
            stencilModes.ToFrozenDictionary(p => p.Key, p => p.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal),
            functions.ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutable(), StringComparer.Ordinal),
            outArguments.ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutable(), StringComparer.Ordinal),
            constantArguments.ToFrozenDictionary(p => p.Key, p => p.Value.ToImmutable(), StringComparer.Ordinal),
            fragmentOnly.ToFrozenSet(StringComparer.Ordinal),
            stageFunctions.ToImmutable());

        static ImmutableArray<T>.Builder BuilderOf<T>(Dictionary<string, ImmutableArray<T>.Builder> table, string name)
        {
            if (!table.TryGetValue(name, out ImmutableArray<T>.Builder? builder))
            {
                table[name] = builder = ImmutableArray.CreateBuilder<T>();
            }

            return builder;
        }

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
